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
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $here 'bin' }

function Assert-Tool {
    param([string]$Name, [string]$InstallHint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. $InstallHint"
    }
}

Assert-Tool -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK.'
Assert-Tool -Name 'wix' -InstallHint 'Install it with: dotnet tool install --global wix --version 5.*'

# The Util extension supplies ServiceConfig and PermissionEx. Its version has to match the WiX
# major version: `wix extension add` without one resolves to the newest package, which is v7 and
# fails with a "could not find expected package root folder wixext5" warning and then an
# unresolved-extension error at build time.
$wixExtension = 'WixToolset.Util.wixext'
$wixExtensionVersion = '5.0.2'
if (-not ((& wix extension list -g 2>&1) -match [regex]::Escape("$wixExtension $wixExtensionVersion"))) {
    Write-Host "==> adding $wixExtension/$wixExtensionVersion"
    & wix extension remove -g $wixExtension 2>&1 | Out-Null
    & wix extension add -g "$wixExtension/$wixExtensionVersion"
    if ($LASTEXITCODE -ne 0) { throw "could not add $wixExtension/$wixExtensionVersion" }
}

# A stale publish directory is the classic way to ship a file that is no longer built.
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $serviceDir) { Remove-Item -Recurse -Force $serviceDir }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $serviceDir | Out-Null
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

foreach ($required in @('Printo.Agent.exe', 'Printo.Tray.exe', 'pdfium.dll')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        # pdfium in particular is loaded by hand at runtime, so its absence would not surface
        # until the agent tried to render a page on a customer's machine.
        throw "the publish output is missing $required"
    }
}

# Debug symbols are not part of a production install: they are a few megabytes per build and
# they hand an attacker a map of the binary for nothing in return. Crash diagnosis uses the
# symbols kept with the build, not the ones on the workstation.
Get-ChildItem -Path $publishDir -Filter '*.pdb' -Recurse | Remove-Item -Force

# The service executable is moved out of the harvested tree because it cannot be harvested: its
# component carries the ServiceInstall, and WiX 5's `Files` element has no `Exclude`, so a file
# that appears in both the glob and an explicit component is a duplicate-file error.
Move-Item -Path (Join-Path $publishDir 'Printo.Agent.exe') -Destination $serviceDir

if ($CertificateThumbprint) {
    Assert-Tool -Name 'signtool' -InstallHint 'Install the Windows SDK signing tools.'

    Write-Host '==> signing the published binaries'
    # The binaries are signed before packaging: signing only the MSI leaves the installed
    # executables unsigned, and it is those that AV inspects every time the service starts.
    & signtool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 `
        (Join-Path $serviceDir 'Printo.Agent.exe') (Join-Path $publishDir 'Printo.Tray.exe')
    if ($LASTEXITCODE -ne 0) { throw 'signing the binaries failed' }
}

$msi = Join-Path $OutputDirectory "PrintoAgent-$Version.msi"

Write-Host "==> building $msi"
& wix build `
    (Join-Path $here 'Printo.Agent.wxs') `
    (Join-Path $here 'Printo.Agent.Payload.wxs') `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -define "ProductVersion=$Version" `
    -define "PublishDir=$publishDir" `
    -define "ServiceDir=$serviceDir" `
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
