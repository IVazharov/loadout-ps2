<#
.SYNOPSIS
Signs the Windows app in a draft GitHub release, and can then publish the release.

.DESCRIPTION
The release workflow leaves a draft with the app's zip. The certificate's key is in
Certum's cloud, and each SimplySign login needs a code from a phone, so signing runs
on a maintainer's PC. Log in to SimplySign Desktop first.

The script signs LoadoutPS2.exe with a timestamp and replaces only that file in the
zip, so the rest is what the workflow built. It then uploads the zip and checks the
uploaded copy. The release stays a draft unless -Publish is given.

.EXAMPLE
tools/sign-release.ps1 -Tag v0.9.2

.EXAMPLE
tools/sign-release.ps1 -Tag v0.9.2 -Publish

.EXAMPLE
tools/sign-release.ps1 -LocalZip Loadout-PS2-0.9.2-win-x64.zip
Signs the app in a zip on this PC, and leaves GitHub alone.
#>
[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Release')]
    [string]$Tag,

    [Parameter(ParameterSetName = 'Release')]
    [switch]$Publish,

    [Parameter(Mandatory, ParameterSetName = 'Local')]
    [string]$LocalZip,

    # Where signtool.exe is, if it is not on PATH or in a Windows SDK.
    [string]$SignTool
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Repository = 'IVazharov/loadout-ps2'
$Publisher = 'Open Source Developer Ivan Vazharov'
$TimestampServer = 'http://time.certum.pl'
$AppFile = 'LoadoutPS2.exe'
$AppDescription = 'Loadout for PS2'

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path $File -Leaf) failed with exit code $LASTEXITCODE."
    }
}

function Find-SignTool {
    if ($SignTool) {
        if (-not (Test-Path $SignTool)) { throw "signtool was not found at $SignTool." }
        return (Resolve-Path $SignTool).Path
    }

    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $inSdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -Last 1
    if ($inSdk) { return $inSdk.FullName }

    throw 'signtool was not found. Pass its path with -SignTool. The NuGet package Microsoft.Windows.SDK.BuildTools has it.'
}

function Find-Certificate {
    $now = Get-Date
    $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like "CN=$Publisher,*" -and $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now } |
        Sort-Object NotAfter |
        Select-Object -Last 1
    if (-not $certificate) {
        throw "No valid code signing certificate for $Publisher. Log in to SimplySign Desktop, then try again."
    }

    return $certificate
}

function Assert-Signed {
    param([string]$Path, [string]$Tool)
    Invoke-Native $Tool @('verify', '/pa', '/q', $Path)
    $signature = Get-AuthenticodeSignature $Path
    if ($signature.Status -ne 'Valid') { throw "The signature on $Path is $($signature.Status)." }
    if ($signature.SignerCertificate.Subject -notlike "CN=$Publisher,*") { throw "$Path is signed by $($signature.SignerCertificate.Subject)." }
    if (-not $signature.TimeStamperCertificate) { throw "The signature on $Path has no timestamp, so it would end with the certificate." }
}

function Read-AppFromZip {
    param([string]$Zip, [string]$Destination)
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = $archive.GetEntry($AppFile)
        if (-not $entry) { throw "$Zip has no $AppFile." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    finally {
        $archive.Dispose()
    }
}

function Set-SignedAppInZip {
    param([string]$Zip, [string]$Tool, [string]$Thumbprint, [string]$WorkFolder)
    $exe = Join-Path $WorkFolder $AppFile
    Read-AppFromZip -Zip $Zip -Destination $exe

    Invoke-Native $Tool @('sign', '/sha1', $Thumbprint, '/fd', 'sha256', '/tr', $TimestampServer, '/td', 'sha256', '/d', $AppDescription, $exe)
    Assert-Signed -Path $exe -Tool $Tool

    $archive = [IO.Compression.ZipFile]::Open($Zip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry($AppFile).Delete()
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $exe, $AppFile, [IO.Compression.CompressionLevel]::Optimal)
    }
    finally {
        $archive.Dispose()
    }

    $check = Join-Path $WorkFolder 'check.exe'
    Read-AppFromZip -Zip $Zip -Destination $check
    Assert-Signed -Path $check -Tool $Tool
}

$tool = Find-SignTool
$certificate = Find-Certificate
Write-Host "Signing as $($certificate.Subject), valid to $($certificate.NotAfter)."

$work = Join-Path ([IO.Path]::GetTempPath()) "loadout-sign-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Local') {
        $zip = (Resolve-Path $LocalZip).Path
        Set-SignedAppInZip -Zip $zip -Tool $tool -Thumbprint $certificate.Thumbprint -WorkFolder $work
        Write-Host "Signed the app in $zip."
        return
    }

    $release = & gh release view $Tag --repo $Repository --json isDraft,assets | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read the release $Tag." }
    if (-not $release.isDraft) { throw "$Tag is already published. This script only changes a draft." }

    $assets = @($release.assets | Where-Object { $_.name -like 'Loadout-PS2-*-win-x64.zip' })
    if ($assets.Count -ne 1) { throw "$Tag has $($assets.Count) app zips, not one." }
    $name = $assets[0].name

    $download = Join-Path $work 'download'
    New-Item -ItemType Directory $download | Out-Null
    Invoke-Native gh @('release', 'download', $Tag, '--repo', $Repository, '--pattern', $name, '--dir', $download)
    $zip = Join-Path $download $name

    Set-SignedAppInZip -Zip $zip -Tool $tool -Thumbprint $certificate.Thumbprint -WorkFolder $work
    Invoke-Native gh @('release', 'upload', $Tag, $zip, '--repo', $Repository, '--clobber')

    $uploaded = Join-Path $work 'uploaded'
    New-Item -ItemType Directory $uploaded | Out-Null
    Invoke-Native gh @('release', 'download', $Tag, '--repo', $Repository, '--pattern', $name, '--dir', $uploaded)
    if ((Get-FileHash $zip).Hash -ne (Get-FileHash (Join-Path $uploaded $name)).Hash) {
        throw 'The uploaded zip differs from the signed one.'
    }

    Write-Host "Uploaded $name with the signed app."
    if ($Publish) {
        Invoke-Native gh @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false')
        Write-Host "Published $Tag."
    }
    else {
        Write-Host "$Tag is still a draft. Run this again with -Publish to publish it."
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
