"""
Moondream 2 AI Service — stdin/stdout JSON-line protocol
Communicates with C# MoondreamService.cs

Uses transformers + local model from PC/MoonDreamAI/ folder.
Optimized for GTX 1650 4GB VRAM with 8-bit quantization.

Protocol (JSON lines, one per message):
  REQUEST:  {"action":"load"}                          → loads model into VRAM
  REQUEST:  {"action":"unload"}                        → unloads model from VRAM
  REQUEST:  {"action":"query","id":0,"path":"...","prompt":"..."}  → runs query
  REQUEST:  {"action":"vram"}                          → returns VRAM info
  REQUEST:  {"action":"shutdown"}                      → exits process

  RESPONSE: {"type":"status","message":"..."}
  RESPONSE: {"type":"result","id":0,"answer":"YES","confidence":0.95}
  RESPONSE: {"type":"vram","used_mb":1234,"total_mb":4096,"percent":30.1}
  RESPONSE: {"type":"error","message":"..."}
"""

import sys
import json
import os
import traceback
import io
import base64

# ── Model globals ────────────────────────────────────────────────────────────
_model = None
_tokenizer = None
_device = "cuda"

def log(msg: str):
    """Write a log line to stderr (won't interfere with stdout JSON protocol)."""
    print(f"[moondream] {msg}", file=sys.stderr, flush=True)


def send_response(obj: dict):
    """Send a JSON response on stdout."""
    line = json.dumps(obj, ensure_ascii=False)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def get_vram_info() -> dict:
    """Get VRAM usage via pynvml if available, otherwise estimate via torch."""
    try:
        import pynvml
        pynvml.nvmlInit()
        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
        info = pynvml.nvmlDeviceGetMemoryInfo(handle)
        pynvml.nvmlShutdown()
        return {
            "used_mb": round(info.used / (1024 * 1024), 1),
            "total_mb": round(info.total / (1024 * 1024), 1),
            "percent": round(info.used / info.total * 100, 1)
        }
    except Exception:
        try:
            import torch
            if torch.cuda.is_available():
                # mem_get_info returns (free, total) — more reliable than memory_reserved
                free_bytes, total_bytes = torch.cuda.mem_get_info(0)
                used_bytes = total_bytes - free_bytes
                return {
                    "used_mb": round(used_bytes / (1024 * 1024), 1),
                    "total_mb": round(total_bytes / (1024 * 1024), 1),
                    "percent": round(used_bytes / total_bytes * 100, 1)
                }
        except Exception:
            pass
    return {"used_mb": 0, "total_mb": 0, "percent": 0}


def load_model():
    """Load Moondream 2 with 8-bit quantization for 4GB VRAM."""
    global _model, _tokenizer, _device

    if _model is not None:
        log("Model already loaded.")
        send_response({"type": "status", "message": "Model already loaded"})
        return

    try:
        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer
        from transformers.modeling_utils import PreTrainedModel

        log("Importing torch + transformers...")

        # Monkey-patch: newer transformers expects `all_tied_weights_keys` to be
        # a dict/set with .keys(), but moondream's custom model has _tied_weights_keys
        # as a list. Fix the attribute before loading.
        _orig_init = PreTrainedModel.__init__
        def _patched_init(self, config, *args, **kwargs):
            _orig_init(self, config, *args, **kwargs)
            val = getattr(self, "all_tied_weights_keys", None)
            if val is None or isinstance(val, list):
                alt = getattr(self, "_tied_weights_keys", None)
                if alt is not None and not isinstance(alt, list):
                    self.all_tied_weights_keys = alt
                else:
                    self.all_tied_weights_keys = {}
        PreTrainedModel.__init__ = _patched_init

        if torch.cuda.is_available():
            _device = "cuda"
            gpu_name = torch.cuda.get_device_name(0)
            vram_total = torch.cuda.get_device_properties(0).total_memory / (1024**3)
            log(f"CUDA available: {gpu_name} ({vram_total:.1f} GB VRAM)")
        else:
            _device = "cpu"
            log("CUDA NOT available, falling back to CPU (will be slow).")

        # Use local model from MoonDreamAI folder
        local_model_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "MoonDreamAI")
        local_model_dir = os.path.normpath(local_model_dir)

        if os.path.isdir(local_model_dir) and os.path.isfile(os.path.join(local_model_dir, "config.json")):
            model_id = local_model_dir
            log(f"Using local model from: {model_id}")
        else:
            model_id = "vikhyatk/moondream2"
            log(f"Local model not found at '{local_model_dir}', downloading from HuggingFace Hub: {model_id}")

        if _device == "cuda":
            # Load in fp16. The vision encoder uses custom linear() in layers.py
            # that calls F.linear(x, w.weight, w.bias) directly — it does NOT go
            # through bitsandbytes' dequantization path. So we CANNOT use 8-bit
            # quantization (it would cause "BFloat16 and Char" dtype mismatch).
            # Moondream 2 (~1.86B params) in fp16 ≈ 3.7 GB, fits in 4 GB VRAM.
            log("Loading model in fp16 (CUDA)...")
            _model = AutoModelForCausalLM.from_pretrained(
                model_id,
                trust_remote_code=True,
                device_map="auto",
                torch_dtype=torch.float16,
            )
            torch.cuda.empty_cache()
        else:
            log("Loading model on CPU (float32)...")
            _model = AutoModelForCausalLM.from_pretrained(
                model_id,
                trust_remote_code=True,
                device_map="cpu",
                torch_dtype=torch.float32,
            )

        _tokenizer = AutoTokenizer.from_pretrained(model_id, trust_remote_code=True)

        log("Model loaded successfully.")
        vram = get_vram_info()
        send_response({
            "type": "status",
            "message": "Model loaded",
            "vram": vram
        })

    except Exception as e:
        log(f"Failed to load model: {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "message": f"Failed to load model: {str(e)}"})


def unload_model():
    """Unload model from VRAM."""
    global _model, _tokenizer

    if _model is None:
        log("Model not loaded.")
        send_response({"type": "status", "message": "Model not loaded"})
        return

    try:
        import torch
        import gc

        _model = None
        _tokenizer = None

        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.synchronize()

        log("Model unloaded from VRAM.")
        vram = get_vram_info()
        send_response({
            "type": "status",
            "message": "Model unloaded",
            "vram": vram
        })

    except Exception as e:
        log(f"Error unloading model: {e}")
        send_response({"type": "error", "message": str(e)})


def query_image(req_id: int, image_path: str, prompt: str):
    """Run a query on a single image."""
    global _model, _tokenizer

    if _model is None:
        send_response({"type": "error", "id": req_id, "message": "Model not loaded"})
        return

    try:
        from PIL import Image

        if not os.path.exists(image_path):
            send_response({"type": "error", "id": req_id, "message": f"File not found: {image_path}"})
            return

        image = Image.open(image_path).convert("RGB")

        # Moondream 2 query API via the custom model's query method
        answer = _model.query(image, prompt)

        # answer is a dict like {"answer": "Yes", ...} or just a string
        if isinstance(answer, dict):
            answer_text = answer.get("answer", str(answer))
        else:
            answer_text = str(answer)

        answer_text = answer_text.strip()

        # Determine YES/NO
        is_yes = answer_text.upper().startswith("YES") or answer_text.upper() == "Y"

        send_response({
            "type": "result",
            "id": req_id,
            "answer": "YES" if is_yes else "NO",
            "raw": answer_text
        })

    except Exception as e:
        log(f"Query error (id={req_id}): {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "id": req_id, "message": str(e)})


def query_image_base64(req_id: int, image_b64: str, prompt: str):
    """Run a query on a base64-encoded image."""
    global _model, _tokenizer

    if _model is None:
        send_response({"type": "error", "id": req_id, "message": "Model not loaded"})
        return

    try:
        from PIL import Image

        image_bytes = base64.b64decode(image_b64)
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")

        answer = _model.query(image, prompt)

        if isinstance(answer, dict):
            answer_text = answer.get("answer", str(answer))
        else:
            answer_text = str(answer)

        answer_text = answer_text.strip()
        is_yes = answer_text.upper().startswith("YES") or answer_text.upper() == "Y"

        send_response({
            "type": "result",
            "id": req_id,
            "answer": "YES" if is_yes else "NO",
            "raw": answer_text
        })

    except Exception as e:
        log(f"Query error (id={req_id}): {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "id": req_id, "message": str(e)})


# ── Main loop ────────────────────────────────────────────────────────────────
def main():
    log("Moondream service started. Waiting for commands on stdin...")

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            log(f"Invalid JSON: {e}")
            send_response({"type": "error", "message": f"Invalid JSON: {e}"})
            continue

        action = req.get("action", "")

        if action == "load":
            load_model()

        elif action == "unload":
            unload_model()

        elif action == "query":
            req_id = req.get("id", 0)
            prompt = req.get("prompt", "")
            image_path = req.get("path", "")
            image_b64 = req.get("image_base64", "")

            if image_b64:
                query_image_base64(req_id, image_b64, prompt)
            elif image_path:
                query_image(req_id, image_path, prompt)
            else:
                send_response({"type": "error", "id": req_id, "message": "No image path or base64 provided"})

        elif action == "vram":
            vram = get_vram_info()
            send_response({"type": "vram", **vram})

        elif action == "shutdown":
            log("Shutting down.")
            unload_model()
            send_response({"type": "status", "message": "Shutting down"})
            sys.exit(0)

        else:
            send_response({"type": "error", "message": f"Unknown action: {action}"})


if __name__ == "__main__":
    main()