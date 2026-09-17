<#
.SYNOPSIS
    Installs, upgrades and removes a Printo Agent package, checking each step.

.DESCRIPTION
    The one part of an installer that cannot be proved by inspecting the package: whether it
    actually installs, upgrades in place, and removes itself without residue. That needs
    elevation, so it is a script to run rather than something the build does.

    It takes either package. There are two - an MSI for Group Policy, which accepts nothing
    else, and an EXE for the machines where Windows Installer does not work - and the checks
    below are the contract they share. Running the same checks against both is what stops the
    two drifting into installing subtly different machines.

    RUN THIS ON A TEST MACHINE OR A VM, not on a workstation you care about. It installs and
    uninstalls a service, writes to HKLM and to ProgramData, and it removes what it created -
    including, on the final uninstall check, the agent's data directory.

    What it checks, in order:

      1. a clean install succeeds
      2. the service exists, is set to start automatically, and is running
      3. the virtual printer queue appears and points at the agent's own endpoint
      4. a page printed to that queue reaches the agent
      5. the unattended properties reached the registry the agent reads
      6. an in-place upgrade to a higher version succeeds and keeps the service running
      7. the data directory survives the upgrade - a site's spool and identity must not be
         thrown away by a version bump
      8. uninstall removes the service, the install directory, the registry key and the queue

    Step 4 prints the Windows test page to the Printo queue. On a machine with no printers
    mapped the agent will then fail to route it, which is expected and harmless: the point of
    the check is that the document arrived at all.

.PARAMETER Package
    The package to test, `.msi` or `.exe`. Defaults to the newest of either in `bin`.

.PARAMETER UpgradePackage
    A higher-versioned package of the same kind, to test the upgrade path. Build one with
    `build.ps1 -Version 0.1.1`. When omitted, the upgrade steps are reported as not run.

.EXAMPLE
    # From an elevated PowerShell:
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0
    pwsh clients/windows/installer/build.ps1 -Version 0.1.1
    pwsh clients/windows/installer/Verify-Install.ps1 `
        -Package bin/PrintoAgent-0.1.0.msi -UpgradePackage bin/PrintoAgent-0.1.1.msi

.EXAMPLE
    pwsh clients/windows/installer/Verify-Install.ps1 `
        -Package bin/PrintoAgent-0.1.0.exe -UpgradePackage bin/PrintoAgent-0.1.1.exe
#>
[CmdletBinding()]
param(
    [Alias('Msi')]
    [string]$Package,

    [Alias('UpgradeMsi')]
    [string]$UpgradePackage,

    [string]$ServerUrl = 'https://printo.verify.local/api/',
    [string]$DecisionMode = 'auto'
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Package) {
    $Package = (Get-ChildItem (Join-Path $here 'bin') -Include '*.msi', '*.exe' -File -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Package -or -not (Test-Path $Package)) { throw 'No package found. Run build.ps1 first.' }

$kind = if ([IO.Path]::GetExtension($Package) -eq '.exe') { 'EXE' } else { 'MSI' }

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

# Either package, driven the same way. The unattended settings are spelled identically - that is
# a deliberate property of the two installers and `SetupParityTests` is what holds them to it -
# so the only thing that differs here is how each one is asked to be quiet and where it logs.
function Invoke-Installer([string]$package, [string[]]$settings, [string]$logName) {
    $log = Join-Path $logDir $logName

    if ([IO.Path]::GetExtension($package) -eq '.exe') {
        $all = @('/quiet', '/log', "`"$log`"") + $settings
        $process = Start-Process $package -ArgumentList $all -Wait -PassThru
    } else {
        $all = @('/i', "`"$package`"", '/qn', '/norestart', '/l*v', "`"$log`"") + $settings
        $process = Start-Process msiexec.exe -ArgumentList $all -Wait -PassThru
    }

    if ($process.ExitCode -ne 0) {
        throw "$(Split-Path -Leaf $package) returned $($process.ExitCode); see $log"
    }
}

function Uninstall-Package([string]$package, [string]$logName) {
    $log = Join-Path $logDir $logName

    if ([IO.Path]::GetExtension($package) -eq '.exe') {
        # The downloaded package rather than the copy it left in Program Files. Both remove the
        # product; the installed copy has to restart itself out of %TEMP% first so it can delete
        # the directory it is running from, and returns as soon as it has handed over, which is
        # not something to race in a check.
        $process = Start-Process $package -ArgumentList @('/uninstall', '/quiet', '/log', "`"$log`"") -Wait -PassThru
    } else {
        $process = Start-Process msiexec.exe -ArgumentList @('/x', "`"$package`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
    }

    if ($process.ExitCode -ne 0) { throw "uninstall returned $($process.ExitCode); see $log" }

    # And then wait for the service to actually go, so that a removal which finished in the
    # background is not read as one that did not happen.
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Service -Name PrintoAgent -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
}

$dataDir = Join-Path $env:ProgramData 'Printo\agent'
$installDir = Join-Path ${env:ProgramFiles} 'Printo Agent'
$machineKey = 'HKLM:\SOFTWARE\Printo\Agent'

Write-Host "==> installing $Package ($kind)"
Invoke-Installer $Package @("SERVERURL=$ServerUrl", "DECISIONMODE=$DecisionMode") 'install.log'

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
    $value = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name PrintoTray -ErrorAction SilentlyContinue).PrintoTray
    # The value alone is not enough - it pointed at a real executable for a whole release while
    # that executable, run with no arguments, showed a usage message box instead of a tray.
    ($value -like '*Printo.Tray.exe*') -and (Test-Path (Join-Path $installDir 'Printo.Tray.exe'))
}
Check 'there is a Start Menu shortcut to open' {
    # Otherwise the install is invisible: a headless service plus an autostart entry that does
    # not fire until the next sign-in reads as "nothing happened" to whoever ran the MSI.
    $link = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Printo\Printo.lnk'
    if (-not (Test-Path $link)) { return $false }
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath
    Test-Path $target
}

Write-Host '==> the virtual printer'
Check 'the queue exists' {
    # Up to two minutes: the first Add-Printer on a machine stages the inbox IPP class driver,
    # and the service does it on a background task so the rest of the agent starts meanwhile.
    for ($i = 0; $i -lt 120; $i++) {
        if (Get-Printer -Name Printo -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Seconds 1
    }
    $false
}
Check 'the queue points at the agent endpoint' {
    $port = (Get-Printer -Name Printo -ErrorAction SilentlyContinue).PortName
    # Loopback and the agent's port: a queue pointing anywhere else would accept jobs and lose
    # them, which is the one failure this whole path must not have.
    $port -match '^http://127\.0\.0\.1:\d+/ipp/print/?$'
}
Check 'the endpoint answers' {
    $port = (Get-Printer -Name Printo -ErrorAction SilentlyContinue).PortName
    try { (Invoke-WebRequest -Uri $port -UseBasicParsing -TimeoutSec 10).Content -match 'Printo virtual printer' }
    catch { $false }
}

Write-Host '==> a printed page reaches the agent'
$since = Get-Date
Check 'the Windows test page is captured' {
    # Through the real spooler and the real class driver - the half of this path that no unit
    # test can reach. The agent logs every captured document to the event log, which is also
    # where a domain administrator would look.
    & rundll32.exe printui.dll,PrintUIEntry /k /n "Printo"
    for ($i = 0; $i -lt 60; $i++) {
        $entry = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'; ProviderName = 'Printo Agent'; StartTime = $since
        } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Capture captured' }
        if ($entry) { return $true }
        Start-Sleep -Seconds 1
    }
    $false
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
Check 'it has an ACL of its own' {
    # Not the inherited ProgramData rules: those grant every authenticated user read, and this
    # is where a site's queued documents live.
    (Get-Acl $dataDir).AreAccessRulesProtected
}
Check 'the system and administrators have full control' {
    # By SID, not by name. `Administrators` is `Administratorzy` on a Polish installation and
    # something else again elsewhere - naming these in English is what made the first install of
    # this package fail with 1603.
    $rules = (Get-Acl $dataDir).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    $full = $rules | Where-Object {
        $_.FileSystemRights.HasFlag([System.Security.AccessControl.FileSystemRights]::FullControl)
    } | ForEach-Object { $_.IdentityReference.Value }

    ($full -contains 'S-1-5-18') -and ($full -contains 'S-1-5-32-544')
}
Check 'ordinary users can use it but not take it over' {
    # Users need read and write: the tray runs as the operator and reads this machine's
    # configuration, the document the picker is asking about, and the job queue behind its
    # tooltip. What they must not have is control of the ACL itself. The enrolment credential is
    # protected separately, as its own file - see the check below.
    $rules = (Get-Acl $dataDir).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    $users = $rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' }
    if (-not $users) { return $false }

    $forbidden = [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                 [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    -not ($users | Where-Object { ($_.FileSystemRights -band $forbidden) -ne 0 })
}
Check 'the enrolment credential is out of their reach' {
    # Written by the agent, not by the installer, and locked from well-known SIDs as it is
    # written. Absent until the machine enrols, which this run does not do - so an absent file
    # passes and a present one is checked.
    $identity = Join-Path $dataDir 'identity.json'
    if (-not (Test-Path $identity)) { return $true }

    $rules = (Get-Acl $identity).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    -not ($rules | Where-Object { $_.IdentityReference.Value -in @('S-1-5-32-545', 'S-1-1-0', 'S-1-5-11') })
}

# A marker file, to prove an upgrade does not discard a site's spool and identity.
$marker = Join-Path $dataDir 'verify-marker.txt'
Set-Content -Path $marker -Value 'survives upgrades' -Encoding utf8

if ($UpgradePackage -and (Test-Path $UpgradePackage)) {
    Write-Host "==> upgrading in place with $UpgradePackage"
    Invoke-Installer $UpgradePackage @() 'upgrade.log'

    Check 'the service survived the upgrade' {
        for ($i = 0; $i -lt 20; $i++) {
            if ((Get-Service PrintoAgent -ErrorAction SilentlyContinue).Status -eq 'Running') { return $true }
            Start-Sleep -Seconds 1
        }
        $false
    }
    Check 'only one product is registered' {
        # Add/Remove Programs rather than Win32_Product: the EXE installs nothing that Windows
        # Installer knows about, and enumerating Win32_Product asks every installed MSI on the
        # machine to reconfigure itself, which is slow and not free of side effects.
        $entries = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
            ForEach-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName } |
            Where-Object { $_ -eq 'Printo Agent' })
        $entries.Count -eq 1
    }
    Check 'the data directory survived the upgrade' { Test-Path $marker }
    Check 'the configuration survived the upgrade' {
        # An upgrade run without properties must not blank a working machine's settings.
        (Get-ItemProperty $machineKey -Name ServerUrl -ErrorAction SilentlyContinue).ServerUrl -eq $ServerUrl
    }
} else {
    Write-Host '==> upgrade NOT TESTED (pass -UpgradePackage with a higher-versioned package)'
    $failures += 'upgrade not tested'
}

Write-Host '==> uninstalling'
$last = if ($UpgradePackage -and (Test-Path $UpgradePackage)) { $UpgradePackage } else { $Package }
Uninstall-Package $last 'uninstall.log'

Check 'the service is gone' { $null -eq (Get-Service -Name PrintoAgent -ErrorAction SilentlyContinue) }
Check 'the install directory is gone' { -not (Test-Path (Join-Path $installDir 'Printo.Agent.exe')) }
Check 'the autostart entry is gone' {
    $null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name PrintoTray -ErrorAction SilentlyContinue)
}
Check 'the machine registry key is gone' { -not (Test-Path $machineKey) }
Check 'the virtual printer queue is gone' {
    # Removed by the package's one custom action. A queue left behind would go on accepting
    # jobs into a socket that no longer has anything listening on it.
    $null -eq (Get-Printer -Name Printo -ErrorAction SilentlyContinue)
}
Check 'the Start Menu folder is gone' {
    -not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Printo'))
}
Check 'the Add/Remove Programs entry is gone' {
    # An entry left behind is worse than no entry: it offers a person a Uninstall button that
    # runs a program which is no longer there.
    @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        ForEach-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName } |
        Where-Object { $_ -eq 'Printo Agent' }).Count -eq 0
}

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
