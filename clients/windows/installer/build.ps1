<#
.SYNOPSIS
    Builds the Printo Agent installers.

.DESCRIPTION
    Publishes the service and the tray self-contained for win-x64 into one directory, then
    packages that directory two ways: as an MSI with WiX, and as a self-contained EXE that
    installs without Windows Installer being involved at all.

    Both are built by default because they are for different machines. Group Policy software
    installation accepts nothing but an MSI, so the MSI is what a fleet is deployed with. The
    EXE is for the machines the MSI cannot reach - and those exist: one workstation refused our
    package for a day and turned out to be refusing every package, a signed one from another
    vendor and a file name that did not exist included, with three lines of log and 1603 before
    msiexec read a single property. A bootstrapper would not have helped, because a bootstrapper
    ends in a call to msiexec.

    Self-contained rather than framework-dependent: a domain fleet is much easier to keep
    correct when the package carries its own runtime. The alternative makes every workstation
    depend on a matching .NET version having been deployed first, and a single missing
    prerequisite is a packing bench that cannot print.

    Signing is deliberately a separate, optional step. The certificate is issued by the
    customer's internal ADCS and is not available in this repository, so the build produces
    unsigned packages and tells you how to sign them. An unsigned MSI installs perfectly well by
    GPO on a domain-joined machine; signing is what stops SmartScreen complaining when someone
    runs it by hand, and what lets the AV exclusions be scoped to a publisher rather than a
    path. It matters rather more for the EXE, which is the one a person downloads and
    double-clicks.

.PARAMETER Version
    Product version, three or four parts. Windows Installer compares only the first three, so
    two builds that differ in the fourth part will not upgrade each other. The EXE compares the
    same three, so that a machine cannot be talked into a downgrade by either package.

.PARAMETER Package
    Which installers to build: Both (the default), Msi or Exe. `Exe` is the one to ask for on a
    machine with no WiX installed - the EXE needs nothing but the .NET SDK.

.PARAMETER Portable
    Also emit a zip of the same binaries that runs without being installed. For proving the
    agent on a machine whose policy will not accept the package, and for support: it is the
    same files in the same layout, so what it does is what an installed agent does.

.PARAMETER CertificateThumbprint
    Optional. When given, the published binaries and the finished packages are
    Authenticode-signed with the matching certificate from the current user's store.

.EXAMPLE
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0

.EXAMPLE
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -Package Exe

.EXAMPLE
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -CertificateThumbprint A1B2...
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.1.0',

    [Parameter()]
    [ValidateSet('Both', 'Msi', 'Exe')]
    [string]$Package = 'Both',

    [Parameter()]
    [switch]$Portable,

    [Parameter()]
    [string]$CertificateThumbprint,

    [Parameter()]
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [Parameter()]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$clientRoot = Split-Path -Parent $here
$publishDir = Join-Path $here 'obj/publish'
$serviceDir = Join-Path $here 'obj/service'
$trayDir = Join-Path $here 'obj/tray'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $here 'bin' }

# The product mark. Committed, and drawn by tools/branding/generate_icons.py; the three
# executables take it through $(PrintoIcon) in Directory.Build.props, and the MSI needs it by
# path for its Add/Remove Programs entry.
$brandIcon = Join-Path (Split-Path -Parent (Split-Path -Parent $clientRoot)) 'assets/brand/printo.ico'
if (-not (Test-Path $brandIcon)) {
    throw "the product icon is missing at $brandIcon. Regenerate it with: python tools/branding/generate_icons.py"
}

function Assert-Tool {
    param([string]$Name, [string]$InstallHint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. $InstallHint"
    }
}

$buildMsi = $Package -in @('Both', 'Msi')
$buildExe = $Package -in @('Both', 'Exe')

Assert-Tool -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK.'

# WiX is only a prerequisite of the MSI. Asking for it unconditionally would mean a machine that
# only needs the EXE - which is the machine this whole second package exists for - could not
# build one without installing a toolchain it has no use for.
if ($buildMsi) {
    Assert-Tool -Name 'wix' -InstallHint 'Install it with: dotnet tool install --global wix --version 5.*'

    # Util supplies ServiceConfig, PermissionEx and the well-known-SID lookup; UI supplies the
    # dialogs somebody sees when they double-click the package. Both versions have to match the
    # WiX major version: `wix extension add` without one resolves to the newest package, which is
    # v7, and fails with a "could not find expected package root folder wixext5" warning and then
    # an unresolved-extension error at build time.
    $wixExtensionVersion = '5.0.2'
    foreach ($wixExtension in @('WixToolset.Util.wixext', 'WixToolset.UI.wixext')) {
        if (-not ((& wix extension list -g 2>&1) -match [regex]::Escape("$wixExtension $wixExtensionVersion"))) {
            Write-Host "==> adding $wixExtension/$wixExtensionVersion"
            & wix extension remove -g $wixExtension 2>&1 | Out-Null
            & wix extension add -g "$wixExtension/$wixExtensionVersion"
            if ($LASTEXITCODE -ne 0) { throw "could not add $wixExtension/$wixExtensionVersion" }
        }
    }
}

# A stale publish directory is the classic way to ship a file that is no longer built.
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $serviceDir) { Remove-Item -Recurse -Force $serviceDir }
if (Test-Path $trayDir) { Remove-Item -Recurse -Force $trayDir }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $serviceDir | Out-Null
New-Item -ItemType Directory -Force -Path $trayDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# Both projects publish into the same directory on purpose: they share almost every assembly,
# and two copies of the runtime would roughly double the MSI for no benefit.
foreach ($project in @('Printo.Agent.Service', 'Printo.Agent.Tray')) {
    Write-Host "==> publishing $project"
    & dotnet publish (Join-Path $clientRoot "$project/$project.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publishing $project failed" }
}

foreach ($required in @(
        'Printo.Agent.exe',
        'Printo.Tray.exe',
        'pdfium.dll',
        'Printo.Agent.Ipp.dll',
        'Microsoft.AspNetCore.Server.Kestrel.Core.dll')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        # pdfium in particular is loaded by hand at runtime, and Kestrel is what the virtual
        # printer listens with - neither absence would surface until a customer's machine tried
        # to render a page or accept a print job.
        throw "the publish output is missing $required"
    }
}

# ---------------------------------------------------------------------------------------------
# Every assembly each executable asks for has to be the one that is actually there.
#
# The two projects publish into one directory, so whichever publishes last wins any file they
# both contribute. When they targeted different Windows TFMs they contributed different versions
# of the WinRT projection: the tray's copy overwrote the service's, and the installed service
# died on startup with a FileNotFoundException naming the version its own deps.json listed. The
# MSI was well formed, every file was present, and the product did not work.
#
# So the publish output is checked against what the apps say they need, before anything is
# packaged. It costs a second and it is the only step here that would have caught that.
# ---------------------------------------------------------------------------------------------
Write-Host '==> checking the publish output against each app''s dependency manifest'

$mismatches = @()
foreach ($manifest in Get-ChildItem -Path $publishDir -Filter '*.deps.json') {
    $deps = Get-Content $manifest.FullName -Raw | ConvertFrom-Json

    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($dependency in $target.Value.PSObject.Properties) {
            $runtime = $dependency.Value.runtime
            if (-not $runtime) { continue }

            foreach ($assembly in $runtime.PSObject.Properties) {
                $wanted = $assembly.Value.assemblyVersion
                if (-not $wanted) { continue }

                $name = Split-Path $assembly.Name -Leaf
                $onDisk = Join-Path $publishDir $name
                if (-not (Test-Path $onDisk)) { continue }

                try {
                    $actual = [System.Reflection.AssemblyName]::GetAssemblyName($onDisk).Version.ToString()
                } catch {
                    continue  # native or unmanaged: nothing to compare
                }

                # Only an *older* file is a fault. A framework facade routinely carries a
                # higher implementation version than the reference version recorded here, and
                # the runtime rolls forward to it happily; what it cannot do is roll backwards,
                # which is exactly what one publish overwriting another produces.
                if ([version]$actual -lt [version]$wanted) {
                    $mismatches += "$($manifest.Name) wants $name $wanted, the published file is $actual"
                }
            }
        }
    }
}

if ($mismatches.Count -gt 0) {
    $mismatches | Sort-Object -Unique | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    throw 'the published assemblies do not match what the applications ask for; the two publishes have overwritten each other'
}

# Debug symbols are not part of a production install: they are a few megabytes per build and
# they hand an attacker a map of the binary for nothing in return. Crash diagnosis uses the
# symbols kept with the build, not the ones on the workstation.
Get-ChildItem -Path $publishDir -Filter '*.pdb' -Recurse | Remove-Item -Force

# Both executables are moved out of the harvested tree because neither can be harvested: the
# service exe carries the ServiceInstall, and the tray exe has to be referenced by id from the
# shortcuts and from the exit dialog's "open the settings" action. WiX 5's `Files` element has
# no `Exclude`, so a file in both the glob and an explicit component is a duplicate-file error.
Move-Item -Path (Join-Path $publishDir 'Printo.Agent.exe') -Destination $serviceDir
Move-Item -Path (Join-Path $publishDir 'Printo.Tray.exe') -Destination $trayDir

if ($CertificateThumbprint) {
    Assert-Tool -Name 'signtool' -InstallHint 'Install the Windows SDK signing tools.'

    Write-Host '==> signing the published binaries'
    # The binaries are signed before packaging: signing only the MSI leaves the installed
    # executables unsigned, and it is those that AV inspects every time the service starts.
    & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 `
        (Join-Path $serviceDir 'Printo.Agent.exe') (Join-Path $trayDir 'Printo.Tray.exe')
    if ($LASTEXITCODE -ne 0) { throw 'signing the binaries failed' }
}

# ---------------------------------------------------------------------------------------------
# One staged copy of the installed product, which both the EXE and the portable zip are made
# from. It is the publish directory with the two executables put back: they are only ever
# separated for WiX's benefit, and nothing outside the MSI wants them anywhere but beside the
# assemblies they load.
# ---------------------------------------------------------------------------------------------
$payloadDir = Join-Path $here 'obj/payload'

function New-PayloadDirectory {
    if (Test-Path $payloadDir) { Remove-Item -Recurse -Force $payloadDir }
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    Copy-Item (Join-Path $publishDir '*') $payloadDir -Recurse -Force
    Copy-Item (Join-Path $serviceDir 'Printo.Agent.exe') $payloadDir -Force
    Copy-Item (Join-Path $trayDir 'Printo.Tray.exe') $payloadDir -Force
}

if ($buildExe -or $Portable) { New-PayloadDirectory }

$built = @()

if ($buildMsi) {
    $msi = Join-Path $OutputDirectory "PrintoAgent-$Version.msi"

    Write-Host "==> building $msi"
    & wix build `
        (Join-Path $here 'Printo.Agent.wxs') `
        (Join-Path $here 'Printo.Agent.Payload.wxs') `
        -ext WixToolset.Util.wixext `
        -ext WixToolset.UI.wixext `
        -arch x64 `
        -define "ProductVersion=$Version" `
        -define "PublishDir=$publishDir" `
        -define "ServiceDir=$serviceDir" `
        -define "TrayDir=$trayDir" `
        -define "NoticeRtf=$(Join-Path $here 'Notice.rtf')" `
        -define "BrandIcon=$brandIcon" `
        -out $msi
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

    if ($CertificateThumbprint) {
        Write-Host '==> signing the MSI'
        & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $msi
        if ($LASTEXITCODE -ne 0) { throw 'signing the MSI failed' }
    }

    $built += $msi
}

if ($buildExe) {
    # ------------------------------------------------------------------------------------------
    # The EXE carries the same files, zipped and embedded in it, and installs them itself: it
    # copies the directory, registers the service with the service control manager, writes the
    # same registry values the MSI writes, and leaves an Add/Remove Programs entry that runs it
    # again with /uninstall. No part of that goes through Windows Installer, which is the whole
    # point of it - see Printo.Agent.Setup/Program.cs.
    # ------------------------------------------------------------------------------------------
    $payloadZip = Join-Path $here 'obj/payload.zip'
    if (Test-Path $payloadZip) { Remove-Item -Force $payloadZip }

    Write-Host '==> compressing the payload'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $setupDir = Join-Path $here 'obj/setup'
    if (Test-Path $setupDir) { Remove-Item -Recurse -Force $setupDir }

    Write-Host '==> building the EXE installer'
    & dotnet publish (Join-Path $clientRoot 'Printo.Agent.Setup/Printo.Agent.Setup.csproj') `
        --configuration Release `
        -p:Version=$Version `
        -p:PayloadZip=$payloadZip `
        --output $setupDir
    if ($LASTEXITCODE -ne 0) { throw 'building the EXE installer failed' }

    $exe = Join-Path $OutputDirectory "PrintoAgent-$Version.exe"
    Copy-Item (Join-Path $setupDir 'Printo.Setup.exe') $exe -Force

    # The payload is embedded, so this is the one check that it actually got in: a setup program
    # that installs nothing looks exactly like one that installs everything until it is run.
    if ((Get-Item $exe).Length -lt 20MB) {
        throw "the EXE installer is only $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, which means the payload was not embedded"
    }

    if ($CertificateThumbprint) {
        Write-Host '==> signing the EXE'
        # This is the package a person downloads and double-clicks, so an unsigned one is the
        # one SmartScreen complains about loudest.
        & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
        if ($LASTEXITCODE -ne 0) { throw 'signing the EXE failed' }
    }

    $built += $exe
}

if (-not $CertificateThumbprint) {
    Write-Host ''
    Write-Host 'The packages are UNSIGNED. To sign them with the internal ADCS certificate:'
    Write-Host "  pwsh $($MyInvocation.MyCommand.Path) -Version $Version -CertificateThumbprint <thumbprint>"
}

if ($Portable) {
    # The same files the MSI installs, in the same directory, plus the two things a person
    # needs to know to run them. No service registration and no virtual printer: those are
    # machine changes and belong to the installer.
    $portableDir = Join-Path $here 'obj/portable'
    if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
    New-Item -ItemType Directory -Force -Path $portableDir | Out-Null

    Copy-Item (Join-Path $payloadDir '*') $portableDir -Recurse -Force

    @"
Printo Agent $Version - portable

The same binaries the installer lays down, run from here instead. Nothing is registered, no
service is created and no printer is added, so this changes nothing on the machine beyond the
data directory below.

  Run-Agent.cmd        the agent, in this window. Ctrl+C stops it.
  Settings.cmd         map this machine's printers, and the watched folders.

Both use .\printo-data beside these files rather than C:\ProgramData\Printogent.

One exception, and it is deliberate: if the agent is also *installed* on this machine, the
installed data directory wins. Values written by the installer and by Group Policy outrank a
configuration file, which is what stops two copies of the agent fighting over one job queue.
Run `Printo.Agent.exe --show-config` to see which directory is in force and where that came
from.

The virtual printer needs a Windows queue pointing at the agent, and creating one needs an
administrator. With the agent running, from an elevated prompt:

  Add-Printer -Name Printo -IppURL http://127.0.0.1:39631/ipp/print

Remove it again with:

  Remove-Printer -Name Printo
"@ | Set-Content -Path (Join-Path $portableDir 'README.txt') -Encoding utf8

    @"
{
  "dataDirectory": "printo-data",
  "decisionMode": "Local",
  "virtualPrinter": { "enabled": true, "port": 39631, "manageQueue": false }
}
"@ | Set-Content -Path (Join-Path $portableDir 'agent.json') -Encoding utf8

    '@echo off' + "`r`n" + 'cd /d "%~dp0"' + "`r`n" + 'Printo.Agent.exe --console --config "%~dp0agent.json"' |
        Set-Content -Path (Join-Path $portableDir 'Run-Agent.cmd') -Encoding ascii

    '@echo off' + "`r`n" + 'cd /d "%~dp0"' + "`r`n" + 'start "" Printo.Tray.exe --settings --config "%~dp0agent.json"' |
        Set-Content -Path (Join-Path $portableDir 'Settings.cmd') -Encoding ascii

    $zip = Join-Path $OutputDirectory "PrintoAgent-$Version-portable.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

Write-Host ''
foreach ($artifact in $built) {
    Write-Host "built $artifact ($([math]::Round((Get-Item $artifact).Length / 1MB, 1)) MB)"
}
