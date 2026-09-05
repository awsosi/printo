using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using Printo.Agent.Runtime;
using Printo.Agent.Tray;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The service/tray channel.
/// </summary>
/// <remarks>
/// Exercised through <c>ListPrinters</c>, which is the one request that needs no human: it
/// proves the pipe, its ACL, the JSON framing and the dispatch, which is everything the picker
/// path depends on apart from the window itself. The window is covered separately by
/// <see cref="PickerTests"/> and measured by <c>Printo.Tray.exe --picker</c>.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIpcTests
{
    /// <summary>A session id nothing else is using, so tests never collide with a real tray.</summary>
    private static int TestSession() => 60000 + (Environment.ProcessId % 1000);

    private static TrayResponse? Send(int sessionId, TrayRequest request, int timeoutMs = 4000)
    {
        using var pipe = new NamedPipeClientStream(
            ".", AgentIpc.TrayPipeName(sessionId), PipeDirection.InOut, PipeOptions.None);

        pipe.Connect(timeoutMs);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        writer.WriteLine(JsonSerializer.Serialize(request, AgentIpc.Json));
        var line = reader.ReadLine();
        return line is null ? null : JsonSerializer.Deserialize<TrayResponse>(line, AgentIpc.Json);
    }

    [Fact]
    public void AnswersThePrinterQueryFromTheUserSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var session = TestSession();
        using var server = new TrayPipeServer(session);
        server.Start();

        var response = Send(session, new TrayRequest { Kind = TrayRequestKind.ListPrinters });

        Assert.NotNull(response);
        Assert.True(response!.Ok, response.Error);

        // A Windows install always has at least one queue; this is what the service cannot see
        // for itself from session 0.
        Assert.NotEmpty(response.Printers);
    }

    [Fact]
    public void ReportsAnUnreadableRequestRatherThanDroppingTheConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var session = TestSession() + 1;
        using var server = new TrayPipeServer(session);
        server.Start();

        using var pipe = new NamedPipeClientStream(
            ".", AgentIpc.TrayPipeName(session), PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(4000);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        writer.WriteLine("{ not json");
        var line = reader.ReadLine();

        Assert.NotNull(line);
        var response = JsonSerializer.Deserialize<TrayResponse>(line!, AgentIpc.Json);
        Assert.False(response!.Ok);
        Assert.Contains("unreadable", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAPickerRequestWhoseDocumentIsGone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var session = TestSession() + 2;
        using var server = new TrayPipeServer(session);
        server.Start();

        var response = Send(session, new TrayRequest
        {
            Kind = TrayRequestKind.ShowPicker,
            JobId = 1,
            DocumentName = "gone.pdf",
            PayloadPath = Path.Combine(Path.GetTempPath(), $"printo-missing-{Guid.NewGuid():N}.pdf"),
        });

        // Reported rather than shown as an empty picker: the job stays parked and the spool
        // records why, instead of a user being asked about a document nobody can render.
        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Contains("not readable", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePrompterReportsNoAnswerWhenNoTrayIsListening()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // No signed-in user, or no tray running: the job must stay parked rather than be
        // guessed at or dropped.
        var prompter = new TrayPrompter(() => [999_001], TimeSpan.FromMilliseconds(300));

        var answer = prompter.Ask(
            new SpoolJob
            {
                JobKey = "k",
                FileName = "doc.pdf",
                DocumentSha256 = "sha",
                PayloadPath = "nowhere.pdf",
            },
            new FallbackPrompt
            {
                ReasonCode = "LOW_CONFIDENCE",
                Message = "test",
                TraceJson = "{}",
            });

        Assert.Null(answer);
    }
}
