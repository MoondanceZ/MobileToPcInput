$ErrorActionPreference = "Stop"

$modelRoot = Join-Path $env:USERPROFILE ".cache\modelscope\hub\models\iic"
$defaultModel = Join-Path $modelRoot "speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx"
$requiredAny = @(
    @("model_quant.onnx", "model.onnx"),
    @("config.yaml", "asr.yaml"),
    @("tokens.json", "tokens.txt"),
    @("am.mvn")
)

Write-Host "MobileToPcInput now uses the pure C# ONNX Paraformer runtime."
Write-Host "Python, venv, pip, funasr-onnx and modelscope are not required at runtime."
Write-Host ""
Write-Host "Default model directory:"
Write-Host "  $defaultModel"
Write-Host ""

if (-not (Test-Path $defaultModel)) {
    throw "Model directory does not exist: $defaultModel"
}

$missing = @()
foreach ($group in $requiredAny) {
    $found = $false
    foreach ($file in $group) {
        if (Test-Path (Join-Path $defaultModel $file)) {
            $found = $true
            break
        }
    }

    if (-not $found) {
        $missing += ($group -join " or ")
    }
}

if ($missing.Count -gt 0) {
    Write-Host "Missing model files:"
    foreach ($item in $missing) {
        Write-Host "  $item"
    }
    throw "Model files are incomplete."
}

Write-Host "ASR model files are ready."
