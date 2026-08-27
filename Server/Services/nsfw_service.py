"""
NSFW Image Detection Service — stdin/stdout JSON-line protocol
Same protocol format as smolvlm_service.py for compatibility.
Uses Falconsai/nsfw_image_detection (ViT-based, 86M params, ~300MB VRAM).

Protocol:
  REQUEST:  {"action":"load"}                          -> loads model
  REQUEST:  {"action":"unload"}                        -> unloads model
  REQUEST:  {"action":"query","id":0,"image_base64":"...","prompt":"ignored"}
  REQUEST:  {"action":"vram"}                          -> returns VRAM info
  REQUEST:  {"action":"shutdown"}                      -> exits

  RESPONSE: {"type":"status","message":"Model loaded","vram":{...}}
  RESPONSE: {"type":"result","id":0,"answer":"YES","raw":"normal:0.95, porn:0.02,..."}
  RESPONSE: {"type":"error","message":"..."}
"""
import sys
import json
import io
import base64
import traceback

_model = None
_processor = None
_device = "cuda"
NSFW_LABELS = ["normal", "nsfw"]


def log(msg: str):
    print(f"[nsfw] {msg}", file=sys.stderr, flush=True)


def send_response(obj: dict):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def get_vram_info() -> dict:
    try:
        import pynvml
        pynvml.nvmlInit()
        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
        info = pynvml.nvmlDeviceGetMemoryInfo(handle)
        pynvml.nvmlShutdown()
        return {"used_mb": round(info.used / 1048576, 1), "total_mb": round(info.total / 1048576, 1),
                "percent": round(info.used / info.total * 100, 1)}
    except Exception:
        try:
            import torch
            if torch.cuda.is_available():
                free, total = torch.cuda.mem_get_info(0)
                used = total - free
                return {"used_mb": round(used / 1048576, 1), "total_mb": round(total / 1048576, 1),
                        "percent": round(used / total * 100, 1)}
        except Exception:
            pass
    return {"used_mb": 0, "total_mb": 0, "percent": 0}


def load_model():
    global _model, _processor, _device

    if _model is not None:
        log("Already loaded.")
        send_response({"type": "status", "message": "Model already loaded"})
        return

    try:
        import torch
        from transformers import AutoImageProcessor, AutoModelForImageClassification

        if torch.cuda.is_available():
            _device = "cuda"
            log(f"CUDA: {torch.cuda.get_device_name(0)}")
        else:
            _device = "cpu"
            log("CUDA not available, using CPU.")

        MODEL_ID = "theusamaaslam/vit-Large-nsfw-detector"
        log(f"Loading {MODEL_ID}...")
        _processor = AutoImageProcessor.from_pretrained(MODEL_ID)
        _model = AutoModelForImageClassification.from_pretrained(
            MODEL_ID, torch_dtype=torch.float16,
            device_map={"": "cuda:0"} if _device == "cuda" else "cpu"
        )

        # Read actual labels from the model config
        global NSFW_LABELS
        if hasattr(_model.config, 'id2label') and _model.config.id2label:
            NSFW_LABELS = [v for k, v in sorted(_model.config.id2label.items())]
        log(f"Labels: {NSFW_LABELS}")

        log("Loaded.")
        vram = get_vram_info()
        send_response({"type": "status", "message": "Model loaded", "vram": vram})

    except Exception as e:
        log(f"Load failed: {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "message": str(e)})


def unload_model():
    global _model, _processor
    if _model is None:
        send_response({"type": "status", "message": "Model not loaded"})
        return
    try:
        import torch, gc
        _model = None
        _processor = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.synchronize()
        log("Unloaded.")
        send_response({"type": "status", "message": "Model unloaded", "vram": get_vram_info()})
    except Exception as e:
        send_response({"type": "error", "message": str(e)})


def classify_image(req_id: int, image_bytes: bytes):
    global _model, _processor

    if _model is None:
        send_response({"type": "error", "id": req_id, "message": "Model not loaded"})
        return

    try:
        import torch
        from PIL import Image

        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        inputs = _processor(images=image, return_tensors="pt").to(_device)

        with torch.no_grad():
            outputs = _model(**inputs)
            probs = torch.softmax(outputs.logits, dim=-1)[0]

        results = []
        for i, label in enumerate(NSFW_LABELS):
            results.append(f"{label}:{probs[i].item():.3f}")

        raw = ", ".join(results)
        log(f"Classified: {raw}")

        # Find which index is the NSFW label
        nsfw_idx = None
        for i, label in enumerate(NSFW_LABELS):
            if 'nsfw' in label.lower() or 'porn' in label.lower() or 'sexy' in label.lower() or 'hentai' in label.lower():
                nsfw_idx = i
                break
        if nsfw_idx is None:
            # Default: treat highest-index label as NSFW
            nsfw_idx = len(NSFW_LABELS) - 1

        is_nsfw = probs[nsfw_idx].item() > 0.5
        answer = "YES" if is_nsfw else "NO"

        send_response({"type": "result", "id": req_id, "answer": answer, "raw": raw})

    except Exception as e:
        log(f"Classify error: {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "id": req_id, "message": str(e)})


def main():
    log("NSFW service started. Waiting for commands...")

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            send_response({"type": "error", "message": "Invalid JSON"})
            continue

        action = req.get("action", "")

        if action == "load":
            load_model()
        elif action == "unload":
            unload_model()
        elif action == "query":
            req_id = req.get("id", 0)
            image_path = req.get("path", "")
            image_b64 = req.get("image_base64", "")

            if image_b64:
                classify_image(req_id, base64.b64decode(image_b64))
            elif image_path:
                try:
                    with open(image_path, "rb") as f:
                        classify_image(req_id, f.read())
                except Exception as e:
                    send_response({"type": "error", "id": req_id, "message": str(e)})
            else:
                send_response({"type": "error", "id": req_id, "message": "No image provided"})
        elif action == "vram":
            send_response({"type": "vram", **get_vram_info()})
        elif action == "shutdown":
            log("Shutting down.")
            unload_model()
            send_response({"type": "status", "message": "Shutting down"})
            sys.exit(0)
        else:
            send_response({"type": "error", "message": f"Unknown action: {action}"})


if __name__ == "__main__":
    main()
