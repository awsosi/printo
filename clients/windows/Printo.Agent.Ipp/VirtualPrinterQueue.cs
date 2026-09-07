using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Printo.Agent.Ipp;

/// <summary>What a queue-management call did.</summary>
public sealed class QueueResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Machine-readable outcome: `present`, `created`, `recreated`, `removed`, `absent`, `failed`.</summary>
    public required string Code { get; init; }

    public string Detail { get; init; } = string.Empty;

    public override string ToString() =>
        string.IsNullOrEmpty(Detail) ? Code : $"{Code}: {Detail}";
}

/// <summary>
/// Creates and removes the Windows print queue that feeds the virtual printer.
/// </summary>
/// <remarks>
/// <para>
/// Through <c>Add-Printer -IppURL</c>, not through <c>AddPrinter</c> and <c>AddPort</c>
/// directly. That one cmdlet is what the M1 spike proved: it stages the inbox Microsoft IPP
/// Class Driver, creates the port itself, performs the nine Get-Printer-Attributes that bind
/// the queue's capabilities, and fails cleanly if the endpoint does not answer. Reimplementing
/// it against the spooler API would be new, unproven code on the single step that must not
/// fail, to save launching a process once per boot.
/// </para>
/// <para>
/// The service repairs the queue at startup rather than the installer creating it once. A
/// virtual printer somebody deleted, or that a profile roamed away, comes back by itself
/// within a poll instead of needing a repair install - and it means the queue is only ever
/// created while the endpoint behind it is actually listening, which is the condition
/// <c>Add-Printer</c> imposes anyway.
/// </para>
/// </remarks>
public static class VirtualPrinterQueue
{
    /// <summary>How long a queue operation may take before it is abandoned.</summary>
    /// <remarks>
    /// Generous: the first <c>Add-Printer</c> on a machine stages a driver package, which on a
    /// cold, busy workstation is tens of seconds. The call runs off the work loop, so waiting
    /// costs nothing but this thread.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Makes the queue exist and point at <paramref name="endpointUrl"/>.</summary>
    /// <remarks>
    /// Idempotent, and safe to call on every service start. A queue that already points at the
    /// right endpoint is left completely alone - recreating it would drop whatever the user has
    /// set as their default printer.
    /// </remarks>
    public static QueueResult Ensure(string printerName, string endpointUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        RequireWindows();

        var existing = Find(printerName);
        if (existing.Succeeded && existing.Code == "present")
        {
            if (PointsAt(existing.Detail, endpointUrl))
            {
                return new QueueResult { Succeeded = true, Code = "present", Detail = existing.Detail };
            }

            // The port is stale - the agent was reconfigured onto another port, or an older
            // build left a queue behind. Recreate rather than leave a printer that accepts jobs
            // and drops them into a socket nothing is listening on.
            var removal = Remove(printerName);
            if (!removal.Succeeded)
            {
                return removal;
            }

            var recreated = Create(printerName, endpointUrl);
            return recreated.Succeeded
                ? new QueueResult
                {
                    Succeeded = true,
                    Code = "recreated",
                    Detail = $"was {existing.Detail}, now {endpointUrl}",
                }
                : recreated;
        }

        return existing.Succeeded ? Create(printerName, endpointUrl) : existing;
    }

    /// <summary>Removes the queue and the port behind it, if they are there.</summary>
    public static QueueResult Remove(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        RequireWindows();

        // The port outlives the printer, and a leftover port blocks the next Add-Printer with a
        // name-in-use error. Both removals tolerate absence so this is safe to run twice, which
        // an uninstall custom action may well do.
        var script =
            """
            $ErrorActionPreference = 'Stop'
            $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction SilentlyContinue
            if (-not $printer) { 'absent'; exit 0 }
            $port = $printer.PortName
            Remove-Printer -Name $env:PRINTO_QUEUE
            if ($port) {
                try { Remove-PrinterPort -Name $port -ErrorAction Stop } catch { }
            }
            'removed'
            """;

        var (exitCode, output, error) = RunPowerShell(script, printerName);
        if (exitCode != 0)
        {
            return new QueueResult { Succeeded = false, Code = "failed", Detail = Describe(error, output) };
        }

        var code = output.Contains("absent", StringComparison.Ordinal) ? "absent" : "removed";
        return new QueueResult { Succeeded = true, Code = code, Detail = printerName };
    }

    /// <summary>Reports whether the queue exists, and the port it is bound to.</summary>
    public static QueueResult Find(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        RequireWindows();

        var script =
            """
            $ErrorActionPreference = 'Stop'
            $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction SilentlyContinue
            if (-not $printer) { 'absent'; exit 0 }
            'port=' + $printer.PortName
            """;

        var (exitCode, output, error) = RunPowerShell(script, printerName);
        if (exitCode != 0)
        {
            return new QueueResult { Succeeded = false, Code = "failed", Detail = Describe(error, output) };
        }

        var port = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("port=", StringComparison.Ordinal));

        return port is null
            ? new QueueResult { Succeeded = true, Code = "absent", Detail = printerName }
            : new QueueResult { Succeeded = true, Code = "present", Detail = port[5..] };
    }

    private static QueueResult Create(string printerName, string endpointUrl)
    {
        var script =
            """
            $ErrorActionPreference = 'Stop'
            Add-Printer -Name $env:PRINTO_QUEUE -IppURL $env:PRINTO_ENDPOINT
            $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction Stop
            'port=' + $printer.PortName
            """;

        var (exitCode, output, error) = RunPowerShell(script, printerName, endpointUrl);
        if (exitCode != 0)
        {
            return new QueueResult { Succeeded = false, Code = "failed", Detail = Describe(error, output) };
        }

        return new QueueResult { Succeeded = true, Code = "created", Detail = endpointUrl };
    }

    /// <summary>True when a port name refers to the endpoint we are listening on.</summary>
    /// <remarks>
    /// Windows names an IPP port after the URL it was created from, but not always character
    /// for character - a trailing slash appears and disappears between releases - so the
    /// comparison is on the parsed URL rather than the string.
    /// </remarks>
    internal static bool PointsAt(string portName, string endpointUrl)
    {
        if (string.Equals(portName, endpointUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(portName, UriKind.Absolute, out var port)
            || !Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint))
        {
            return false;
        }

        return port.Port == endpoint.Port
            && string.Equals(port.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                port.AbsolutePath.TrimEnd('/'),
                endpoint.AbsolutePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs a snippet of PowerShell with the queue name and endpoint passed as environment
    /// variables.
    /// </summary>
    /// <remarks>
    /// As environment variables rather than interpolated into the script, so a printer name
    /// containing a quote is a name and not an injection. The name is administrator-supplied
    /// and the endpoint is ours, but a command line assembled by string concatenation is a
    /// defect waiting for the first site that calls its queue <c>Printo (packing)</c>.
    /// </remarks>
    private static (int ExitCode, string Output, string Error) RunPowerShell(
        string script,
        string printerName,
        string? endpointUrl = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        start.Environment["PRINTO_QUEUE"] = printerName;
        if (endpointUrl is not null)
        {
            start.Environment["PRINTO_ENDPOINT"] = endpointUrl;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("powershell.exe could not be started");

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill; nothing to do.
            }

            return (
                -1,
                string.Empty,
                string.Create(CultureInfo.InvariantCulture, $"timed out after {Timeout.TotalSeconds:0} s"));
        }

        return (process.ExitCode, output.Result, error.Result);
    }

    private static string Describe(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        var line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return line ?? "powershell reported a failure with no message";
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows print queues can only be managed on Windows");
        }
    }
}
