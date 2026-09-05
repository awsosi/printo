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
        var configPath = ResolveConfigPath(args);

        if (args.Contains("--write-default-config", StringComparer.OrdinalIgnoreCase))
        {
            new AgentConfiguration().Save(configPath);
            Console.WriteLine($"wrote default configuration to {configPath}");
            return 0;
        }

        AgentConfiguration configuration;
        try
        {
            configuration = AgentConfiguration.Load(configPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Refusing to start beats running on defaults: a workstation quietly printing
            // everything to the wrong place because someone mistyped a brace is far worse than
            // a service that does not come up and says why.
            Console.Error.WriteLine($"configuration at {configPath} could not be read: {error.Message}");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(configuration);
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

    /// <summary>
    /// Where the configuration lives.
    /// </summary>
    /// <remarks>
    /// ProgramData rather than the install directory: the MSI replaces the install directory on
    /// upgrade, and a site's printer mapping and watched folders must survive that.
    /// </remarks>
    private static string ResolveConfigPath(string[] args)
    {
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, "--config", StringComparison.OrdinalIgnoreCase));

        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Printo",
            "agent",
            "agent.json");
    }
}
