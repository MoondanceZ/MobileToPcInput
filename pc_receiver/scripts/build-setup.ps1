param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$WixVersion = "5.0.2",
    [switch]$SkipMsiBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$repositoryRoot = Split-Path -Parent $root
$bundleSource = Join-Path $root "Installer\Bundle.wxs"
$buildMsiScript = Join-Path $root "scripts\build-msi.ps1"
$releaseOutput = Join-Path $repositoryRoot "publish"

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

Ensure-WixExtension "WixToolset.BootstrapperApplications.wixext"

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
    -d "ProjectDir=$root" `
    -d "MsiPath=$msi" `
    -d "ProductVersion=$version" `
    -out $setup
if ($LASTEXITCODE -ne 0) {
    throw "WiX bundle build failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Bundled setup created:"
Write-Host $setup
Write-Host "VB-CABLE is not bundled. The app checks for it when bridge input is selected."
