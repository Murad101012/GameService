"""
SmolVLM-500M AI Service — stdin/stdout JSON-line protocol
Same protocol as moondream_service.py for drop-in compatibility.
Uses SmolVLM-500M-Instruct with strict YES/NO output.

Protocol (JSON lines, one per message):
  REQUEST:  {"action":"load"}                          -> loads model into VRAM
  REQUEST:  {"action":"unload"}                        -> unloads model from VRAM
  REQUEST:  {"action":"query","id":0,"path":"...","prompt":"..."}  -> runs query
  REQUEST:  {"action":"query","id":0,"image_base64":"...","prompt":"..."}  -> base64 query
  REQUEST:  {"action":"vram"}                          -> returns VRAM info
  REQUEST:  {"action":"shutdown"}                      -> exits process

  RESPONSE: {"type":"status","message":"..."}
  RESPONSE: {"type":"result","id":0,"answer":"YES","raw":"..."}
  RESPONSE: {"type":"vram","used_mb":1234,"total_mb":4096,"percent":30.1}
  RESPONSE: {"type":"error","message":"..."}
"""
import sys
import json
import os
import traceback
import io
import base64

# Model globals
_model = None
_processor = None
_device = "cuda"
_eos_token_id = None  # cached from tokenizer

def log(msg: str):
    print(f"[smolvlm] {msg}", file=sys.stderr, flush=True)


def send_response(obj: dict):
    line = json.dumps(obj, ensure_ascii=False)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def get_vram_info() -> dict:
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


class StrictYesNoLogitsProcessor:
    """Forces the model to output only YES or NO token-by-token."""
    def __init__(self, prompt_len, pad_token_id):
        self.prompt_len = prompt_len
        self.pad_token_id = pad_token_id
        # Pre-encode YES/NO tokens
        self.yes_seq = self._encode_letters("YES")
        self.no_seq = self._encode_letters("NO")
        self._path = None

    def _encode_letters(self, word):
        return [self._encode_single(c) for c in word]

    def _encode_single(self, char):
        global _processor
        ids = _processor.tokenizer.encode(char, add_special_tokens=False)
        return ids[0] if ids else 0

    def __call__(self, input_ids, scores):
        import torch
        step = input_ids.shape[1] - self.prompt_len

        # Step 0: force first token to be Y or N
        if step == 0:
            mask = torch.full_like(scores, -float("inf"))
            mask[0, self.yes_seq[0]] = 0
            mask[0, self.no_seq[0]] = 0
            return scores + mask

        # Determine path from first generated token
        first_token = input_ids[0, self.prompt_len].item()
        self._path = "yes" if first_token == self.yes_seq[0] else "no"

        seq = self.yes_seq if self._path == "yes" else self.no_seq

        # Force next letter, or EOS if sequence complete
        mask = torch.full_like(scores, -float("inf"))
        if step < len(seq):
            mask[0, seq[step]] = 0
        else:
            mask[0, self.pad_token_id] = 0
        return scores + mask


def load_model():
    global _model, _processor, _device, _eos_token_id

    if _model is not None:
        log("Model already loaded.")
        send_response({"type": "status", "message": "Model already loaded"})
        return

    try:
        import torch
        from transformers import AutoProcessor, AutoModelForImageTextToText

        log("Importing transformers + torch...")

        if torch.cuda.is_available():
            _device = "cuda"
            gpu_name = torch.cuda.get_device_name(0)
            vram_total = torch.cuda.get_device_properties(0).total_memory / (1024**3)
            log(f"CUDA: {gpu_name} ({vram_total:.1f} GB VRAM)")
        else:
            _device = "cpu"
            log("CUDA NOT available, falling back to CPU.")

        MODEL_ID = "HuggingFaceTB/SmolVLM-500M-Instruct"

        log(f"Loading model from {MODEL_ID}...")
        _processor = AutoProcessor.from_pretrained(MODEL_ID)
        _model = AutoModelForImageTextToText.from_pretrained(
            MODEL_ID,
            torch_dtype=torch.float16,
            device_map={"": "cuda:0"} if _device == "cuda" else "cpu"
        )
        _eos_token_id = _processor.tokenizer.eos_token_id

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
    global _model, _processor

    if _model is None:
        send_response({"type": "status", "message": "Model not loaded"})
        return

    try:
        import torch
        import gc
        _model = None
        _processor = None
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.synchronize()
        log("Model unloaded.")
        vram = get_vram_info()
        send_response({"type": "status", "message": "Model unloaded", "vram": vram})
    except Exception as e:
        log(f"Error unloading: {e}")
        send_response({"type": "error", "message": str(e)})


def _do_query(req_id: int, image, prompt: str, mode: str = "yesno"):
    global _model, _processor, _eos_token_id

    import torch

    messages = [{
        "role": "user",
        "content": [
            {"type": "image"},
            {"type": "text", "text": prompt}
        ]
    }]

    prompt_text = _processor.apply_chat_template(messages, add_generation_prompt=True)
    inputs = _processor(text=prompt_text, images=image, return_tensors="pt").to(_device)

    prompt_len = inputs.input_ids.shape[1]

    # Configure generation based on mode
    if mode == "yesno":
        logits_proc = StrictYesNoLogitsProcessor(
            prompt_len,
            _processor.tokenizer.pad_token_id or _eos_token_id
        )
        logits_processors = [logits_proc]
        max_tokens = 5
    elif mode == "describe":
        logits_processors = None
        max_tokens = 256
    else:  # "ask" mode
        logits_processors = None
        max_tokens = 200

    with torch.no_grad():
        generated = _model.generate(
            **inputs,
            max_new_tokens=max_tokens,
            do_sample=False,
            logits_processor=logits_processors
        )

    new_tokens = generated[0, prompt_len:]
    raw_answer = _processor.decode(new_tokens, skip_special_tokens=True).strip()
    answer = _processor.batch_decode(new_tokens.unsqueeze(0), skip_special_tokens=True)[0].strip()

    if mode == "yesno":
        ans_upper = answer.upper().replace(".", "").strip()
        if ans_upper.startswith("YES"):
            final = "YES"
        elif ans_upper.startswith("NO"):
            final = "NO"
        else:
            final = ans_upper
    else:
        # Limit to 4 sentences for ask mode
        if mode == "ask":
            sentences = answer.replace('\n', '. ').split('. ')
            sentences = [s.strip() for s in sentences if s.strip()]
            if len(sentences) > 4:
                answer = '. '.join(sentences[:4]) + '.'
        final = answer

    send_response({
        "type": "result",
        "id": req_id,
        "answer": final,
        "raw": raw_answer
    })


def query_image(req_id: int, image_path: str, prompt: str, mode: str = "yesno"):
    global _model

    if _model is None:
        send_response({"type": "error", "id": req_id, "message": "Model not loaded"})
        return

    try:
        from PIL import Image

        if not os.path.exists(image_path):
            send_response({"type": "error", "id": req_id, "message": f"File not found: {image_path}"})
            return

        image = Image.open(image_path).convert("RGB")
        _do_query(req_id, image, prompt, mode)

    except Exception as e:
        log(f"Query error (id={req_id}): {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "id": req_id, "message": str(e)})


def query_image_base64(req_id: int, image_b64: str, prompt: str, mode: str = "yesno"):
    global _model

    if _model is None:
        send_response({"type": "error", "id": req_id, "message": "Model not loaded"})
        return

    try:
        from PIL import Image

        image_bytes = base64.b64decode(image_b64)
        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        _do_query(req_id, image, prompt, mode)

    except Exception as e:
        log(f"Query error (id={req_id}): {e}")
        traceback.print_exc(file=sys.stderr)
        send_response({"type": "error", "id": req_id, "message": str(e)})


def main():
    log("SmolVLM service started. Waiting for commands on stdin...")

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
            mode = req.get("mode", "yesno")

            if image_b64:
                query_image_base64(req_id, image_b64, prompt, mode)
            elif image_path:
                query_image(req_id, image_path, prompt, mode)
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
