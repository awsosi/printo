using System.IO.Pipes;
using System.Text.Json;

namespace Printo.Agent.Runtime;

/// <summary>
/// Asks the tray to show the picker, over the service/tray pipe.
/// </summary>
/// <remarks>
/// The service cannot show UI from session 0, so the question is handed to the tray running in
/// the user's session. Everything here is failure-tolerant on purpose: no signed-in user, no
/// tray running, or a tray that never answers all mean "nobody decided", and the job stays
/// parked in the spool rather than being guessed at or dropped.
/// </remarks>
public sealed class TrayPrompter(Func<IReadOnlyList<int>> sessionIds, TimeSpan? connectTimeout = null)
    : IFallbackPrompter
{
    private readonly Func<IReadOnlyList<int>> sessionIds =
        sessionIds ?? throw new ArgumentNullException(nameof(sessionIds));

    /// <summary>
    /// How long to wait for a tray to accept the connection.
    /// </summary>
    /// <remarks>
    /// Short: either a tray is listening or it is not. The wait for the *user* to answer is
    /// unbounded by design — the job waits rather than printing something wrong.
    /// </remarks>
    private readonly TimeSpan connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);

    /// <summary>Sessions that answered, most recent first. Exposed for the tray's status view.</summary>
    public IReadOnlyList<int> LastAttemptedSessions { get; private set; } = [];

    public IReadOnlySet<int>? Ask(SpoolJob job, FallbackPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(prompt);

        var sessions = sessionIds();
        LastAttemptedSessions = sessions;

        var request = new TrayRequest
        {
            Kind = TrayRequestKind.ShowPicker,
            JobId = job.Id,
            DocumentName = job.FileName,
            PayloadPath = job.PayloadPath,
            ReasonCode = prompt.ReasonCode,
            Message = prompt.Message,
            SuggestedThermalPages = prompt.SuggestedThermalPages,
        };

        foreach (var session in sessions)
        {
            var response = TryAsk(session, request);
            if (response is { Ok: true })
            {
                return response.ThermalPages.ToHashSet();
            }
        }

        return null;
    }

    private TrayResponse? TryAsk(int sessionId, TrayRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", AgentIpc.TrayPipeName(sessionId), PipeDirection.InOut, PipeOptions.None);

            pipe.Connect((int)connectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            writer.WriteLine(JsonSerializer.Serialize(request, AgentIpc.Json));

            // No read timeout: the user is being asked a question and may take as long as they
            // take. A timeout here would print something wrong to save a few seconds.
            var line = reader.ReadLine();
            return line is null ? null : JsonSerializer.Deserialize<TrayResponse>(line, AgentIpc.Json);
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException or JsonException)
        {
            // No tray in that session, or it went away mid-question. Try the next one.
            return null;
        }
    }
}
