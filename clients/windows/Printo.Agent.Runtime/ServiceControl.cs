using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printo.Agent.Runtime;

/// <summary>What the tray can ask the running service.</summary>
public enum ServiceCommandKind
{
    /// <summary>Report what the agent is doing right now.</summary>
    Status,

    /// <summary>Cancel every unfinished job, remove their documents, and empty the Windows queue.</summary>
    ClearQueue,

    /// <summary>Check the Windows queue against the endpoint now, and recreate it if it is wrong.</summary>
    RepairVirtualPrinter,

    /// <summary>Run the spool garbage collector now rather than at its next hour.</summary>
    CollectGarbage,
}

/// <summary>A request from the tray to the service.</summary>
public sealed class ServiceCommand
{
    public ServiceCommandKind Kind { get; init; }

    /// <summary>Recorded on anything the command changes, e.g. "cleared from the tray by DOMAIN\user".</summary>
    public string? Reason { get; init; }
}

/// <summary>The service's answer.</summary>
public sealed class ServiceReply
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>What was done, in words fit for a message box.</summary>
    public string? Message { get; init; }

    public AgentStatusSnapshot? Status { get; init; }

    public static ServiceReply Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>One job, as the status window lists it.</summary>
public sealed class JobSummary
{
    public long Id { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public int Pages { get; init; }

    public string? UserName { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? Error { get; init; }

    public static JobSummary From(SpoolJob job) => new()
    {
        Id = job.Id,
        FileName = job.FileName,
        State = job.State.ToString(),
        Source = job.Source.ToString(),
        Pages = job.PageCount,
        UserName = job.UserName,
        UpdatedAt = job.UpdatedAt,
        Error = job.Error,
    };
}

/// <summary>
/// Everything the status window shows, gathered by the service in one go.
/// </summary>
/// <remarks>
/// Asked of the service rather than pieced together by the tray, because the tray cannot see
/// most of it: whether the endpoint is bound, what the last attempt to create the Windows queue
/// said and why, which policy the server sent. A status window that guessed those from the
/// configuration file would be wrong exactly when it is needed.
/// </remarks>
public sealed class AgentStatusSnapshot
{
    public string AgentVersion { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset At { get; init; }

    public string DecisionMode { get; init; } = string.Empty;

    public string? ServerUrl { get; init; }

    public string? ServerOutcome { get; init; }

    public DateTimeOffset? ServerContactAt { get; init; }

    public long? BundleVersion { get; init; }

    public bool VirtualPrinterEnabled { get; init; }

    public string VirtualPrinterName { get; init; } = string.Empty;

    public string? VirtualPrinterEndpoint { get; init; }

    public bool VirtualPrinterListening { get; init; }

    /// <summary><c>present</c>, <c>created</c>, <c>failed</c>, ... as the last queue check reported it.</summary>
    public string? QueueState { get; init; }

    public string? QueueDetail { get; init; }

    public DateTimeOffset? QueueCheckedAt { get; init; }

    public bool OcrAvailable { get; init; }

    public string? OcrLanguage { get; init; }

    public string WaybillHandling { get; init; } = string.Empty;

    public string ThermalMedia { get; init; } = string.Empty;

    public string Logging { get; init; } = string.Empty;

    public string Retention { get; init; } = string.Empty;

    public string? LogDirectory { get; init; }

    public int Pending { get; init; }

    public int Printing { get; init; }

    public int AwaitingUser { get; init; }

    public int Retrying { get; init; }

    public int Failed { get; init; }

    public long SpoolBytes { get; init; }

    public string? LastCollection { get; init; }

    public DateTimeOffset? LastCollectionAt { get; init; }

    public IReadOnlyList<JobSummary> RecentJobs { get; init; } = [];
}

/// <summary>The service/tray control protocol's wire format.</summary>
public static class ServiceControlProtocol
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>
/// The service's end of the control pipe.
/// </summary>
/// <remarks>
/// <para>
/// One JSON request per connection, one JSON reply, like the tray's own pipe. The service owns
/// the spool, the endpoint and the Windows queue, and runs as LocalSystem, so anything that has
/// to touch those on an operator's behalf - emptying the Windows queue needs rights an operator
/// does not have - is asked for here rather than attempted from the tray.
/// </para>
/// <para>
/// Open to interactive users, on purpose: every command here is something the person at the
/// desk is meant to be able to do from the tray, and the worst any of them does is cancel
/// documents that person could equally cancel from the Windows print queue. Nothing here
/// changes configuration or runs code of the caller's choosing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceControlServer(Func<ServiceCommand, ServiceReply> handler, string? pipeName = null) : IDisposable
{
    private readonly Func<ServiceCommand, ServiceReply> handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    private readonly string pipeName = pipeName ?? AgentIpc.ServicePipeName;

    private readonly CancellationTokenSource cancellation = new();

    private readonly ManualResetEventSlim listening = new(false);

    private Task? loop;

    /// <summary>Starts listening, and returns once the pipe exists.</summary>
    public void Start()
    {
        loop = Task.Factory.StartNew(
            () => ListenAsync(cancellation.Token),
            cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        listening.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        cancellation.Cancel();

        var stopped = true;
        try
        {
            stopped = loop?.Wait(TimeSpan.FromSeconds(2)) ?? true;
        }
        catch (AggregateException)
        {
            // Shutting down; a cancelled listener is the expected outcome.
        }

        cancellation.Dispose();
        if (stopped)
        {
            listening.Dispose();
        }
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // Whoever runs the listener, which is LocalSystem in production and the developer when
        // the service runs with --console or under test.
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var security = Security();

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 16 * 1024,
                    outBufferSize: 64 * 1024,
                    pipeSecurity: security);

                listening.Set();
                await pipe.WaitForConnectionAsync(token);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    continue;
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(Handle(line), ServiceControlProtocol.Json));
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The tray went away mid-request; wait for the next one.
            }
            catch (UnauthorizedAccessException)
            {
                // Another process holds the name. Back off rather than spin; the service keeps
                // printing without its control channel.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private ServiceReply Handle(string line)
    {
        ServiceCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ServiceCommand>(line, ServiceControlProtocol.Json);
        }
        catch (JsonException error)
        {
            return ServiceReply.Failure($"unreadable request: {error.Message}");
        }

        if (command is null)
        {
            return ServiceReply.Failure("empty request");
        }

        try
        {
            return handler(command);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Whatever went wrong is reported to the tray rather than taking the listener down:
            // the control channel is how somebody finds out what is wrong.
            return ServiceReply.Failure($"{error.GetType().Name}: {error.Message}");
        }
    }
}

/// <summary>The tray's end of the control pipe.</summary>
public static class ServiceControlClient
{
    /// <summary>
    /// Sends one command, or returns <c>null</c> when no service is listening.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because "the service is not running" is an ordinary answer
    /// the status window has to show, not a failure of the window.
    /// </remarks>
    public static ServiceReply? Send(ServiceCommand command, TimeSpan? timeout = null, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var limit = timeout ?? TimeSpan.FromSeconds(3);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName ?? AgentIpc.ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)limit.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            writer.WriteLine(JsonSerializer.Serialize(command, ServiceControlProtocol.Json));

            // Clearing a queue can take a few seconds; the read is bounded all the same, because a
            // service that accepted the connection and then hung must not hang the tray with it.
            var read = reader.ReadLineAsync();
            var patience = command.Kind == ServiceCommandKind.Status ? limit : limit + TimeSpan.FromSeconds(60);
            if (!read.Wait(patience))
            {
                return ServiceReply.Failure("the agent service did not answer in time");
            }

            return read.Result is { } line
                ? JsonSerializer.Deserialize<ServiceReply>(line, ServiceControlProtocol.Json)
                : null;
        }
        catch (Exception error) when (
            error is TimeoutException or IOException or UnauthorizedAccessException or JsonException
                or AggregateException)
        {
            return null;
        }
    }
}
