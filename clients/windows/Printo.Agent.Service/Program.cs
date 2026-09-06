using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
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

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IReadOnlyList<EffectiveSetting>>(sources);
        builder.Services.AddHostedService<AgentService>();

        builder.Logging.AddEventLog(settings =>
        {
            // The event log is where a domain admin looks first, and it is the only sink that
            // survives a machine nobody can log into.
            settings.SourceName = "Printo Agent";
        });

        if (!args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            builder.Services.AddWindowsService(options => options.ServiceName = "PrintoAgent");
        }

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}
