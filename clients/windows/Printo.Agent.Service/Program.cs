using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Printo.Agent.Ipp;
using Printo.Agent.Runtime;

namespace Printo.Agent.Service;

/// <summary>
/// Host for the Printo agent.
/// </summary>
/// <remarks>
/// The same executable runs as a Windows service and, with <c>--console</c>, in the foreground.
/// Keeping one binary means what an engineer debugs on a bench is exactly what the MSI installs
/// — the usual alternative, a separate console harness, drifts from the service and hides the
/// bugs that only appear under LocalSystem in session 0.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configPath = AgentConfiguration.ResolvePath(args);

        if (args.Contains("--write-default-config", StringComparer.OrdinalIgnoreCase))
        {
            new AgentConfiguration().Save(configPath);
            Console.WriteLine($"wrote default configuration to {configPath}");
            return 0;
        }

        AgentConfiguration fromFile;
        try
        {
            fromFile = AgentConfiguration.Load(configPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Refusing to start beats running on defaults: a workstation quietly printing
            // everything to the wrong place because someone mistyped a brace is far worse than
            // a service that does not come up and says why.
            Console.Error.WriteLine($"configuration at {configPath} could not be read: {error.Message}");
            return 2;
        }

        // Group Policy and the MSI's own properties override the file. Applied here rather than
        // inside `Load` so the file layer stays a plain, testable JSON read, and so `--config`
        // on a bench still describes exactly what the file says plus what policy imposes.
        var (configuration, sources) = PolicyConfiguration.Apply(fromFile, File.Exists(configPath));

        if (args.Contains("--show-config", StringComparer.OrdinalIgnoreCase))
        {
            // The answer to "what is this machine actually set to, and why", without needing a
            // remote session or a registry editor.
            Console.WriteLine($"configuration file: {configPath} ({(File.Exists(configPath) ? "present" : "absent")})");
            foreach (var setting in sources)
            {
                var managed = setting.IsManaged ? " (managed by Group Policy)" : string.Empty;
                Console.WriteLine($"  {setting.Name} = {setting.Value}   [from {setting.Source}]{managed}");
            }

            Console.WriteLine($"  Printers = {configuration.Printers.Count}, HotFolders = {configuration.HotFolders.Count}");
            return 0;
        }

        if (args.Contains("--remove-virtual-printer", StringComparer.OrdinalIgnoreCase))
        {
            // Run by the installer on uninstall, and by hand when someone wants the queue gone.
            // It is deliberately independent of the service: at uninstall time the service has
            // already been stopped, and a queue nobody removes is a printer that accepts jobs
            // into a socket that no longer exists.
            var removal = VirtualPrinterQueue.Remove(configuration.VirtualPrinter.PrinterName);
            Console.WriteLine($"{configuration.VirtualPrinter.PrinterName}: {removal}");
            return removal.Succeeded ? 0 : 1;
        }

        if (args.Contains("--install-virtual-printer", StringComparer.OrdinalIgnoreCase))
        {
            return await InstallVirtualPrinterAsync(configuration);
        }

        if (args.Contains("--diagnose-virtual-printer", StringComparer.OrdinalIgnoreCase))
        {
            // Everything Add-Printer -IppURL depends on, checked without changing anything: the
            // answer to "why is there no Printo printer on this machine", on that machine.
            var endpoint = $"http://127.0.0.1:{configuration.VirtualPrinter.Port}/ipp/print";
            Console.WriteLine($"{configuration.VirtualPrinter.PrinterName}: {VirtualPrinterQueue.Find(configuration.VirtualPrinter.PrinterName)}");
            foreach (var finding in VirtualPrinterQueue.Diagnose(configuration.VirtualPrinter.PrinterName, endpoint))
            {
                Console.WriteLine($"  - {finding}");
            }

            return 0;
        }

        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

        // The operational settings - thermal stock, waybills, log files, spool retention - over
        // the fleet policy the server last sent, which is on disk so a start during an outage
        // still has it. Built before the host so the log file is open for its first line.
        var state = new AgentSettingsState(
            configuration,
            sources,
            FleetPolicy.Load(Path.Combine(configuration.DataDirectory, "fleet-policy.json")));
        var fileLog = new RollingFileLog(() => state.Current.Logging, () => configuration.LogDirectory, "agent");

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IReadOnlyList<EffectiveSetting>>(sources);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(fileLog);
        builder.Services.AddSingleton(new AgentHostInfo(RunningAsService: !console));
        builder.Services.AddHostedService<AgentService>();

        builder.Logging.AddEventLog(settings =>
        {
            // The event log is where a domain admin looks first, and it is the only sink that
            // survives a machine nobody can log into.
            settings.SourceName = "Printo Agent";
        });

        // Local log files, when they are on. The host's floor comes down to Debug so the file can
        // have it when asked for; the file provider applies the agent's own level per line, the
        // event log keeps its own default, and the console stays at Information.
        builder.Logging.AddProvider(new FileLoggerProvider(fileLog));
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Debug.DebugLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventSource.EventSourceLoggerProvider>(null, LogLevel.Information);

        // The event log keeps what it always had - Information and up - and takes each job's
        // audit trail only when something went wrong. Thirty benches writing two lines per
        // document would bury the lines a domain administrator opens the event log to find.
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(null, LogLevel.Information);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(AgentService.JobCategory, LogLevel.Warning);

        if (!console)
        {
            builder.Services.AddWindowsService(options => options.ServiceName = "PrintoAgent");
        }

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Creates the Windows queue without starting the whole agent.
    /// </summary>
    /// <remarks>
    /// The service does this for itself at startup, so this exists for the two cases where that
    /// is not enough: proving the capture path on a bench without leaving a service running, and
    /// repairing a machine whose queue was removed while <c>ManageQueue</c> is switched off.
    ///
    /// The listener has to be up while the queue is created - <c>Add-Printer</c> reads the
    /// printer's capabilities before it will bind one - so this starts the real endpoint on the
    /// configured port, creates the queue against it, and stops. The queue survives; it points
    /// at the port the service will bind next time it starts.
    /// </remarks>
    private static async Task<int> InstallVirtualPrinterAsync(AgentConfiguration configuration)
    {
        var settings = configuration.VirtualPrinter;

        await using var server = new VirtualPrinterServer(
            new VirtualPrinterOptions { PrinterName = settings.PrinterName, Port = settings.Port },

            // Nothing should print through this short-lived endpoint: it exists to answer
            // Get-Printer-Attributes. Refusing rather than silently discarding means a job that
            // somehow arrives is reported as failed to whoever sent it.
            _ => CaptureResult.Reject("the agent service is not running on this endpoint"),
            (code, detail) => Console.WriteLine($"{code}: {detail}"));

        try
        {
            await server.StartAsync();
        }
        catch (Exception error)
            when (error is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"could not listen on port {settings.Port}: {error.Message}");
            return 2;
        }

        var result = VirtualPrinterQueue.Ensure(settings.PrinterName, server.EndpointUrl);
        Console.WriteLine($"{settings.PrinterName}: {result.Code}: {result.Detail}");
        foreach (var finding in result.Findings)
        {
            Console.WriteLine($"  - {finding}");
        }

        return result.Succeeded ? 0 : 1;
    }
}

/// <summary>How this process was started, for the parts of the agent that behave differently.</summary>
/// <param name="RunningAsService">
/// False under <c>--console</c>, where there is no service to set permissions on.
/// </param>
/// <param name="ControlPipeName">
/// The pipe the tray reaches the service on. Only a test hosting a second agent beside a real
/// one has a reason to change it.
/// </param>
public sealed record AgentHostInfo(bool RunningAsService, string ControlPipeName = AgentIpc.ServicePipeName);
