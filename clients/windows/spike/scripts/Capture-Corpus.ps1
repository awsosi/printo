<#
.SYNOPSIS
    Captures a spread of real documents through the Windows print path, for analysis.

.DESCRIPTION
    `Run-CaptureSpike.ps1` answered "which tier, which format" with one document. This answers
    the harder question that one document raised: what does the Windows print pipeline actually
    do to a page on its way through, across the shapes that occur in production.

    It matters because the corpus is not uniform. 63 of 258 documents mix page sizes inside one
    job - A4 portrait, a 99x200 mm label on label stock, A4 landscape - and a Windows queue
    prints a whole job onto one media size. Rotation was the first transformation we caught;
    scaling, media substitution and margin fitting are all things a print pipeline does, and a
    rule set calibrated on source files is not thereby calibrated on printed ones.

    The printing is deliberately NOT automated. The Windows PDF handler exposes no `print` or
    `printto` verb on this machine, and more importantly Ctrl+P then Enter in Chrome *is* the
    production workflow - so driving it by hand is more faithful evidence than any script, not
    less. The spike queue is made the default printer for the duration so that Enter goes
    straight to it, and the previous default is put back afterwards.

    Chrome runs on a throwaway profile so the run cannot disturb the operator's own session,
    and so the print destination is predictable rather than whatever Chrome last remembered.

    Everything is restored in a `finally`: the queue, the default printer, the temporary
    profile and the listener.

.PARAMETER Documents
    Paths to print. Defaults to a curated spread covering every page-shape family in the
    corpus - see the table in the script - plus a generated HTML probe that answers whether
    visible text survives as text.

.PARAMETER CorpusDirectory
    Where the sample PDFs live.

.EXAMPLE
    # From an ELEVATED PowerShell:
    powershell -ExecutionPolicy Bypass -File clients\windows\spike\scripts\Capture-Corpus.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Documents,

    [string]$CorpusDirectory = 'C:\Users\olek\Documents\code\si\printo-materials',

    [int]$Port = 39631,

    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

# An unmistakable banner. This script and Run-CaptureSpike.ps1 do similar things in the same
# window, and a session's scrollback from one is easy to mistake for a run of the other.
Write-Host ''
Write-Host '########################################################################'
Write-Host '#  CAPTURE-CORPUS  -  multi-document capture                           #'
Write-Host '#  You will be asked to press Ctrl+P then Enter once per document.     #'
Write-Host '#  Output goes to tests\capture\session\                               #'
Write-Host '########################################################################'
Write-Host ''

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Capture-Corpus.ps1 must run from an ELEVATED PowerShell: Add-Printer requires it.'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$spikeRoot = Split-Path -Parent $here
# scripts -> spike -> windows -> clients -> repository root. Counted wrong once, which put the
# whole session under `clients\tests\` and made a run that had happened look like one that had
# not, so it is asserted rather than trusted.
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $spikeRoot))
if (-not (Test-Path (Join-Path $repoRoot 'profiles'))) {
    throw "computed repository root '$repoRoot' does not look like the printo checkout"
}
$project = Join-Path $spikeRoot 'Printo.Spike.Ipp\Printo.Spike.Ipp.csproj'
$captureDir = Join-Path $repoRoot 'tests\capture\session'
$logPath = Join-Path $captureDir 'ipp-session.jsonl'
$printerName = 'Printo-Spike-IPP'
$ippUrl = "http://127.0.0.1:$Port/ipp/print"
$profileDir = Join-Path $env:TEMP "printo-capture-profile-$([guid]::NewGuid().ToString('n').Substring(0,8))"

<#
  The curated spread. Each entry covers a page-shape family from the corpus census, chosen so
  that between them they exercise every transformation the print path can apply.

    VTW189036998   297x210 x2                  landscape only - the rotation case, already known
    VKI189056401   A4 | 99x200 | A4-landscape  MIXED SIZES incl. label stock - the hard case
    VTW189020749   A4 | A4-landscape           the common two-page invoice + label
    VTW189053882   A4 | 231x318 custom         a carrier sheet on a non-standard size
    VTW189048823   99x200 first, then A4       label stock as the FIRST page
    VTW189038066   297x210 single page         the minimal landscape case
#>
$curated = @(
    'OneClickPrint_VTW189036998_anon.pdf',
    'OneClickPrint_VKI189056401_anon.pdf',
    'OneClickPrint_VTW189020749_anon.pdf',
    'OneClickPrint_VTW189053882_anon.pdf',
    'OneClickPrint_VTW189048823_anon.pdf',
    'OneClickPrint_VTW189038066_anon.pdf'
)

if (-not $Documents) {
    $Documents = @()
    foreach ($name in $curated) {
        $found = Get-ChildItem -Path $CorpusDirectory -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $Documents += $found.FullName }
        else { Write-Warning "not found in the corpus, skipping: $name" }
    }
}

if ($Documents.Count -eq 0) { throw "No documents to capture. Is $CorpusDirectory correct?" }

$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome was not found; it is the client this experiment is about.' }

# The text-layer probe. Real invoice pages in the corpus carry visible text, but so does the
# anonymiser's invisible layer, and from the outside those look the same. This page has nothing
# but visible text and a box of known size, so what comes back answers both questions at once:
# does text survive as text, and was the page scaled.
$probe = Join-Path $captureDir 'text-probe.html'

if (Test-Path $captureDir) { Remove-Item -Recurse -Force $captureDir }
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null

@'
<!doctype html><meta charset="utf-8"><title>Printo text probe</title>
<style>
 body { font: 12pt/1.5 "Segoe UI", sans-serif; margin: 20mm; }
 .box { width: 100mm; height: 150mm; border: 0.5mm solid #000; margin-top: 10mm; }
 .mark { font: 9pt monospace; }
</style>
<h1>Printo text-layer probe</h1>
<p>If this paragraph comes back as selectable text, the Windows print path preserves glyphs and
the engine's text predicates can fire on captured jobs. If it comes back as an image, every
printed job is image-only and routing must rely on geometry, barcodes, OCR and picture matching.</p>
<p class="mark">MEASURE-ME the box below is exactly 100 x 150 mm at 1:1 scale.</p>
<div class="box"></div>
'@ | Set-Content -Path $probe -Encoding utf8

$Documents = @($Documents) + @($probe)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is not on PATH.' }

<#
  Recover from a previous run that did not finish.

  `finally` covers a failure and a Ctrl+C. It does not cover the window being closed, and that
  run left the machine with this script's queue as the default printer and an orphaned listener
  holding port 39631 - which would also have stopped the next run from starting. Undoing that is
  this script's job, not the operator's.

  The breadcrumb is written before the default printer is touched, so recovery knows what to put
  back even though the run that changed it is gone.
#>
$breadcrumb = Join-Path $env:ProgramData 'Printo\capture-corpus-state.json'

if (Test-Path $breadcrumb) {
    Write-Host '==> a previous run did not finish; undoing it'
    try {
        $previous = Get-Content $breadcrumb -Raw | ConvertFrom-Json
        if ($previous.previousDefault) {
            $restore = Get-CimInstance Win32_Printer -Filter "Name='$($previous.previousDefault)'" -ErrorAction Stop
            Invoke-CimMethod -InputObject $restore -MethodName SetDefaultPrinter -ErrorAction Stop | Out-Null
            Write-Host "    default printer put back to $($previous.previousDefault)"
        }
    } catch {
        Write-Warning "could not undo the previous run's default printer: $($_.Exception.Message)"
    }
    Remove-Item $breadcrumb -Force -ErrorAction SilentlyContinue
}

# An orphaned listener still owns port 39631, so a new one cannot bind and the run would fail
# with a message about the listener not answering - which says nothing about the real cause.
Get-Process printo-spike-ipp -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "==> stopping an orphaned listener from an earlier run (pid $($_.Id))"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}

if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
    Write-Host '==> removing a leftover queue from an earlier run'
    Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
}

Write-Host '==> building the IPP capture spike'
& dotnet build $project --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'building Printo.Spike.Ipp failed' }
$exe = Join-Path $spikeRoot 'Printo.Spike.Ipp\bin\Release\net10.0\printo-spike-ipp.exe'

$previousDefault = (Get-CimInstance Win32_Printer -Filter 'Default=True' -ErrorAction SilentlyContinue).Name
$listener = $null
$manifest = @()

function Get-JobCount {
    return @(Get-ChildItem $captureDir -Filter 'job-*.*' -ErrorAction SilentlyContinue).Count
}

try {
    Write-Host "==> starting the listener on $ippUrl"
    $listener = Start-Process -FilePath $exe `
        -ArgumentList @('--port', "$Port", '--formats', 'both', '--out', "`"$captureDir`"") `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $captureDir 'listener-stdout.log') `
        -RedirectStandardError (Join-Path $captureDir 'listener-stderr.log')

    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        if ($listener.HasExited) { throw "the listener exited with code $($listener.ExitCode)" }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/" -TimeoutSec 2 | Out-Null
            $ready = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "the listener did not answer on port $Port" }

    Write-Host "==> creating the $printerName queue"
    Add-Printer -Name $printerName -IppURL $ippUrl

    # So that Ctrl+P then Enter - the production keystroke - goes straight to the spike.
    #
    # Deliberately not fatal. This is a convenience: without it the operator picks the printer
    # in Chrome's dialog and the capture is just as valid. An earlier version threw here - a
    # CimInstance has no InvokeMethod, that is a WMI ManagementObject - and took the whole run
    # down with it before Chrome ever opened, for a nicety.
    Write-Host "==> making $printerName the default printer (was: $previousDefault)"

    # Written before the change, so that a run killed at any point after this can be undone by
    # the next one even though its own `finally` never got to run.
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $breadcrumb) | Out-Null
    @{ previousDefault = $previousDefault; startedAt = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content -Path $breadcrumb -Encoding utf8

    $defaultSet = $false
    try {
        $queue = Get-CimInstance Win32_Printer -Filter "Name='$printerName'" -ErrorAction Stop
        Invoke-CimMethod -InputObject $queue -MethodName SetDefaultPrinter -ErrorAction Stop | Out-Null
        $defaultSet = ((Get-CimInstance Win32_Printer -Filter 'Default=True').Name -eq $printerName)
    } catch {
        Write-Warning "could not set the default printer: $($_.Exception.Message)"
    }

    if ($defaultSet) {
        Write-Host '    done - Ctrl+P then Enter will go straight to it'
    } else {
        Write-Warning "the default printer is unchanged; choose '$printerName' in Chrome's print dialog"
    }

    $index = 0
    foreach ($document in $Documents) {
        $index++
        $before = Get-JobCount
        $name = Split-Path -Leaf $document

        Write-Host ''
        Write-Host '------------------------------------------------------------------------'
        Write-Host ("  [{0}/{1}]  {2}" -f $index, $Documents.Count, $name)
        Write-Host '  Chrome is opening it. Press Ctrl+P then Enter.'
        Write-Host ("  (destination should already say {0})" -f $printerName)
        Write-Host '------------------------------------------------------------------------'

        $browser = Start-Process -FilePath $chrome -PassThru -ArgumentList @(
            "--user-data-dir=$profileDir",
            '--no-first-run',
            '--no-default-browser-check',
            '--new-window',
            $document
        )

        # Said out loud, because "Chrome did not open" was the single most confusing symptom of
        # the first attempt and nothing on screen distinguished it from "Chrome opened and you
        # did not print".
        # Chrome's launcher process hands the command line to the browser process and exits, so
        # a zero exit code from the process we started means nothing. What matters is whether a
        # browser is actually running on our profile.
        Start-Sleep -Seconds 2
        $running = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like "*$profileDir*" })

        if ($running.Count -gt 0) {
            Write-Host "  Chrome is up ($($running.Count) processes). Waiting for the job..."
        } elseif ($browser -and $browser.HasExited -and $browser.ExitCode -ne 0) {
            Write-Warning "Chrome failed to start (code $($browser.ExitCode)). Open the file by hand and print it."
        } else {
            Write-Host '  Waiting for the job...'
        }

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $arrived = $false
        while ((Get-Date) -lt $deadline) {
            if ((Get-JobCount) -gt $before) { $arrived = $true; break }
            Start-Sleep -Milliseconds 500
        }

        if ($arrived) {
            # Give the last Send-Document a moment to be written before moving on.
            Start-Sleep -Seconds 2
            $job = Get-ChildItem $captureDir -Filter 'job-*.*' | Sort-Object LastWriteTime | Select-Object -Last 1
            Write-Host ("  captured -> {0} ({1:N0} bytes)" -f $job.Name, $job.Length)
            $manifest += [pscustomobject]@{ source = $document; job = $job.Name }
        } else {
            Write-Host '  NOTHING ARRIVED - skipped.'
            $manifest += [pscustomobject]@{ source = $document; job = $null }
        }

        if ($browser -and -not $browser.HasExited) {
            Stop-Process -Id $browser.Id -Force -ErrorAction SilentlyContinue
        }
        Get-Process chrome -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $chrome -and $_.StartTime -gt (Get-Date).AddSeconds(-$TimeoutSeconds - 30) } |
            Where-Object { $_.MainWindowTitle } |
            ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 1
    }

    $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $captureDir 'manifest.json') -Encoding utf8

    Write-Host ''
    Write-Host '==> captured'
    $manifest | Format-Table -AutoSize
    Write-Host "Everything is in $captureDir"
}
finally {
    Write-Host ''
    Write-Host '==> cleaning up'

    if ($previousDefault) {
        try {
            $restore = Get-CimInstance Win32_Printer -Filter "Name='$previousDefault'" -ErrorAction Stop
            Invoke-CimMethod -InputObject $restore -MethodName SetDefaultPrinter -ErrorAction Stop | Out-Null
            Write-Host "    default printer restored to $previousDefault"
        } catch {
            # Said loudly: leaving someone's default pointing at a queue this script is about
            # to delete would break printing on the machine until they noticed and fixed it.
            Write-Warning "COULD NOT RESTORE the default printer to '$previousDefault' - set it by hand in Settings > Printers."
        }
    }

    # Only once the machine is actually back as it was.
    Remove-Item $breadcrumb -Force -ErrorAction SilentlyContinue

    if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
        Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
        Write-Host "    removed $printerName"
    }

    Get-PrinterPort -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*$Port*" -or $_.Description -like "*$Port*" } |
        ForEach-Object { Remove-PrinterPort -Name $_.Name -ErrorAction SilentlyContinue }

    if ($listener -and -not $listener.HasExited) {
        Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue
        Write-Host '    stopped the listener'
    }

    Get-Process chrome -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$profileDir*" } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $profileDir) {
        Start-Sleep -Seconds 2
        Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
    }
}
