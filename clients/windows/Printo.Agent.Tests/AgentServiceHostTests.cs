using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Printo.Agent.Core.Routing;
using Printo.Agent.Runtime;
using Printo.Agent.Service;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The real service, hosted as the service host hosts it, driven the way the tray drives it.
/// </summary>
/// <remarks>
/// Everything below it is unit-tested on its own; this is where they have to agree - the
/// control pipe answering with the real status, the garbage collector running at start, a
/// dropped document reaching the spool, "clear all jobs" clearing it, the settings a machine
/// chose reaching the log and the status, and the log file filling. Only the parts that need an
/// administrator - the Windows queue and the service's permissions - are left out, by the same
/// switches a site uses to leave them out.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AgentServiceHostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "printo-host-tests", Guid.NewGuid().ToString("N"));

    public AgentServiceHostTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Not a test failure.
        }
    }

    [Fact]
    public async Task TheServiceAnswersTheTrayCollectsItsSpoolAndClearsItsQueue()
    {
        var hot = Directory.CreateDirectory(Path.Combine(root, "hot")).FullName;
        var configuration = new AgentConfiguration
        {
            DataDirectory = Path.Combine(root, "data"),
            DecisionMode = DecisionMode.Local,
            VirtualPrinter = new VirtualPrinterSettings { Enabled = true, PrinterName = "PrintoHostTest", Port = FreePort(), ManageQueue = false },
            HotFolders = [new HotFolderSettings { Path = hot, PostAction = HotFolderPostAction.Leave, StabilitySeconds = 0.2 }],
            Logging = new LoggingSettings { FileEnabled = true, Level = AgentLogLevel.Debug },
            WaybillHandling = WaybillHandling.Skip,
            PollInterval = TimeSpan.FromMilliseconds(250),
        };

        // A job printed three days ago, whose document the collector should remove at start.
        Directory.CreateDirectory(configuration.SpoolDirectory);
        var old = Path.Combine(configuration.SpoolDirectory, "00000000deadbeef-old.pdf");
        await File.WriteAllBytesAsync(old, TestPdf.Build(TestPdf.A4Document()));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        using (var seed = new JobSpool(configuration.DatabasePath) { Clock = () => DateTimeOffset.UtcNow.AddDays(-3) })
        {
            var job = seed.Enqueue("seed", JobSource.HotFolder, "old.pdf", "deadbeef", old).Job;
            seed.ClaimNext("seed");
            seed.Complete(job.Id, "printed");
        }

        var pipe = $"printo-host-test-{Guid.NewGuid():n}";
        var state = new AgentSettingsState(configuration, [], fleet: null);
        using var fileLog = new RollingFileLog(() => state.Current.Logging, () => configuration.LogDirectory, "agent");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IReadOnlyList<EffectiveSetting>>([]);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(fileLog);
        builder.Services.AddSingleton(new AgentHostInfo(RunningAsService: false, ControlPipeName: pipe));
        builder.Services.AddHostedService<AgentService>();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(fileLog));
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var status = await Until(() => ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, pipeName: pipe)?.Status);
            Assert.True(status.VirtualPrinterListening);
            Assert.Equal("unmanaged", status.QueueState);
            Assert.Equal("not printed - this machine", status.WaybillHandling);
            Assert.StartsWith("100x210mm - product default", status.ThermalMedia, StringComparison.Ordinal);
            Assert.StartsWith("on, Debug", status.Logging, StringComparison.Ordinal);

            // The collector ran at start: the three-day-old document is gone, its job kept.
            Assert.False(File.Exists(old));
            Assert.Contains(status.RecentJobs, job => job.FileName == "old.pdf" && job.State == "Completed");

            // A document dropped in the watched folder is accepted and, with no printer mapped,
            // fails and waits to retry - which is what "clear all jobs" is for.
            await File.WriteAllBytesAsync(Path.Combine(hot, "invoice.pdf"), TestPdf.Build(TestPdf.A4Document()));
            await Until(() => ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, pipeName: pipe)?.Status is { Retrying: > 0 } s ? s : null);

            var cleared = ServiceControlClient.Send(
                new ServiceCommand { Kind = ServiceCommandKind.ClearQueue, Reason = "host test" },
                pipeName: pipe)!;
            Assert.True(cleared.Ok, cleared.Error);
            Assert.Contains("1 job(s) cancelled", cleared.Message, StringComparison.Ordinal);

            var after = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, pipeName: pipe)!.Status!;
            Assert.Equal(0, after.Pending + after.Retrying + after.Failed + after.AwaitingUser);
            Assert.Contains(after.RecentJobs, job => job.FileName == "invoice.pdf" && job.State == "Cancelled");

            var collected = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.CollectGarbage }, pipeName: pipe)!;
            Assert.True(collected.Ok, collected.Error);
        }
        finally
        {
            await host.StopAsync();
        }

        // The log file has the job's life in it, and the effective settings the agent started on.
        string log;
        await using (var stream = new FileStream(
            Directory.GetFiles(configuration.LogDirectory, "agent*.log").Single(),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            log = await reader.ReadToEndAsync();
        }

        Assert.Contains("Effective: waybills skip (from this machine)", log, StringComparison.Ordinal);
        Assert.Contains("[INF] Jobs: Job ", log, StringComparison.Ordinal);
        Assert.Contains("cleared: host test", log, StringComparison.Ordinal);
        Assert.Contains("Spool spool-collected", log, StringComparison.Ordinal);
    }

    private static async Task<T> Until<T>(Func<T?> probe)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found)
            {
                return found;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("the agent did not reach the expected state within 30 s");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
