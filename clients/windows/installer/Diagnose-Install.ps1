<#
.SYNOPSIS
    Works out why the Printo Agent MSI will not install on this machine.

.DESCRIPTION
    Windows Installer reports almost everything as 1603, and the event log says nothing more.
    This collects the handful of things that actually produce a 1603 before the first file is
    copied - elevation, policy, a service left pending deletion, a blocked download - and, with
    -Install, runs the installation with a verbose log and reports the action that failed.

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

$service = Get-Service PrintoAgent -ErrorAction SilentlyContinue
if ($service) {
    Say 'STOP' ("the PrintoAgent service still exists (status {0})" -f $service.Status)
    $blockers += 'A service left behind by a failed install blocks the next one until the machine is restarted. Close Services and Event Viewer, restart, then install.'
} else {
    Say 'OK' 'no PrintoAgent service is registered'
}

$installed = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq 'Printo Agent' }

if ($installed) {
    Say 'INFO' ("Printo Agent {0} is registered as installed" -f $installed.DisplayVersion)
} else {
    Say 'OK' 'no Printo Agent product is registered'
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

        Write-Host ''
        Write-Host '  the log lines that carry an error:' -ForegroundColor Cyan
        Select-String -Path $log -Pattern 'Note: 1:|Error 1:|error status|MainEngineThread is returning|Product: .* -- ' |
            Select-Object -Last 12 |
            ForEach-Object { Write-Host ("    " + $_.Line.Trim()) }
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
