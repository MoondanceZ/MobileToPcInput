$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $root "asr_runtime\.venv"
$python = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $python)) {
    python -m venv $venv
}

& $python -m pip install --upgrade pip
& $python -m pip install funasr-onnx modelscope soundfile

Write-Host "ASR runtime is ready: $python"
