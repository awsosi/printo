using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Printo.Agent.Ipp;

/// <summary>What a queue-management call did.</summary>
public sealed class QueueResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Machine-readable outcome: `present`, `created`, `recreated`, `removed`, `absent`, `failed`.</summary>
    public required string Code { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>What was found on the way, most useful first, when something went wrong.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    public override string ToString() =>
        (string.IsNullOrEmpty(Detail) ? Code : $"{Code}: {Detail}")
        + (Findings.Count == 0 ? string.Empty : " [" + string.Join("; ", Findings) + "]");
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
/// <para>
/// <b>How a failure is reported matters as much as the attempt.</b> One workstation logged only
/// <c>The Printo queue could not be created: #&lt; CLIXML</c>. That is PowerShell's header for
/// errors it serialises when started with <c>-EncodedCommand</c> and its error stream
/// redirected, and the agent kept the first line of it - so the real error, the one line that
/// would have said what was wrong, was discarded on every machine it happened on. The scripts
/// now run with plain <c>-Command</c>, which writes errors as text; they catch their own
/// failures and print the message, exception type, HRESULT and error id as named lines; they
/// check the usual causes first and say which one it is; and CLIXML, should it ever appear
/// again, is decoded rather than quoted.
/// </para>
/// <para>
/// Encoded commands had a second cost: base64-encoded PowerShell launched by a service is one of
/// the things endpoint protection is configured to block outright, as an obfuscation technique.
/// A plain <c>-Command</c> is not obfuscated and says what it does in the process list.
/// </para>
/// </remarks>
public static partial class VirtualPrinterQueue
{
    /// <summary>How long a queue operation may take before it is abandoned.</summary>
    /// <remarks>
    /// Generous: the first <c>Add-Printer</c> on a machine stages a driver package, which on a
    /// cold, busy workstation is tens of seconds. The call runs off the work loop, so waiting
    /// costs nothing but this thread.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>The inbox driver <c>Add-Printer -IppURL</c> binds.</summary>
    public const string IppClassDriver = "Microsoft IPP Class Driver";

    /// <summary>
    /// Common to every script: stop on errors, no progress records, and a catch that prints the
    /// failure as named lines the agent can parse, instead of a formatted error record.
    /// </summary>
    private const string Prologue =
        """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        function Report-Failure($record) {
            $e = $record.Exception
            'error=' + ($e.Message -replace '\s+', ' ').Trim()
            'type=' + $e.GetType().FullName
            if ($e.HResult) { 'hresult=0x' + $e.HResult.ToString('X8') }
            if ($record.FullyQualifiedErrorId) { 'errorid=' + $record.FullyQualifiedErrorId }
            if ($record.CategoryInfo) { 'category=' + $record.CategoryInfo.Category }
        }
        """;

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
        var script = Prologue +
            """

            try {
                $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction SilentlyContinue
                if (-not $printer) { 'absent'; exit 0 }
                $port = $printer.PortName
                Remove-Printer -Name $env:PRINTO_QUEUE
                if ($port) {
                    try { Remove-PrinterPort -Name $port -ErrorAction Stop } catch { }
                }
                'removed'
            } catch { Report-Failure $_; exit 1 }
            """;

        var run = RunPowerShell(script, printerName);
        if (run.ExitCode != 0)
        {
            return Failed(run, "removing the queue");
        }

        var code = run.Output.Contains("absent", StringComparison.Ordinal) ? "absent" : "removed";
        return new QueueResult { Succeeded = true, Code = code, Detail = printerName };
    }

    /// <summary>
    /// Deletes every document waiting in the Windows queue, whoever printed it.
    /// </summary>
    /// <remarks>
    /// Part of clearing the agent: documents printed while the endpoint was down wait in the
    /// Windows queue, not the spool, and would otherwise arrive the moment the agent is back -
    /// which is not what somebody who pressed "clear" expects. Needs rights on other users' jobs,
    /// which is why the service does it and the tray asks.
    /// </remarks>
    public static QueueResult Purge(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        RequireWindows();

        var script = Prologue +
            """

            try {
                $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction SilentlyContinue
                if (-not $printer) { 'absent'; exit 0 }
                $jobs = @(Get-PrintJob -PrinterName $env:PRINTO_QUEUE -ErrorAction SilentlyContinue)
                foreach ($job in $jobs) { Remove-PrintJob -InputObject $job -ErrorAction SilentlyContinue }
                'removed=' + $jobs.Count
            } catch { Report-Failure $_; exit 1 }
            """;

        var run = RunPowerShell(script, printerName);
        if (run.ExitCode != 0)
        {
            return Failed(run, "emptying the Windows queue");
        }

        return Field(run.Output, "removed") is { } count
            ? new QueueResult { Succeeded = true, Code = "purged", Detail = $"{count} document(s) removed" }
            : new QueueResult { Succeeded = true, Code = "absent", Detail = printerName };
    }

    /// <summary>Reports whether the queue exists, and the port it is bound to.</summary>
    public static QueueResult Find(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        RequireWindows();

        var script = Prologue +
            """

            try {
                $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction SilentlyContinue
                if (-not $printer) { 'absent'; exit 0 }
                'port=' + $printer.PortName
            } catch { Report-Failure $_; exit 1 }
            """;

        var run = RunPowerShell(script, printerName);
        if (run.ExitCode != 0)
        {
            return Failed(run, "looking for the queue");
        }

        var port = Field(run.Output, "port");
        return port is null
            ? new QueueResult { Succeeded = true, Code = "absent", Detail = printerName }
            : new QueueResult { Succeeded = true, Code = "present", Detail = port };
    }

    /// <summary>
    /// Checks everything <c>Add-Printer -IppURL</c> depends on, without changing anything.
    /// </summary>
    /// <remarks>
    /// What <c>Printo.Agent.exe --diagnose-virtual-printer</c> prints and what the status window
    /// shows beside a failed queue, so the question "why is there no Printo printer" has an
    /// answer on the machine where it is asked.
    /// </remarks>
    public static IReadOnlyList<string> Diagnose(string printerName, string? endpointUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        RequireWindows();

        var findings = new List<string>();
        var run = RunPowerShell(Prologue + "\n" + PreflightScript(repair: false), printerName, endpointUrl);
        findings.AddRange(Preflight(run));

        if (run.ExitCode == -1 && run.Error.Length > 0)
        {
            findings.Add(run.Error);
        }

        if (endpointUrl is not null)
        {
            findings.Add(EndpointAnswers(endpointUrl)
                ? $"the endpoint {endpointUrl} answers"
                : $"the endpoint {endpointUrl} does not answer - is the agent service running?");
        }

        findings.Add(WinHttpProxy());
        return findings;
    }

    private static QueueResult Create(string printerName, string endpointUrl)
    {
        // Preflight and create in one process: each check below is a known way for Add-Printer
        // to fail with a message that names none of them, and two of them are repaired here
        // rather than only reported.
        var script = Prologue + "\n" + PreflightScript(repair: true) +
            """

            if ($blocked) { exit 3 }
            try {
                Add-Printer -Name $env:PRINTO_QUEUE -IppURL $env:PRINTO_ENDPOINT
            } catch {
                # Once more after a pause. The first Add-Printer after boot stages the class
                # driver, and on a cold machine that can lose a race with the spooler.
                $first = $_
                Start-Sleep -Seconds 5
                try {
                    Add-Printer -Name $env:PRINTO_QUEUE -IppURL $env:PRINTO_ENDPOINT
                    'retried=after ' + ($first.Exception.Message -replace '\s+', ' ').Trim()
                } catch { Report-Failure $_; exit 1 }
            }
            try {
                $printer = Get-Printer -Name $env:PRINTO_QUEUE -ErrorAction Stop
                'port=' + $printer.PortName
            } catch { Report-Failure $_; exit 1 }
            """;

        var run = RunPowerShell(script, printerName, endpointUrl);
        if (run.ExitCode != 0)
        {
            var failed = Failed(run, "creating the queue");
            if (run.ExitCode == 3)
            {
                return failed;
            }

            // An Add-Printer failure that names nothing is most often the spooler failing to
            // reach the endpoint, and the proxy and the endpoint are the two things to check.
            return new QueueResult
            {
                Succeeded = false,
                Code = failed.Code,
                Detail = failed.Detail,
                Findings = [.. failed.Findings, EndpointAnswers(endpointUrl)
                    ? "the endpoint answers locally"
                    : "the endpoint does not answer locally", WinHttpProxy()],
            };
        }

        var retried = Field(run.Output, "retried");
        return new QueueResult
        {
            Succeeded = true,
            Code = "created",
            Detail = endpointUrl,
            Findings = [.. Preflight(run), .. retried is null ? Array.Empty<string>() : [$"created on the second attempt, {retried}"]],
        };
    }

    /// <summary>
    /// The checks that come before <c>Add-Printer</c>, as PowerShell that sets <c>$blocked</c> and
    /// prints one <c>finding=</c> line per observation.
    /// </summary>
    /// <param name="repair">
    /// Start a stopped spooler, install the class driver from the driver store, and remove a
    /// port left over from a queue that no longer exists. Diagnosis only looks.
    /// </param>
    private static string PreflightScript(bool repair) =>
        """
        $blocked = $false
        $repair = $env:PRINTO_REPAIR -eq '1'

        # The Print Spooler. Hardening guides written after PrintNightmare disable it outright,
        # and nothing can create or use any printer while it is off.
        $spooler = Get-Service -Name Spooler -ErrorAction SilentlyContinue
        if (-not $spooler) {
            'finding=the Print Spooler service does not exist on this machine'; $blocked = $true
        } elseif ($spooler.Status -ne 'Running') {
            $start = (Get-CimInstance -ClassName Win32_Service -Filter "Name='Spooler'" -ErrorAction SilentlyContinue).StartMode
            if ($start -eq 'Disabled') {
                'finding=the Print Spooler service is disabled (a common hardening setting); the virtual printer needs it - set it to Automatic and start it, or deliver Printo by watched folders only'
                $blocked = $true
            } elseif ($repair) {
                try { Start-Service -Name Spooler -ErrorAction Stop; 'finding=the Print Spooler service was stopped and has been started' }
                catch { 'finding=the Print Spooler service is stopped and would not start: ' + $_.Exception.Message; $blocked = $true }
            } else {
                'finding=the Print Spooler service is ' + $spooler.Status
            }
        } else {
            'finding=the Print Spooler service is running'
        }

        # The cmdlets themselves. Missing on stripped-down images and on Server Core without
        # the print feature.
        if (-not (Get-Command -Name Add-Printer -ErrorAction SilentlyContinue)) {
            'finding=the PrintManagement PowerShell module is not available, so Add-Printer cannot run'
            $blocked = $true
        }

        'finding=PowerShell ' + $PSVersionTable.PSVersion + ', ' + $ExecutionContext.SessionState.LanguageMode + ' language mode'

        if (-not $blocked) {
            # The inbox IPP class driver, which Add-Printer -IppURL binds. Present on every
            # supported Windows, but installable from the driver store if something removed it,
            # and blockable by a driver-installation policy - which is worth naming when it is.
            $driver = Get-PrinterDriver -Name $env:PRINTO_DRIVER -ErrorAction SilentlyContinue
            if ($driver) {
                'finding=the ' + $env:PRINTO_DRIVER + ' is installed'
            } elseif ($repair) {
                try { Add-PrinterDriver -Name $env:PRINTO_DRIVER -ErrorAction Stop; 'finding=the ' + $env:PRINTO_DRIVER + ' was missing and has been installed from the driver store' }
                catch { 'finding=the ' + $env:PRINTO_DRIVER + ' is missing and could not be installed: ' + ($_.Exception.Message -replace '\s+', ' ').Trim() }
            } else {
                'finding=the ' + $env:PRINTO_DRIVER + ' is not installed yet (Add-Printer installs it from the driver store)'
            }

            # A queue of the same name bound to something else, or a port left behind by one
            # that was deleted: either makes Add-Printer fail with "already exists".
            if ($env:PRINTO_ENDPOINT) {
                $ports = Get-PrinterPort -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ('*' + ([Uri]$env:PRINTO_ENDPOINT).Authority + '*') }
                foreach ($port in $ports) {
                    $users = Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.PortName -eq $port.Name }
                    if (-not $users) {
                        if ($repair) {
                            try { Remove-PrinterPort -Name $port.Name -ErrorAction Stop; 'finding=removed the orphaned port ' + $port.Name }
                            catch { 'finding=an orphaned port ' + $port.Name + ' could not be removed: ' + $_.Exception.Message }
                        } else {
                            'finding=an orphaned port ' + $port.Name + ' is left from an earlier queue'
                        }
                    }
                }
            }
        }
        """.Replace("$env:PRINTO_REPAIR -eq '1'", repair ? "$true" : "$false", StringComparison.Ordinal);

    private static QueueResult Failed((int ExitCode, string Output, string Error) run, string what)
    {
        var findings = Preflight(run).ToList();
        var error = Field(run.Output, "error");
        var details = new[] { "type", "hresult", "errorid" }
            .Select(name => Field(run.Output, name) is { } value ? $"{name} {value}" : null)
            .OfType<string>()
            .ToList();

        string detail;
        if (run.ExitCode == 3)
        {
            // The preflight found a reason Add-Printer cannot work; the findings are the detail.
            detail = findings.FirstOrDefault(finding => !finding.StartsWith("PowerShell ", StringComparison.Ordinal))
                ?? "a prerequisite of the virtual printer is missing";
        }
        else if (error is not null)
        {
            detail = details.Count == 0 ? error : $"{error} ({string.Join(", ", details)})";
        }
        else
        {
            detail = Describe(run.Error, run.Output);
        }

        return new QueueResult
        {
            Succeeded = false,
            Code = "failed",
            Detail = $"{what}: {detail}",
            Findings = findings,
        };
    }

    private static IEnumerable<string> Preflight((int ExitCode, string Output, string Error) run) =>
        Lines(run.Output)
            .Where(line => line.StartsWith("finding=", StringComparison.Ordinal))
            .Select(line => line["finding=".Length..]);

    private static string? Field(string output, string name) =>
        Lines(output)
            .Where(line => line.StartsWith(name + "=", StringComparison.Ordinal))
            .Select(line => line[(name.Length + 1)..])
            .FirstOrDefault();

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when the endpoint answers an HTTP GET on loopback.</summary>
    private static bool EndpointAnswers(string endpointUrl)
    {
        try
        {
            var root = new Uri(endpointUrl).GetLeftPart(UriPartial.Authority) + "/";
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = client.GetAsync(root).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The machine-wide WinHTTP proxy, which the spooler's IPP client uses.
    /// </summary>
    /// <remarks>
    /// Named because it is the one cause of an Add-Printer failure that nothing on the machine
    /// will point at: a proxy with no bypass for loopback sends the spooler's requests for
    /// 127.0.0.1 to the proxy, and the queue cannot read the printer's capabilities.
    /// </remarks>
    private static string WinHttpProxy()
    {
        var run = Run(
            Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            ["winhttp", "show", "proxy"],
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(15));

        if (run.ExitCode != 0)
        {
            return "the WinHTTP proxy could not be read";
        }

        // Localised output, so the only thing read is whether an address and a bypass list
        // appear - a direct connection prints neither.
        var server = ProxyServer().Match(run.Output);
        if (!server.Success)
        {
            return "WinHTTP uses a direct connection (no machine-wide proxy)";
        }

        var bypass = ProxyBypass().Match(run.Output);
        var list = bypass.Success ? bypass.Groups[1].Value.Trim() : string.Empty;
        return list.Contains("<local>", StringComparison.OrdinalIgnoreCase) || list.Contains("127.0.0.1", StringComparison.Ordinal)
            ? $"WinHTTP proxy {server.Groups[1].Value.Trim()} bypasses local addresses"
            : $"WinHTTP proxy {server.Groups[1].Value.Trim()} with no bypass for 127.0.0.1 - the spooler may be sending the virtual printer's traffic to the proxy; add <local> to its bypass list";
    }

    [GeneratedRegex(@":\s*([A-Za-z0-9.\-]+:\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProxyServer();

    [GeneratedRegex(@"\n[^\n:]*:\s*((?:<local>|[\w\-.*]+)(?:;[^\n]*)?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ProxyBypass();

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
    ///
    /// Windows PowerShell by its full path: a service's search path is whatever the machine's
    /// is, and a <c>powershell.exe</c> earlier on it is not one to run as LocalSystem.
    /// </remarks>
    private static (int ExitCode, string Output, string Error) RunPowerShell(
        string script,
        string printerName,
        string? endpointUrl = null)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            powershell = "powershell.exe";
        }

        var environment = new Dictionary<string, string>
        {
            ["PRINTO_QUEUE"] = printerName,
            ["PRINTO_DRIVER"] = IppClassDriver,
        };

        if (endpointUrl is not null)
        {
            environment["PRINTO_ENDPOINT"] = endpointUrl;
        }

        return Run(
            powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            environment,
            Timeout);
    }

    private static (int ExitCode, string Output, string Error) Run(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            // Blocked by application control, most likely: say so, with the path, because
            // "the queue could not be created" is not something an administrator can act on.
            return (-1, string.Empty, $"{Path.GetFileName(fileName)} could not be started ({error.Message}) - is it blocked by AppLocker, WDAC or endpoint protection?");
        }

        if (process is null)
        {
            return (-1, string.Empty, $"{Path.GetFileName(fileName)} could not be started");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
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
                    string.Create(CultureInfo.InvariantCulture, $"timed out after {timeout.TotalSeconds:0} s"));
            }

            return (process.ExitCode, output.Result, error.Result);
        }
    }

    /// <summary>
    /// The first meaningful line of a failure with no structured error in it.
    /// </summary>
    /// <remarks>
    /// Serialised error records are decoded rather than quoted. The line that used to reach the
    /// event log was the serialiser's header, <c>#&lt; CLIXML</c>, which says nothing at all.
    /// </remarks>
    internal static string Describe(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        if (text.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            text = DecodeClixml(text);
        }

        var line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => !candidate.StartsWith("At line:", StringComparison.Ordinal)
                && !candidate.StartsWith('+'));

        return line ?? "powershell reported a failure with no message";
    }

    /// <summary>The error strings out of a CLIXML stream, as text.</summary>
    internal static string DecodeClixml(string clixml)
    {
        var errors = ClixmlError().Matches(clixml)
            .Select(match => WebUtility.HtmlDecode(ClixmlEscape().Replace(
                match.Groups[1].Value,
                escape => ((char)int.Parse(escape.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString())))
            .ToList();

        return errors.Count == 0 ? clixml : string.Concat(errors);
    }

    [GeneratedRegex("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ClixmlError();

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant)]
    private static partial Regex ClixmlEscape();

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows print queues can only be managed on Windows");
        }
    }
}
