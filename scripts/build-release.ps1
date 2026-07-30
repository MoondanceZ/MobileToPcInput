param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$WixVersion = "5.0.2"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pcRoot = Join-Path $repositoryRoot "pc_receiver"
$mobileRoot = Join-Path $repositoryRoot "mobile_app"
$releaseOutput = Join-Path $repositoryRoot "publish"
$buildMsiScript = Join-Path $pcRoot "scripts\build-msi.ps1"
$pcProject = Join-Path $pcRoot "pc_receiver.csproj"
$mobileManifest = Join-Path $mobileRoot "pubspec.yaml"

function Get-PcVersion {
    [xml]$projectXml = Get-Content -LiteralPath $pcProject
    $version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "pc_receiver.csproj does not contain a <Version> value."
    }

    return $version.Trim()
}

function Get-MobileVersion {
    $versionLine = Get-Content -LiteralPath $mobileManifest |
        Where-Object { $_ -match '^\s*version:\s*(\S+)\s*$' } |
        Select-Object -First 1
    if (-not $versionLine) {
        throw "mobile_app/pubspec.yaml does not contain a version value."
    }

    $version = ([regex]::Match($versionLine, '^\s*version:\s*(\S+)\s*$')).Groups[1].Value
    return ($version -split '\+')[0]
}

$pcVersion = Get-PcVersion
$mobileVersion = Get-MobileVersion
if ($pcVersion -ne $mobileVersion) {
    throw "PC version ($pcVersion) and Android version ($mobileVersion) must match."
}

New-Item -ItemType Directory -Force -Path $releaseOutput | Out-Null

Write-Host "Building Android release APK..."
Push-Location $mobileRoot
try {
    flutter build apk --release
    if ($LASTEXITCODE -ne 0) {
        throw "flutter build apk failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$builtApk = Join-Path $mobileRoot "build\app\outputs\flutter-apk\app-release.apk"
if (-not (Test-Path -LiteralPath $builtApk)) {
    throw "Missing Android release APK: $builtApk"
}

$releaseApk = Join-Path $releaseOutput "MobileToPcInput-Android-$mobileVersion.apk"
Copy-Item -LiteralPath $builtApk -Destination $releaseApk -Force

Write-Host "Building PC release MSI..."
& $buildMsiScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -WixVersion $WixVersion
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

$releaseMsi = Join-Path $releaseOutput "MobileToPcInput-$pcVersion-x64.msi"
if (-not (Test-Path -LiteralPath $releaseMsi)) {
    throw "Missing PC release MSI: $releaseMsi"
}

Write-Host ""
Write-Host "Release files created:"
Write-Host $releaseApk
Write-Host $releaseMsi
