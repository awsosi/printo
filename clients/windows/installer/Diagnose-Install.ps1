<#
.SYNOPSIS
    Works out why the Printo Agent MSI will not install on this machine.

.DESCRIPTION
    Windows Installer reports almost everything as 1603, and the event log says nothing more.
    This collects the handful of things that actually produce a 1603 before the first file is
    copied - a broken Windows Installer, elevation, policy, a service left pending deletion, a
    blocked download - and, with -Install, runs the installation with a verbose log and reports
    the action that failed.

    The first check is the one that matters most: whether msiexec works on this machine at all,
    asked with a package that cannot exist so that nothing is installed and no package is
    blamed.

    Safe to run as an ordinary user: without -Install it only reads.

.PARAMETER Msi
    The package to look at. Defaults to the newest MSI beside this script, then in Downloads.

.PARAMETER Install
    Actually run the installation, with a verbose log, and summarise the result.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Diagnose-Install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Diagnose-Install.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$Msi,
    [switch]$Install
)

$ErrorActionPreference = 'Continue'

function Say([string]$state, [string]$text) {
    # Three states, and only the first two are conclusions: OK is a thing ruled out, STOP is a
    # thing that will make the install fail, INFO is context for reading the rest.
    $colour = 'Gray'
    if ($state -eq 'OK') { $colour = 'Green' }
    if ($state -eq 'STOP') { $colour = 'Red' }
    Write-Host ("  {0,-4} {1}" -f $state, $text) -ForegroundColor $colour
}

function Get-PolicyValue([string]$path, [string]$name) {
    try {
        $key = Get-ItemProperty -Path $path -Name $name -ErrorAction Stop
        return $key.$name
    } catch {
        return $null
    }
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Msi) {
    $candidates = @()
    foreach ($directory in @($here, (Join-Path $here 'bin'), (Join-Path $env:USERPROFILE 'Downloads'))) {
        if (Test-Path $directory) {
            $candidates += Get-ChildItem -Path $directory -Filter 'PrintoAgent-*.msi' -ErrorAction SilentlyContinue
        }
    }
    $Msi = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

Write-Host ''
Write-Host 'Printo Agent - install diagnosis' -ForegroundColor Cyan
Write-Host ("  package : {0}" -f $(if ($Msi) { $Msi } else { '(not found)' }))
Write-Host ("  machine : {0}   user: {1}" -f $env:COMPUTERNAME, "$env:USERDOMAIN\$env:USERNAME")
Write-Host ("  windows : {0}" -f (Get-CimInstance Win32_OperatingSystem).Caption)
Write-Host ''

$blockers = @()

# ---------------------------------------------------------------------------------------------
Write-Host 'Windows Installer itself' -ForegroundColor Cyan

# The first question, because everything else assumes the answer is yes: does msiexec work on
# this machine at all? Asked with a package that cannot exist, so nothing is installed and no
# package is blamed. A working installer reports 1619 - "this installation package could not be
# opened" - after thirty-odd lines of log. A machine where Windows Installer is broken or hooked
# returns 1603 in three lines, for every package, including one that is not there.
#
# This is not hypothetical: a workstation spent a day being blamed for rejecting our MSI, and it
# rejected Notepad++'s signed MSI in exactly the same way.
$controlLog = Join-Path $env:TEMP ("printo-msi-control-{0}.log" -f (Get-Random))
$controlPath = Join-Path $env:TEMP 'printo-no-such-package.msi'
$control = Start-Process msiexec.exe -ArgumentList @('/i', "`"$controlPath`"", '/qn', '/l*v', "`"$controlLog`"") -Wait -PassThru
$controlLines = 0
if (Test-Path $controlLog) { $controlLines = (Get-Content $controlLog).Count }

if ($control.ExitCode -eq 1619 -or $controlLines -gt 10) {
    Say 'OK' "Windows Installer answers normally (exit $($control.ExitCode), $controlLines log lines)"
} else {
    Say 'STOP' "Windows Installer failed on a package that does not exist (exit $($control.ExitCode), $controlLines log lines)"
    $blockers += 'Windows Installer is not working on this machine: it fails the same way for every package, including one that does not exist, so no MSI will install until that is fixed. Try, in order: msiexec /unregister then msiexec /regserver; check HKLM and HKCU \SOFTWARE\Policies\Microsoft\Windows\Installer for DisableMSI; check for an application-control policy (AppLocker, WDAC) or an EDR agent hooking msiexec; and confirm the same package installs on another workstation.'
}
Remove-Item $controlLog -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------------------------
Write-Host 'Elevation' -ForegroundColor Cyan

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# By SID: `Administrators` is `Administratorzy` on a Polish installation, and any check written
# against the English name silently reports the wrong answer.
$administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$inAdministrators = $identity.Groups | Where-Object { $_.Value -eq $administrators.Value }

if ($elevated) {
    Say 'OK' 'this session is elevated'
} elseif ($inAdministrators) {
    Say 'INFO' 'this account is an administrator, but this session is not elevated - Windows Installer will ask'
} else {
    Say 'STOP' "$env:USERNAME is not an administrator on this machine"
    $blockers += 'The account installing is not an administrator. A per-machine MSI cannot install without elevation: either install as an administrator, deploy it by Group Policy (which installs as the machine), or run it from a SYSTEM context.'
}

$enableLua = Get-PolicyValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'EnableLUA'
$promptUser = Get-PolicyValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'ConsentPromptBehaviorUser'

if ($null -ne $promptUser -and $promptUser -eq 0 -and -not $inAdministrators) {
    # The setting that makes this failure invisible: the elevation request is refused without a
    # prompt, so the install dies before it starts and nothing on screen says why.
    Say 'STOP' 'policy denies elevation requests from standard users (ConsentPromptBehaviorUser = 0)'
    $blockers += 'This machine refuses elevation prompts for standard users, so a double-clicked MSI fails silently. Install from an administrator account, or deploy by Group Policy.'
} else {
    Say 'OK' ("UAC: EnableLUA={0}, ConsentPromptBehaviorUser={1}" -f $enableLua, $promptUser)
}

# ---------------------------------------------------------------------------------------------
Write-Host 'Windows Installer policy' -ForegroundColor Cyan

foreach ($scope in @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\Installer',
                     'HKCU:\SOFTWARE\Policies\Microsoft\Windows\Installer')) {
    $disable = Get-PolicyValue $scope 'DisableMSI'
    if ($null -ne $disable -and $disable -ne 0) {
        Say 'STOP' "$scope\DisableMSI = $disable"
        $blockers += "Windows Installer is restricted by policy ($scope\DisableMSI = $disable). 1 allows only managed installs; 2 disables it entirely."
    }
}

$installerService = Get-Service msiserver -ErrorAction SilentlyContinue
if ($installerService -and $installerService.StartType -eq 'Disabled') {
    Say 'STOP' 'the Windows Installer service is disabled'
    $blockers += 'The msiserver service is disabled. Set it back to Manual.'
} else {
    Say 'OK' 'the Windows Installer service is available'
}

# ---------------------------------------------------------------------------------------------
Write-Host 'Software restriction' -ForegroundColor Cyan

# An unsigned MSI on a managed workstation is exactly what AppLocker and SRP are configured to
# stop, and a refusal looks like every other 1603: the install ends immediately, elevated or not.
$appLocker = $null
try { $appLocker = Get-AppLockerPolicy -Effective -ErrorAction Stop } catch { }

if ($appLocker) {
    $collections = @($appLocker.RuleCollections | Where-Object { $_.EnforcementMode -ne 'NotConfigured' })
    if ($collections.Count -gt 0) {
        $kinds = ($collections | ForEach-Object { "$($_.RuleCollectionType)=$($_.EnforcementMode)" }) -join ', '
        Say 'INFO' "AppLocker is configured: $kinds"
        if ($collections | Where-Object { $_.RuleCollectionType -eq 'Msi' -and $_.EnforcementMode -eq 'Enabled' }) {
            Say 'STOP' 'AppLocker is enforcing rules for Windows Installer files'
            $blockers += 'AppLocker enforces MSI rules on this machine. An unsigned package will be refused whoever runs it. Sign the MSI with the internal ADCS certificate, deploy it by Group Policy, or have an administrator add a path rule.'
        }
    } else {
        Say 'OK' 'AppLocker is present but enforcing nothing'
    }
} else {
    Say 'OK' 'no effective AppLocker policy'
}

$srp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers'
$srpLevel = Get-PolicyValue $srp 'DefaultLevel'
if ($null -ne $srpLevel -and $srpLevel -eq 0) {
    # 0 is Disallowed: everything is blocked unless a rule allows it.
    Say 'STOP' 'Software Restriction Policy defaults to Disallowed'
    $blockers += 'Software Restriction Policy is set to disallow by default, so an unsigned package in a user folder will not run. Sign it, or deploy by Group Policy.'
} else {
    Say 'OK' 'no restrictive Software Restriction Policy'
}

# ---------------------------------------------------------------------------------------------
Write-Host 'Room to install' -ForegroundColor Cyan

$systemDrive = (Get-Item $env:SystemRoot).PSDrive
$freeMb = [math]::Round($systemDrive.Free / 1MB)
if ($freeMb -lt 1024) {
    # The package is 57 MB, but Windows Installer stages the cab, writes a rollback script and
    # keeps a cached copy: a machine with a nearly full disk fails early and reports 1603.
    Say 'STOP' "only $freeMb MB free on $($systemDrive.Name):"
    $blockers += "There is not enough room on the system drive ($freeMb MB). Windows Installer stages the payload, writes a rollback script and caches the package; clear a couple of gigabytes and try again."
} else {
    Say 'OK' "$freeMb MB free on $($systemDrive.Name):"
}

$temp = $env:TEMP
if (-not (Test-Path $temp)) {
    Say 'STOP' "TEMP points at $temp, which does not exist"
    $blockers += "TEMP points at a directory that does not exist ($temp). Windows Installer extracts the payload there."
} else {
    try {
        $probe = Join-Path $temp ("printo-probe-{0}.tmp" -f (Get-Random))
        [System.IO.File]::WriteAllText($probe, 'probe')
        Remove-Item $probe -Force
        Say 'OK' "TEMP is writable ($temp)"
    } catch {
        Say 'STOP' "TEMP is not writable ($temp)"
        $blockers += "Windows Installer cannot write to TEMP ($temp), which is where it extracts the payload."
    }
}

# ---------------------------------------------------------------------------------------------
Write-Host 'The package itself' -ForegroundColor Cyan

if (-not $Msi -or -not (Test-Path $Msi)) {
    Say 'STOP' 'no package found - pass -Msi <path>'
    $blockers += 'No MSI to examine.'
} else {
    $file = Get-Item $Msi
    Say 'INFO' ("{0:N1} MB, written {1}" -f ($file.Length / 1MB), $file.LastWriteTime)

    $zone = Get-Item -Path $Msi -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($zone) {
        # A file copied from a share or a browser carries a mark of the web, and some policies
        # refuse to install one at all.
        Say 'STOP' 'the file is marked as downloaded from another computer'
        $blockers += "The package is blocked by its mark of the web. Run: Unblock-File '$Msi'"
    } else {
        Say 'OK' 'the file is not blocked'
    }

    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($file.FullName, 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        $version = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        Say 'OK' "the package opens and reports version $version"
    } catch {
        Say 'STOP' 'the package cannot be opened - it is corrupt or was truncated in transfer'
        $blockers += 'The MSI does not open as a database. Copy it again; a truncated transfer looks exactly like this.'
    }
}

# ---------------------------------------------------------------------------------------------
Write-Host 'Previous attempts' -ForegroundColor Cyan

$installed = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq 'Printo Agent' }

$service = Get-Service PrintoAgent -ErrorAction SilentlyContinue

if ($service -and $installed) {
    # Both present is an ordinary installed machine, whatever the service happens to be doing.
    Say 'INFO' ("Printo Agent {0} is installed; the service is {1}" -f $installed.DisplayVersion, $service.Status)
} elseif ($service) {
    # A service with no product behind it is the leftover that blocks the next install until the
    # machine restarts - Windows only marks it deleted while something still holds it open.
    Say 'STOP' ("a PrintoAgent service exists with no product behind it (status {0})" -f $service.Status)
    $blockers += 'A service left behind by a failed install blocks the next one until the machine is restarted. Close Services and Event Viewer, restart, then install.'
} elseif ($installed) {
    Say 'INFO' ("Printo Agent {0} is registered but has no service" -f $installed.DisplayVersion)
} else {
    Say 'OK' 'no previous install to trip over'
}

$installDir = Join-Path ${env:ProgramFiles} 'Printo Agent'
if (Test-Path $installDir) {
    Say 'INFO' "$installDir exists (left by a rolled-back install, or a working one)"
}

# ---------------------------------------------------------------------------------------------
if ($Install) {
    Write-Host 'Installing' -ForegroundColor Cyan

    $log = Join-Path $env:TEMP ("printo-install-{0}.log" -f (Get-Date -Format 'HHmmss'))
    $process = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/qb', '/l*v', "`"$log`"") -Wait -PassThru
    Say 'INFO' "msiexec returned $($process.ExitCode); log: $log"

    if (Test-Path $log) {
        # The failing action is the first one to return 3; everything after it is rollback.
        $failed = Select-String -Path $log -Pattern 'Action ended .*: (.+)\. Return value 3\.' |
            Where-Object { $_.Matches[0].Groups[1].Value -notin @('INSTALL', 'ExecuteAction') } |
            Select-Object -First 1

        if ($failed) {
            Say 'STOP' ("the failing action is {0}" -f $failed.Matches[0].Groups[1].Value)
        }

        # The two ways a privilege problem shows up: our own launch condition, which says so in
        # as many words, and the installer's own 1925 for a package that has no such condition.
        $privilege = Select-String -Path $log -Pattern 'installed by an administrator|sufficient privileges|error status: 1925' |
            Select-Object -First 1

        if ($privilege) {
            Say 'STOP' 'the install was refused for want of administrator rights'
            $blockers += 'This is a per-machine package: it installs a service and cannot be installed by a standard user. Install from an administrator account, or deploy it with Group Policy, which installs as the machine.'
        }

        # Policy refusals name themselves, and are worth calling out before the raw log.
        $forbidden = Select-String -Path $log -Pattern 'forbidden by system policy|1625|AppLocker|Safer' |
            Select-Object -First 1
        if ($forbidden) {
            Say 'STOP' 'the installation was refused by system policy'
            $blockers += 'Windows refused the package by policy (AppLocker or Software Restriction Policy). Sign the MSI, or deploy it by Group Policy.'
        }

        Write-Host ''
        Write-Host '  the 25 lines before the failure - this is the part worth sending:' -ForegroundColor Cyan
        $context = Get-Content $log | Select-String -Pattern 'Return value 3' -Context 25, 2 | Select-Object -First 1
        if ($context) {
            $context.Context.PreContext | ForEach-Object { Write-Host ("    " + $_.Trim()) }
            Write-Host ("    " + $context.Line.Trim()) -ForegroundColor Yellow
            $context.Context.PostContext | ForEach-Object { Write-Host ("    " + $_.Trim()) }
        } else {
            Get-Content $log -Tail 25 | ForEach-Object { Write-Host ("    " + $_.Trim()) }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Write-Host ''
if ($blockers.Count -eq 0) {
    Write-Host 'Nothing here explains a failed install.' -ForegroundColor Yellow
    Write-Host 'Run again with -Install to capture a verbose log and the action that fails.'
} else {
    Write-Host ("Found {0} thing(s) that will stop it:" -f $blockers.Count) -ForegroundColor Red
    $blockers | ForEach-Object { Write-Host "  - $_" }
}
Write-Host ''
