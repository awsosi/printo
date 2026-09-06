<#
.SYNOPSIS
    M1 Tier 1 capture spike, end to end in one elevated command.

.DESCRIPTION
    Answers the two open M1 questions - what page description language Windows delivers to an
    IPP Everywhere queue, and which IPP job attributes come with it - without the operator
    having to orchestrate the build, the listener, the queue and the cleanup by hand.

    It builds `printo-spike-ipp`, starts it, creates a `Printo-Spike-IPP` queue bound to it with
    the inbox Microsoft IPP Class Driver, waits for you to print one page to that queue, then
    reports what arrived and takes the queue away again.

    The queue and the listener are removed in a `finally`, so a Ctrl+C or a failure part way
    through still leaves the machine as it was found. That matters more than it sounds: a
    half-created queue bound to a dead endpoint makes every subsequent print dialog on the
    machine wait while Windows tries to reach it.

    Requires elevation: Add-Printer and Remove-Printer are administrator operations.

.PARAMETER Formats
    What the spike advertises in `document-format-supported`. `both` asks the honest question -
    which does Windows *choose*. `pdf` and `raster` force the issue when the answer to `both`
    is ambiguous, for instance to establish whether a PDF-only printer is usable at all.

.PARAMETER TimeoutSeconds
    How long to wait for a print job before giving up and cleaning up.

.PARAMETER KeepPrinter
    Leave the queue and the listener in place when the script exits, for further experiments.
    Remove them afterwards with `Invoke-SpikePrinters.ps1 -Action Remove`.

.EXAMPLE
    # From an ELEVATED PowerShell:
    powershell -ExecutionPolicy Bypass -File clients\windows\spike\scripts\Run-CaptureSpike.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File clients\windows\spike\scripts\Run-CaptureSpike.ps1 -Formats pdf
#>
[CmdletBinding()]
param(
    [ValidateSet('both', 'pdf', 'raster')]
    [string]$Formats = 'both',

    [int]$Port = 39631,

    [int]$TimeoutSeconds = 300,

    [switch]$KeepPrinter
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run-CaptureSpike.ps1 must run from an ELEVATED PowerShell: Add-Printer requires it.'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$spikeRoot = Split-Path -Parent $here
$project = Join-Path $spikeRoot 'Printo.Spike.Ipp\Printo.Spike.Ipp.csproj'
$captureDir = Join-Path $spikeRoot 'capture'
$logPath = Join-Path $captureDir 'ipp-session.jsonl'
$stdoutPath = Join-Path $captureDir 'listener-stdout.log'
$stderrPath = Join-Path $captureDir 'listener-stderr.log'
$printerName = 'Printo-Spike-IPP'
$ippUrl = "http://127.0.0.1:$Port/ipp/print"
$exitCode = 1

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found on PATH. Install the .NET 10 SDK.'
}

# A previous run that was killed rather than exited leaves the queue behind, and a queue bound
# to a dead endpoint stalls print dialogs. Clear it before doing anything else.
if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
    Write-Host "==> removing a leftover $printerName from an earlier run"
    Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
}

# Started fresh each run so "what arrived" is unambiguous rather than mixed with an older pass.
if (Test-Path $captureDir) { Remove-Item -Recurse -Force $captureDir }
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null

Write-Host '==> building the IPP capture spike'
& dotnet build $project --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'building Printo.Spike.Ipp failed' }

$exe = Join-Path $spikeRoot 'Printo.Spike.Ipp\bin\Release\net10.0\printo-spike-ipp.exe'
if (-not (Test-Path $exe)) { throw "the build did not produce $exe" }

$listener = $null
try {
    Write-Host "==> starting the listener on $ippUrl (advertising $Formats)"
    $listener = Start-Process -FilePath $exe `
        -ArgumentList @('--port', "$Port", '--formats', $Formats, '--out', "`"$captureDir`"") `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    # Windows queries printer attributes during Add-Printer, so the endpoint has to be
    # answering before the queue is created - not merely have been started.
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        if ($listener.HasExited) {
            Get-Content $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
            throw "the listener exited immediately with code $($listener.ExitCode)"
        }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw "the listener did not answer on port $Port within 15 seconds" }
    Write-Host '    listener is answering'

    Write-Host "==> creating the $printerName queue"
    Add-Printer -Name $printerName -IppURL $ippUrl
    $queue = Get-Printer -Name $printerName
    Write-Host "    driver: $($queue.DriverName)"
    Write-Host "    port  : $($queue.PortName)"

    Write-Host ''
    Write-Host '========================================================================'
    Write-Host "  NOW: print ONE page to the printer named '$printerName'."
    Write-Host '  Chrome is the case that matters - open any page or a PDF from'
    Write-Host '  printo-materials, Ctrl+P, pick that printer, Print.'
    Write-Host "  Waiting up to $TimeoutSeconds seconds..."
    Write-Host '========================================================================'
    Write-Host ''

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $delivered = $null
    while ((Get-Date) -lt $deadline -and -not $delivered) {
        if (Test-Path $logPath) {
            foreach ($line in (Get-Content $logPath -ErrorAction SilentlyContinue)) {
                if (-not $line) { continue }
                try { $record = $line | ConvertFrom-Json } catch { continue }
                if ($record.PSObject.Properties.Name -contains 'deliveredFormat') {
                    $delivered = $record
                    break
                }
            }
        }
        if (-not $delivered) { Start-Sleep -Seconds 1 }
    }

    Write-Host ''
    if (-not $delivered) {
        Write-Host 'NO JOB ARRIVED.'
        if (Test-Path $logPath) {
            $operations = @(Get-Content $logPath | ForEach-Object {
                try { ($_ | ConvertFrom-Json).operation } catch { $null }
            } | Where-Object { $_ })
            if ($operations.Count -gt 0) {
                # Windows talked to us but never sent a document: the queue bound correctly and
                # something rejected the job later. That is a different fault from silence.
                Write-Host "Windows did reach the endpoint: $($operations -join ', ')"
            } else {
                Write-Host 'Windows never reached the endpoint at all.'
            }
        }
        Write-Host "Full log: $logPath"
    } else {
        Write-Host '================= M1 TIER 1 ANSWER ====================================='
        Write-Host "  delivered format : $($delivered.deliveredFormat)"
        Write-Host "  delivered bytes  : $($delivered.deliveredBytes)"
        Write-Host "  saved to         : $($delivered.deliveredPath)"
        Write-Host "  via operation    : $($delivered.operation)"
        Write-Host "  IPP version      : $($delivered.ippVersion)"
        Write-Host "  user agent       : $($delivered.httpUserAgent)"
        Write-Host ''
        Write-Host '  operation attributes Windows sent:'
        foreach ($attribute in $delivered.operationAttributes) { Write-Host "    $attribute" }
        if ($delivered.jobAttributes -and @($delivered.jobAttributes).Count -gt 0) {
            Write-Host '  job attributes Windows sent:'
            foreach ($attribute in $delivered.jobAttributes) { Write-Host "    $attribute" }
        }
        Write-Host '========================================================================'
        Write-Host "Full log: $logPath"
        $exitCode = 0
    }
}
finally {
    if (-not $KeepPrinter) {
        Write-Host ''
        Write-Host '==> cleaning up'
        if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
            Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
            Write-Host "    removed $printerName"
        }
        # Add-Printer -IppURL creates its own port, named for the endpoint it points at.
        Get-PrinterPort -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*$Port*" -or $_.Description -like "*$Port*" } |
            ForEach-Object {
                Remove-PrinterPort -Name $_.Name -ErrorAction SilentlyContinue
                Write-Host "    removed port $($_.Name)"
            }
        if ($listener -and -not $listener.HasExited) {
            Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue
            Write-Host '    stopped the listener'
        }
    } else {
        Write-Host ''
        Write-Host 'The queue and listener are still up (-KeepPrinter). Remove them with:'
        Write-Host '  Invoke-SpikePrinters.ps1 -Action Remove'
    }
}

exit $exitCode
