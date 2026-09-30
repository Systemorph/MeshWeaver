"""Build-time proof that the image that SHIPS can load its model and extract.

Runs in the final stage, offline, through the server's OWN load path (Extractor._load_gliner),
so a runtime dependency missing from that stage fails the BUILD instead of leaving every pod
serving 503 "model loading" forever. The export stage's smoke test cannot catch that: it runs in
a different stage. The service tests (test_server.py) cannot either: they use a fake model.

Measured 2026-09-30: the image built from 55d99d2d12 uninstalled httpx after its tests, and
huggingface_hub 1.x needs it — /healthz answered in 0.7 s, /readyz never did.
"""
import os
import sys
import time

os.environ.setdefault("S1_NO_AUTOSTART", "1")

import server  # noqa: E402  (after the autostart switch)

started = time.time()
model = server.Extractor._load_gliner()
loaded = time.time() - started
spans = model.inference(["Contoso Ltd hired Jane Roe."], ["organization", "person"], threshold=0.3)[0]
labels = sorted({str(s["label"]) for s in spans})
print(f"runtime load {loaded:.1f}s from {server.MODEL_DIR}; smoke labels: {labels}")
if "organization" not in labels:
    sys.exit("FATAL: the shipped model found no organisation in the smoke sentence")
