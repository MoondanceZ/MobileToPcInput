import json
import os
import sys
import time
import wave
import base64
from json import JSONDecodeError
from pathlib import Path


sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

MODEL_REVISION = os.environ.get("FUNASR_MODEL_REVISION", "v2.0.5")
ASR_MODEL = os.environ.get(
    "FUNASR_ASR_MODEL",
    "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-onnx",
)
PUNC_MODEL = os.environ.get(
    "FUNASR_PUNC_MODEL",
    "iic/punc_ct-transformer_zh-cn-common-vocab272727-onnx",
)


def log(message):
    print(message, file=sys.stderr, flush=True)


def write_json(payload):
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def get_model_cache_path(model_name):
    short_name = model_name.split("/")[-1]
    model_dir = Path.home() / ".cache" / "modelscope" / "hub" / "models" / "iic" / short_name
    if (model_dir / "model_quant.onnx").exists() or (model_dir / "model.onnx").exists():
        log(f"using cached model: {model_dir}")
        return str(model_dir)

    log(f"model cache missing, downloading once: {model_name}")
    from modelscope.hub.snapshot_download import snapshot_download

    try:
        return snapshot_download(model_name, revision=MODEL_REVISION, local_files_only=True)
    except Exception:
        return snapshot_download(model_name, revision=MODEL_REVISION)


def load_models():
    start = time.time()
    os.environ.setdefault("OMP_NUM_THREADS", "8")

    from funasr_onnx.paraformer_bin import Paraformer
    from funasr_onnx.punc_bin import CT_Transformer

    asr_dir = get_model_cache_path(ASR_MODEL)
    punc_dir = get_model_cache_path(PUNC_MODEL)
    asr_quantize = Path(asr_dir, "model_quant.onnx").exists()
    punc_quantize = Path(punc_dir, "model_quant.onnx").exists()
    threads = int(os.environ.get("OMP_NUM_THREADS", "8"))

    log(f"loading ASR ONNX model. quantize={asr_quantize}, threads={threads}")
    asr_model = Paraformer(
        asr_dir,
        batch_size=1,
        device_id=-1,
        quantize=asr_quantize,
        intra_op_num_threads=threads,
    )

    log(f"loading punctuation ONNX model. quantize={punc_quantize}, threads={threads}")
    punc_model = CT_Transformer(
        punc_dir,
        batch_size=1,
        device_id=-1,
        quantize=punc_quantize,
        intra_op_num_threads=threads,
    )

    log(f"ONNX models ready in {time.time() - start:.2f}s")
    return asr_model, punc_model


def warmup_models(asr_model, punc_model):
    start = time.time()
    warmup_path = Path(os.environ.get("TEMP", ".")) / "mobiletopcinput-asr-warmup.wav"
    if not warmup_path.exists():
        with wave.open(str(warmup_path), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(16000)
            wav.writeframes(b"\x00\x00" * 16000)

    try:
        asr_model([str(warmup_path)])
    except Exception as exc:
        log(f"ASR warm-up inference ignored: {exc}")

    try:
        punc_model("你好")
    except Exception as exc:
        log(f"punctuation warm-up ignored: {exc}")

    log(f"ONNX inference warm-up done in {time.time() - start:.2f}s")


def extract_text(result):
    if isinstance(result, list) and result:
        first = result[0]
        if isinstance(first, dict):
            if "text" in first:
                return str(first["text"])
            if "preds" in first:
                preds = first["preds"]
                if isinstance(preds, tuple) and preds:
                    return str(preds[0])
                return str(preds)

        return str(first)

    return str(result or "")


def normalize_json_line(line):
    line = line.lstrip("\ufeff")
    if line.startswith("{"):
        return line

    brace_index = line.find("{")
    if brace_index >= 0:
        return line[brace_index:]

    quote_id_index = line.find('"id"')
    if quote_id_index >= 0:
        return "{" + line[quote_id_index:]

    return line


def main():
    try:
        asr_model, punc_model = load_models()
        warmup_models(asr_model, punc_model)
        write_json({"type": "ready"})

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            line = normalize_json_line(line)

            request = None
            try:
                try:
                    request = json.loads(line)
                except JSONDecodeError as exc:
                    write_json({
                        "type": "error",
                        "id": None,
                        "error": f"{exc}; raw={line[:120]!r}",
                    })
                    continue

                request_id = request.get("id")
                if "wav_b64" in request:
                    wav_path = base64.b64decode(request["wav_b64"]).decode("utf-8")
                else:
                    wav_path = request["wav"]

                start = time.time()
                asr_result = asr_model([wav_path])
                raw_text = extract_text(asr_result).strip()
                final_text = raw_text
                if raw_text:
                    try:
                        punc_result = punc_model(raw_text)
                        final_text = str(punc_result[0] if isinstance(punc_result, tuple) else punc_result)
                    except Exception as punc_exc:
                        log(f"punctuation failed, raw text used: {punc_exc}")

                log(f"recognition done in {time.time() - start:.2f}s, textLength={len(final_text)}")
                write_json({"type": "result", "id": request_id, "text": final_text})
            except Exception as exc:
                write_json({
                    "type": "error",
                    "id": request.get("id") if request else None,
                    "error": str(exc),
                })
    except Exception as exc:
        write_json({"type": "fatal", "error": str(exc)})
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
