param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$WixVersion = "5.0.2",
    [string]$VbCableZip,
    [switch]$SkipMsiBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$repositoryRoot = Split-Path -Parent $root
$bundleSource = Join-Path $root "Installer\Bundle.wxs"
$buildMsiScript = Join-Path $root "scripts\build-msi.ps1"
$artifacts = Join-Path $root "artifacts"
$releaseOutput = Join-Path $repositoryRoot "publish"
$dependencyCache = Join-Path $artifacts "dependencies"
$bundleWork = Join-Path $root "obj\bundle"
$vbCableDirectory = Join-Path $bundleWork "VBCABLE_Driver_Pack45"
$vbCableFileName = "VBCABLE_Driver_Pack45.zip"
$vbCableDownloadUrl = "https://download.vb-audio.com/Download_CABLE/$vbCableFileName"
$vbCableSha256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB"

function Get-ProjectVersion {
    [xml]$projectXml = Get-Content (Join-Path $root "pc_receiver.csproj")
    $version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "pc_receiver.csproj does not contain a <Version> value."
    }

    return $version.Trim()
}

function Ensure-WixExtension {
    param([string]$Name)

    $installed = (& wix extension list --global 2>$null) -join "`n"
    if ($installed -match [regex]::Escape($Name)) {
        return
    }

    Write-Host "Installing WiX extension $Name $WixVersion..."
    wix extension add --global "$Name/$WixVersion" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to install WiX extension $Name."
    }
}

function Resolve-VbCablePackage {
    New-Item -ItemType Directory -Force -Path $dependencyCache | Out-Null

    if ([string]::IsNullOrWhiteSpace($VbCableZip)) {
        $script:VbCableZip = Join-Path $dependencyCache $vbCableFileName
    }

    if (-not (Test-Path -LiteralPath $VbCableZip)) {
        Write-Host "Downloading official VB-CABLE package..."
        Invoke-WebRequest -Uri $vbCableDownloadUrl -OutFile $VbCableZip
    }

    $actualHash = (Get-FileHash -LiteralPath $VbCableZip -Algorithm SHA256).Hash
    if ($actualHash -ne $vbCableSha256) {
        throw "VB-CABLE package checksum mismatch. Expected $vbCableSha256, got $actualHash. Review the official package before updating the pinned checksum."
    }

    if (Test-Path -LiteralPath $vbCableDirectory) {
        $resolvedWork = [System.IO.Path]::GetFullPath($bundleWork)
        $resolvedTarget = [System.IO.Path]::GetFullPath($vbCableDirectory)
        if (-not $resolvedTarget.StartsWith($resolvedWork, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear a VB-CABLE directory outside the bundle work directory."
        }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $vbCableDirectory | Out-Null
    Expand-Archive -LiteralPath $VbCableZip -DestinationPath $vbCableDirectory

    $requiredFiles = @(
        "VBCABLE_Setup_x64.exe",
        "vbMmeCable64_win10.inf",
        "vbaudio_cable64_win10.cat",
        "vbaudio_cable64_win10.sys"
    )
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $vbCableDirectory $file))) {
            throw "Official VB-CABLE package is incomplete. Missing: $file"
        }
    }
}

$version = Get-ProjectVersion
New-Item -ItemType Directory -Force -Path $releaseOutput | Out-Null

if (-not $SkipMsiBuild) {
    & $buildMsiScript -Configuration $Configuration -Runtime $Runtime -WixVersion $WixVersion
    if ($LASTEXITCODE -ne 0) {
        throw "MSI build failed with exit code $LASTEXITCODE."
    }
}

$msi = Join-Path $releaseOutput "MobileToPcInput-$version-x64.msi"
if (-not (Test-Path -LiteralPath $msi)) {
    throw "Missing MSI package: $msi"
}

Resolve-VbCablePackage
Ensure-WixExtension "WixToolset.BootstrapperApplications.wixext"
Ensure-WixExtension "WixToolset.Util.wixext"

$setup = Join-Path $releaseOutput "MobileToPcInput-Setup-$version-x64.exe"
$setupPdb = [System.IO.Path]::ChangeExtension($setup, ".wixpdb")
if (Test-Path -LiteralPath $setupPdb) {
    Remove-Item -LiteralPath $setupPdb -Force
}

Write-Host "Building bundled setup: $setup"
wix build $bundleSource `
    -arch x64 `
    -pdbtype none `
    -ext WixToolset.BootstrapperApplications.wixext `
    -ext WixToolset.Util.wixext `
    -d "ProjectDir=$root" `
    -d "MsiPath=$msi" `
    -d "VbCableDir=$vbCableDirectory" `
    -d "ProductVersion=$version" `
    -out $setup
if ($LASTEXITCODE -ne 0) {
    throw "WiX bundle build failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Bundled setup created:"
Write-Host $setup
Write-Host "VB-CABLE is installed only when the VBAudioVACMME driver service is absent."
Write-Host "A Windows driver confirmation and system restart may still be required."
