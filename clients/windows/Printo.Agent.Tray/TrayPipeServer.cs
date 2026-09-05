using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// Listens for the service's questions and answers them in the user's session.
/// </summary>
/// <remarks>
/// The pipe's ACL grants LocalSystem — the service's identity — and the owning user, and
/// nobody else. It is the channel that decides what gets printed, so a second user on the same
/// machine must not be able to answer another's picker.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayPipeServer(int sessionId) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();

    private Task? loop;

    /// <summary>Starts listening. Returns immediately.</summary>
    public void Start()
    {
        loop = Task.Run(() => ListenAsync(cancellation.Token));
    }

    public void Dispose()
    {
        cancellation.Cancel();

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down; a cancelled listener is the expected outcome.
        }

        cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(
                    AgentIpc.TrayPipeName(sessionId),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 16 * 1024,
                    outBufferSize: 16 * 1024,
                    pipeSecurity: security);

                await pipe.WaitForConnectionAsync(token);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    continue;
                }

                var response = Handle(line);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentIpc.Json));

                // Let the service read the answer before the pipe instance goes away.
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The service went away mid-question; wait for the next connection.
            }
        }
    }

    private static TrayResponse Handle(string line)
    {
        TrayRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TrayRequest>(line, AgentIpc.Json);
        }
        catch (JsonException error)
        {
            return TrayResponse.Failure($"unreadable request: {error.Message}");
        }

        if (request is null)
        {
            return TrayResponse.Failure("empty request");
        }

        return request.Kind switch
        {
            TrayRequestKind.ShowPicker => ShowPicker(request),
            TrayRequestKind.ListPrinters => ListPrinters(),
            TrayRequestKind.Notify => new TrayResponse { Ok = true },
            _ => TrayResponse.Failure($"unsupported request {request.Kind}"),
        };
    }

    private static TrayResponse ShowPicker(TrayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PayloadPath) || !File.Exists(request.PayloadPath))
        {
            return TrayResponse.Failure($"spooled document not readable: {request.PayloadPath}");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var document = PdfDocument.Load(File.ReadAllBytes(request.PayloadPath));
            var thumbnails = PickerModel.RenderThumbnails(document, request.SuggestedThermalPages);
            var model = new PickerModel(thumbnails);

            var outcome = PickerForm.Ask(model, request.DocumentName ?? "document");
            stopwatch.Stop();

            return new TrayResponse
            {
                Ok = true,
                Resolution = outcome.Resolution == PickerResolution.AllA4 ? "allA4" : "print",
                ThermalPages = outcome.ThermalPages.OrderBy(page => page).ToList(),

                // Recorded for the fallback analytics: how long a person took is as much a
                // signal about a bad rule as which pages they chose.
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return TrayResponse.Failure(error.Message);
        }
    }

    private static TrayResponse ListPrinters()
    {
        try
        {
            // Enumerated here rather than in the service because a service in session 0 cannot
            // see per-user printer connections such as \\server\queue.
            return new TrayResponse { Ok = true, Printers = PrinterDiscovery.ListQueues() };
        }
        catch (InvalidOperationException error)
        {
            return TrayResponse.Failure(error.Message);
        }
    }
}
