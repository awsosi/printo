<#
.SYNOPSIS
    Installs, upgrades and removes the Printo Agent MSI, checking each step.

.DESCRIPTION
    The one part of the installer that cannot be proved by inspecting the package: whether
    Windows Installer actually installs it, upgrades it in place, and removes it without
    residue. That needs elevation, so it is a script to run rather than something the build
    does.

    RUN THIS ON A TEST MACHINE OR A VM, not on a workstation you care about. It installs and
    uninstalls a service, writes to HKLM and to ProgramData, and it removes what it created -
    including, on the final uninstall check, the agent's data directory.

    What it checks, in order:

      1. a clean install succeeds silently
      2. the service exists, is set to start automatically, and is running
      3. the unattended properties reached the registry the agent reads
      4. an in-place upgrade to a higher version succeeds and keeps the service running
      5. the data directory survives the upgrade - a site's spool and identity must not be
         thrown away by a version bump
      6. uninstall removes the service, the install directory and the registry key

.PARAMETER Msi
    The package to test. Defaults to the newest MSI in `bin`.

.PARAMETER UpgradeMsi
    A higher-versioned package, to test the upgrade path. Build one with
    `build.ps1 -Version 0.1.1`. When omitted, the upgrade steps are reported as not run.

.EXAMPLE
    # From an elevated PowerShell:
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0
    pwsh clients/windows/installer/build.ps1 -Version 0.1.1
    pwsh clients/windows/installer/Verify-Install.ps1 `
        -Msi bin/PrintoAgent-0.1.0.msi -UpgradeMsi bin/PrintoAgent-0.1.1.msi
#>
[CmdletBinding()]
param(
    [string]$Msi,
    [string]$UpgradeMsi,
    [string]$ServerUrl = 'https://printo.verify.local/api/',
    [string]$DecisionMode = 'auto'
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Msi) {
    $Msi = (Get-ChildItem (Join-Path $here 'bin') -Filter '*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Msi -or -not (Test-Path $Msi)) { throw "No MSI found. Run build.ps1 first." }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script installs a service and must run from an elevated PowerShell.'
}

$logDir = Join-Path $env:TEMP "printo-verify-$([guid]::NewGuid().ToString('n').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$failures = @()
function Check([string]$what, [scriptblock]$test) {
    try {
        if (& $test) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what"; $script:failures += $what }
    } catch {
        Write-Host "  FAIL  $what ($($_.Exception.Message))"
        $script:failures += $what
    }
}

function Invoke-Msi([string]$package, [string[]]$arguments, [string]$logName) {
    $log = Join-Path $logDir $logName
    $all = @('/i', "`"$package`"", '/qn', '/norestart', '/l*v', "`"$log`"") + $arguments
    $process = Start-Process msiexec.exe -ArgumentList $all -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "msiexec returned $($process.ExitCode); see $log"
    }
}

$dataDir = Join-Path $env:ProgramData 'Printo\agent'
$installDir = Join-Path ${env:ProgramFiles} 'Printo Agent'
$machineKey = 'HKLM:\SOFTWARE\Printo\Agent'

Write-Host "==> installing $Msi"
Invoke-Msi $Msi @("SERVERURL=$ServerUrl", "DECISIONMODE=$DecisionMode") 'install.log'

Write-Host '==> a clean install'
Check 'the service is registered' { $null -ne (Get-Service -Name PrintoAgent -ErrorAction SilentlyContinue) }
Check 'the service starts automatically' { (Get-CimInstance Win32_Service -Filter "Name='PrintoAgent'").StartMode -eq 'Auto' }
Check 'the service is running' {
    # Given up to 20 seconds: the first start opens the spool database and enumerates printers.
    for ($i = 0; $i -lt 20; $i++) {
        if ((Get-Service PrintoAgent).Status -eq 'Running') { return $true }
        Start-Sleep -Seconds 1
    }
    $false
}
Check 'the binaries are installed' { Test-Path (Join-Path $installDir 'Printo.Agent.exe') }
Check 'the native renderer is installed' { Test-Path (Join-Path $installDir 'pdfium.dll') }
Check 'the tray starts at sign-in' {
    (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name PrintoTray -ErrorAction SilentlyContinue).PrintoTray -like '*Printo.Tray.exe*'
}

Write-Host '==> unattended configuration reached the agent'
Check 'the server address was written' { (Get-ItemProperty $machineKey -Name ServerUrl).ServerUrl -eq $ServerUrl }
Check 'the decision mode was written' { (Get-ItemProperty $machineKey -Name DecisionMode).DecisionMode -eq $DecisionMode }
Check 'the agent reports that configuration back' {
    # The authoritative check: not what the registry holds, but what the agent resolved from it.
    $output = & (Join-Path $installDir 'Printo.Agent.exe') --show-config 2>&1 | Out-String
    ($output -match [regex]::Escape($ServerUrl)) -and ($output -match 'from Install')
}

Write-Host '==> the data directory is protected'
Check 'the data directory exists' { Test-Path $dataDir }
Check 'ordinary users cannot read it' {
    $rules = (Get-Acl $dataDir).Access | Where-Object { -not $_.IsInherited }
    $identities = $rules | ForEach-Object { $_.IdentityReference.Value }
    # The enrolment credential lives here, and ProgramData grants Users read by default.
    -not ($identities -match 'BUILTIN\\Users|Everyone|Authenticated Users')
}

# A marker file, to prove an upgrade does not discard a site's spool and identity.
$marker = Join-Path $dataDir 'verify-marker.txt'
Set-Content -Path $marker -Value 'survives upgrades' -Encoding utf8

if ($UpgradeMsi -and (Test-Path $UpgradeMsi)) {
    Write-Host "==> upgrading in place with $UpgradeMsi"
    Invoke-Msi $UpgradeMsi @() 'upgrade.log'

    Check 'the service survived the upgrade' {
        for ($i = 0; $i -lt 20; $i++) {
            if ((Get-Service PrintoAgent -ErrorAction SilentlyContinue).Status -eq 'Running') { return $true }
            Start-Sleep -Seconds 1
        }
        $false
    }
    Check 'only one product is registered' {
        @(Get-CimInstance Win32_Product -Filter "Name='Printo Agent'" -ErrorAction SilentlyContinue).Count -le 1
    }
    Check 'the data directory survived the upgrade' { Test-Path $marker }
    Check 'the configuration survived the upgrade' {
        # An upgrade run without properties must not blank a working machine's settings.
        (Get-ItemProperty $machineKey -Name ServerUrl -ErrorAction SilentlyContinue).ServerUrl -eq $ServerUrl
    }
} else {
    Write-Host '==> upgrade NOT TESTED (pass -UpgradeMsi with a higher-versioned package)'
    $failures += 'upgrade not tested'
}

Write-Host '==> uninstalling'
$last = if ($UpgradeMsi -and (Test-Path $UpgradeMsi)) { $UpgradeMsi } else { $Msi }
$log = Join-Path $logDir 'uninstall.log'
$process = Start-Process msiexec.exe -ArgumentList @('/x', "`"$last`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "uninstall returned $($process.ExitCode); see $log" }

Check 'the service is gone' { $null -eq (Get-Service -Name PrintoAgent -ErrorAction SilentlyContinue) }
Check 'the install directory is gone' { -not (Test-Path (Join-Path $installDir 'Printo.Agent.exe')) }
Check 'the autostart entry is gone' {
    $null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name PrintoTray -ErrorAction SilentlyContinue)
}
Check 'the machine registry key is gone' { -not (Test-Path $machineKey) }

# Data is deliberately left behind by the uninstall - a reinstall should find its spool and its
# enrolment - so this cleans up after the test rather than asserting the directory is gone.
if (Test-Path $dataDir) {
    Write-Host "  NOTE  the data directory remains at $dataDir (by design); removing it now"
    Remove-Item -Recurse -Force $dataDir
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "install / upgrade / uninstall verified. Logs: $logDir"
    exit 0
}

Write-Host "FAILED: $($failures.Count) check(s)"
$failures | ForEach-Object { Write-Host "  - $_" }
Write-Host "Logs: $logDir"
exit 1
