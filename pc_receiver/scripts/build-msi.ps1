param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$WixVersion = "5.0.2",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$project = Join-Path $root "pc_receiver.csproj"
$installer = Join-Path $root "Installer\Product.wxs"
$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $root "bin\$Configuration\net10.0-windows\$Runtime\publish"

function Get-ProjectVersion {
    [xml]$projectXml = Get-Content $project
    $version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "pc_receiver.csproj does not contain a <Version> value."
    }

    return $version.Trim()
}

function Ensure-Wix {
    $wix = Get-Command wix -ErrorAction SilentlyContinue
    if ($wix) {
        $currentVersion = (& wix --version).Trim()
        Write-Host "WiX detected: $currentVersion"
        if ($currentVersion.StartsWith("7.")) {
            Write-Host "WiX 7 requires the OSMF EULA. Installing WiX $WixVersion for unattended local builds..."
            dotnet tool uninstall --global wix | Out-Host
            dotnet tool install --global wix --version $WixVersion | Out-Host
        }
        return
    }

    Write-Host "WiX is not installed. Installing WiX $WixVersion..."
    dotnet tool install --global wix --version $WixVersion | Out-Host
}

function Ensure-WixUiExtension {
    $extension = "WixToolset.UI.wixext"
    $installed = (& wix extension list --global 2>$null) -join "`n"
    if ($installed -match [regex]::Escape($extension)) {
        Write-Host "WiX UI extension detected."
        return
    }

    Write-Host "Installing WiX UI extension..."
    wix extension add --global "$extension/$WixVersion" | Out-Host
}

$version = Get-ProjectVersion
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if (-not $SkipPublish) {
    Write-Host "Publishing MobileToPcInput AOT ($Configuration, $Runtime)..."
    dotnet publish $project -c $Configuration -r $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

$requiredFiles = @(
    "MobileToPcInput.exe",
    "av_libglesv2.dll",
    "kaldi-native-fbank.dll",
    "libHarfBuzzSharp.dll",
    "libSkiaSharp.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "sherpa-onnx-c-api.dll"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $publishDir $file
    if (-not (Test-Path $path)) {
        throw "Missing publish file: $path"
    }
}

$legacyAsrRuntimeZip = Join-Path $publishDir "asr_runtime.zip"
if (Test-Path $legacyAsrRuntimeZip) {
    Write-Host "Removing stale Python ASR runtime bundle: $legacyAsrRuntimeZip"
    Remove-Item -LiteralPath $legacyAsrRuntimeZip -Force
}

$legacyScriptsDir = Join-Path $publishDir "scripts"
if (Test-Path $legacyScriptsDir) {
    Write-Host "Removing stale Python ASR scripts: $legacyScriptsDir"
    Remove-Item -LiteralPath $legacyScriptsDir -Recurse -Force
}

$legacyFunasrDir = Join-Path $publishDir "funasr"
if (Test-Path $legacyFunasrDir) {
    Write-Host "Removing stale native FunASR bundle: $legacyFunasrDir"
    Remove-Item -LiteralPath $legacyFunasrDir -Recurse -Force
}

Ensure-Wix
Ensure-WixUiExtension

$msi = Join-Path $artifacts "MobileToPcInput-$version-x64.msi"
Write-Host "Building MSI: $msi"
wix build $installer `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "ProjectDir=$root" `
    -d "PublishDir=$publishDir" `
    -d "ProductVersion=$version" `
    -out $msi
if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "MSI created:"
Write-Host $msi
