<#
.SYNOPSIS
    M1 capture spike: create or remove the throwaway Printo test printers.

.DESCRIPTION
    Creates exactly three queues, all named Printo-Spike-*, and removes them again on demand.
    Nothing that already exists on the machine is modified.

      Printo-Spike-IPP        Tier 1 - Microsoft IPP Class Driver bound to the local IPP
                              endpoint hosted by printo-spike-ipp.exe.
      Printo-Spike-PDF-File   Tier 2a - inbox Microsoft Print To PDF bound to a Local Port
                              that points at a file we own (not PORTPROMPT:).
      Printo-Spike-PDF-Pipe   Tier 2b - the same driver bound to a Local Port that points at
                              a named pipe hosted by printo-spike-pipeport.exe.

    Requires elevation: Add-Printer and Add-PrinterPort are administrator operations.

.PARAMETER Action
    Add, Remove or Status.
#>
[CmdletBinding()]
param(
    [ValidateSet('Add', 'Remove', 'Status')]
    [string]$Action = 'Status',

    [int]$IppPort = 39631,

    [string]$PipeName = 'printo-spike-port',

    [string]$FilePortPath = "$env:TEMP\printo-spike-fileport.pdf",

    [string]$ResultPath = "$env:TEMP\printo-spike-result.json"
)

$ErrorActionPreference = 'Continue'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Action -ne 'Status' -and -not $isAdmin) {
    throw 'Invoke-SpikePrinters.ps1 must run elevated for Add and Remove.'
}

$ippPrinter = 'Printo-Spike-IPP'
$filePrinter = 'Printo-Spike-PDF-File'
$pipePrinter = 'Printo-Spike-PDF-Pipe'
$pipePort = "\\.\pipe\$PipeName"
$ippUrl = "http://127.0.0.1:$IppPort/ipp/print"

$steps = @()

function Add-Step {
    param([string]$Name, [scriptblock]$Body)
    $entry = [ordered]@{ step = $Name; ok = $false; detail = $null }
    try {
        $result = & $Body
        $entry.ok = $true
        $entry.detail = if ($null -ne $result) { "$result" } else { 'ok' }
    }
    catch {
        $entry.detail = $_.Exception.Message
    }
    $script:steps += [pscustomobject]$entry
    $status = if ($entry.ok) { 'OK  ' } else { 'FAIL' }
    Write-Host ("[{0}] {1} :: {2}" -f $status, $Name, $entry.detail)
}

switch ($Action) {
    'Add' {
        Add-Step 'tier1-add-ipp-printer' {
            Add-Printer -Name $ippPrinter -IppURL $ippUrl -ErrorAction Stop
            "added $ippPrinter -> $ippUrl"
        }

        Add-Step 'tier2a-add-file-port' {
            if (-not (Get-PrinterPort -Name $FilePortPath -ErrorAction SilentlyContinue)) {
                Add-PrinterPort -Name $FilePortPath -ErrorAction Stop
            }
            "port $FilePortPath"
        }

        Add-Step 'tier2a-add-printer' {
            Add-Printer -Name $filePrinter -DriverName 'Microsoft Print To PDF' `
                -PortName $FilePortPath -ErrorAction Stop
            "added $filePrinter on $FilePortPath"
        }

        Add-Step 'tier2b-add-pipe-port' {
            if (-not (Get-PrinterPort -Name $pipePort -ErrorAction SilentlyContinue)) {
                Add-PrinterPort -Name $pipePort -ErrorAction Stop
            }
            "port $pipePort"
        }

        Add-Step 'tier2b-add-printer' {
            Add-Printer -Name $pipePrinter -DriverName 'Microsoft Print To PDF' `
                -PortName $pipePort -ErrorAction Stop
            "added $pipePrinter on $pipePort"
        }
    }

    'Remove' {
        foreach ($name in @($ippPrinter, $filePrinter, $pipePrinter)) {
            Add-Step "remove-printer-$name" {
                if (Get-Printer -Name $name -ErrorAction SilentlyContinue) {
                    Remove-Printer -Name $name -ErrorAction Stop
                    "removed $name"
                }
                else { "$name not present" }
            }
        }

        # The IPP queue creates its own port; find any port that points at our endpoint.
        Add-Step 'remove-ipp-port' {
            $ports = Get-PrinterPort -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "*$IppPort*" -or $_.Description -like "*$IppPort*" }
            if (-not $ports) { return 'no ipp port found' }
            foreach ($port in $ports) {
                Remove-PrinterPort -Name $port.Name -ErrorAction Stop
            }
            "removed " + (($ports | ForEach-Object { $_.Name }) -join ', ')
        }

        foreach ($port in @($FilePortPath, $pipePort)) {
            Add-Step "remove-port-$port" {
                if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) {
                    Remove-PrinterPort -Name $port -ErrorAction Stop
                    "removed $port"
                }
                else { "$port not present" }
            }
        }
    }
}

$snapshot = [ordered]@{
    action    = $Action
    elevated  = $isAdmin
    timestamp = (Get-Date).ToString('o')
    steps     = $steps
    printers  = @(Get-Printer -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Printo-Spike-*' } |
        Select-Object Name, DriverName, PortName, PrinterStatus)
    ports     = @(Get-PrinterPort -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*printo-spike*' -or $_.Name -like "*$IppPort*" -or $_.Description -like "*$IppPort*" } |
        Select-Object Name, Description, PortMonitor)
}

$snapshot | ConvertTo-Json -Depth 6 | Out-File -FilePath $ResultPath -Encoding utf8
Write-Host "Result written to $ResultPath"
