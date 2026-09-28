"""Build-time only: fetch urchade/gliner_multi-v2.1 (Apache-2.0) and export it to ONNX in /models/gliner.

Runs in the Dockerfile's export stage, so the runtime image carries the exported model and never
downloads anything. A failure here fails the BUILD, naming what is missing.
"""
import os
import sys
from pathlib import Path

from gliner import GLiNER

name = os.environ.get("S1_MODEL_NAME", "urchade/gliner_multi-v2.1")
out = Path(sys.argv[1] if len(sys.argv) > 1 else "/models/gliner")
model = GLiNER.from_pretrained(name)
model.save_pretrained(str(out))
paths = model.export_to_onnx(str(out), onnx_filename="model.onnx", quantize=False)
onnx = Path(paths["onnx_path"])
if not onnx.is_file() or onnx.stat().st_size < 10_000_000:
    sys.exit(f"FATAL: the ONNX export at {onnx} is missing or too small to be the model")
# Smoke: the exported model loads on onnxruntime and finds an organisation.
check = GLiNER.from_pretrained(str(out), runtime="onnxruntime", onnx_model_file="model.onnx", local_files_only=True)
spans = check.inference(["Contoso Ltd hired Jane Roe."], ["organization", "person"], threshold=0.3)[0]
labels = sorted({s["label"] for s in spans})
print(f"exported {name} → {onnx} ({onnx.stat().st_size:,} bytes); smoke labels: {labels}")
if "organization" not in labels:
    sys.exit("FATAL: the exported model found no organisation in the smoke sentence")
