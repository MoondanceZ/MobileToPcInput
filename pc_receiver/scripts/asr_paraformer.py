import json
import sys
from contextlib import redirect_stdout


MODEL_ID = "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch"


def main() -> int:
    if len(sys.argv) < 2:
        print(json.dumps({"error": "missing wav path"}, ensure_ascii=False), file=sys.stderr)
        return 2

    wav_path = sys.argv[1]
    try:
        with redirect_stdout(sys.stderr):
            from funasr import AutoModel

            model = AutoModel(
                model=MODEL_ID,
                vad_model="fsmn-vad",
                punc_model="ct-punc",
                disable_update=True,
            )
            result = model.generate(input=wav_path)
        text = ""
        if result:
            text = result[0].get("text", "")

        print(json.dumps({"text": text}, ensure_ascii=False))
        return 0
    except Exception as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
