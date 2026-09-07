<#
.SYNOPSIS
    Builds the Printo Agent MSI.

.DESCRIPTION
    Publishes the service and the tray self-contained for win-x64 into one directory, then
    packages that directory with WiX.

    Self-contained rather than framework-dependent: a domain fleet is much easier to keep
    correct when the MSI carries its own runtime. The alternative makes every workstation
    depend on a matching .NET version having been deployed first, and a single missing
    prerequisite is a packing bench that cannot print.

    Signing is deliberately a separate, optional step. The certificate is issued by the
    customer's internal ADCS and is not available in this repository, so the build produces an
    unsigned MSI and tells you how to sign it. An unsigned MSI installs perfectly well by GPO
    on a domain-joined machine; signing is what stops SmartScreen complaining when someone runs
    it by hand, and what lets the AV exclusions be scoped to a publisher rather than a path.

.PARAMETER Version
    Product version, three or four parts. Windows Installer compares only the first three, so
    two builds that differ in the fourth part will not upgrade each other.

.PARAMETER CertificateThumbprint
    Optional. When given, the published binaries and the finished MSI are Authenticode-signed
    with the matching certificate from the current user's store.

.EXAMPLE
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0

.EXAMPLE
    pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -CertificateThumbprint A1B2...
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.1.0',

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

function Assert-Tool {
    param([string]$Name, [string]$InstallHint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. $InstallHint"
    }
}

Assert-Tool -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK.'
Assert-Tool -Name 'wix' -InstallHint 'Install it with: dotnet tool install --global wix --version 5.*'

# Util supplies ServiceConfig, PermissionEx and the well-known-SID lookup; UI supplies the
# dialogs somebody sees when they double-click the package. Both versions have to match the WiX
# major version: `wix extension add` without one resolves to the newest package, which is v7,
# and fails with a "could not find expected package root folder wixext5" warning and then an
# unresolved-extension error at build time.
$wixExtensionVersion = '5.0.2'
foreach ($wixExtension in @('WixToolset.Util.wixext', 'WixToolset.UI.wixext')) {
    if (-not ((& wix extension list -g 2>&1) -match [regex]::Escape("$wixExtension $wixExtensionVersion"))) {
        Write-Host "==> adding $wixExtension/$wixExtensionVersion"
        & wix extension remove -g $wixExtension 2>&1 | Out-Null
        & wix extension add -g "$wixExtension/$wixExtensionVersion"
        if ($LASTEXITCODE -ne 0) { throw "could not add $wixExtension/$wixExtensionVersion" }
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
        foreach ($package in $target.Value.PSObject.Properties) {
            $runtime = $package.Value.runtime
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
    -out $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

if ($CertificateThumbprint) {
    Write-Host '==> signing the MSI'
    & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $msi
    if ($LASTEXITCODE -ne 0) { throw 'signing the MSI failed' }
} else {
    Write-Host ''
    Write-Host 'The MSI is UNSIGNED. To sign it with the internal ADCS certificate:'
    Write-Host "  pwsh $($MyInvocation.MyCommand.Path) -Version $Version -CertificateThumbprint <thumbprint>"
}

Write-Host ''
Write-Host "built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
