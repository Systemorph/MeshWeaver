"""s1-extract — a stateless System-1 extractor: GLiNER (urchade/gliner_multi-v2.1, Apache-2.0) on ONNX.

It finds ORGANISATION / PERSON spans in the text it is sent and returns them. It holds NO names and
decides nothing: whether a span is a client is the CRM's call, made by the caller (the instance that
owns the CRM). Texts are never logged or stored.

    POST /v1/extract   {"texts": [...], "labels": ["organization", "person"], "threshold": 0.5}
                     → {"model": "...", "results": [[{"start", "end", "label", "score", "text"}], ...]}
    GET  /healthz      process alive
    GET  /readyz       model loaded

Auth: ``Authorization: Bearer <projected ServiceAccount token, audience s1-extract>`` checked by
TokenReview (app/auth.py). ``S1_AUTH=none`` turns it off for a laptop run and is refused unless
``S1_ALLOW_NO_AUTH=1`` is also set, so a values typo cannot open it.
"""
from __future__ import annotations

import logging
import os
import threading
from typing import Any

from fastapi import FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

from auth import Authenticator, Verdict

MODEL_DIR = os.environ.get("S1_MODEL_DIR", "/models/gliner")
MODEL_NAME = os.environ.get("S1_MODEL_NAME", "urchade/gliner_multi-v2.1")
AUDIENCE = os.environ.get("S1_AUDIENCE", "s1-extract")
ALLOWED = frozenset(s.strip() for s in os.environ.get("S1_ALLOWED_SERVICEACCOUNTS", "").split(",") if s.strip())
AUTH_MODE = os.environ.get("S1_AUTH", "tokenreview")
MAX_TEXTS = int(os.environ.get("S1_MAX_TEXTS", "256"))
MAX_CHARS = int(os.environ.get("S1_MAX_CHARS", "2000"))
DEFAULT_LABELS = ["organization", "person"]

log = logging.getLogger("s1-extract")


class ExtractRequest(BaseModel):
    texts: list[str] = Field(default_factory=list)
    labels: list[str] = Field(default_factory=lambda: list(DEFAULT_LABELS))
    threshold: float = 0.5


def validate(req: ExtractRequest) -> str | None:
    """Why a request is refused, or None. Pure."""
    if not req.texts:
        return "texts is empty"
    if len(req.texts) > MAX_TEXTS:
        return f"{len(req.texts)} texts; the limit is {MAX_TEXTS} per call"
    if any(len(t) > MAX_CHARS for t in req.texts):
        return f"a text is longer than {MAX_CHARS} characters; split it"
    if not req.labels or len(req.labels) > 16:
        return "labels must name 1 to 16 entity types"
    if not 0.0 < req.threshold < 1.0:
        return "threshold must be in (0, 1)"
    return None


class Extractor:
    """Loads the model once, in the background, so /healthz answers while it loads."""

    def __init__(self, load=None):
        self.model: Any = None
        self.error: str | None = None
        self._load = load or self._load_gliner
        self._lock = threading.Lock()

    @staticmethod
    def _load_gliner():
        from gliner import GLiNER
        return GLiNER.from_pretrained(MODEL_DIR, runtime="onnxruntime", onnx_model_file="model.onnx",
                                      local_files_only=True)

    def start(self) -> None:
        def run():
            try:
                self.model = self._load()
                log.info("model loaded from %s", MODEL_DIR)
            except Exception as exc:  # readiness stays false and says why
                self.error = f"{type(exc).__name__}: {exc}"
                log.error("model load failed: %s", self.error)
        threading.Thread(target=run, daemon=True).start()

    def extract(self, req: ExtractRequest) -> list[list[dict]]:
        with self._lock:  # onnxruntime sessions are thread-safe, the gliner wrapper's batching is not
            raw = self.model.inference(req.texts, req.labels, threshold=req.threshold, batch_size=8)
        return [[{"start": int(e["start"]), "end": int(e["end"]), "label": str(e["label"]),
                  "score": round(float(e["score"]), 4), "text": str(e["text"])} for e in spans] for spans in raw]


def build_app(extractor: Extractor, authenticator: Authenticator | None) -> FastAPI:
    app = FastAPI(title="s1-extract", docs_url=None, redoc_url=None, openapi_url=None)

    def authorize(authorization: str | None) -> None:
        if authenticator is None:
            return
        verdict: Verdict = authenticator.check(authorization)
        if not verdict.allowed:
            log.warning("refused caller %s: %s", verdict.who or "(unknown)", verdict.why)
            raise HTTPException(status_code=401 if not verdict.who else 403, detail=verdict.why)

    @app.get("/healthz")
    def healthz():
        return {"ok": True}

    @app.get("/readyz")
    def readyz():
        if extractor.model is None:
            raise HTTPException(status_code=503, detail=extractor.error or "model loading")
        return {"ok": True, "model": MODEL_NAME}

    @app.post("/v1/extract")
    def extract(req: ExtractRequest, authorization: str | None = Header(default=None)):
        authorize(authorization)
        if extractor.model is None:
            raise HTTPException(status_code=503, detail=extractor.error or "model loading")
        problem = validate(req)
        if problem:
            raise HTTPException(status_code=422, detail=problem)
        return {"model": MODEL_NAME, "results": extractor.extract(req)}

    return app


def authenticator_from_env() -> Authenticator | None:
    if AUTH_MODE == "none":
        if os.environ.get("S1_ALLOW_NO_AUTH") != "1":
            raise SystemExit("S1_AUTH=none requires S1_ALLOW_NO_AUTH=1 — refusing to serve without caller auth")
        log.warning("caller authentication is OFF (S1_AUTH=none) — laptop use only")
        return None
    if AUTH_MODE != "tokenreview":
        raise SystemExit(f"unknown S1_AUTH {AUTH_MODE!r}")
    if not ALLOWED:
        raise SystemExit("S1_ALLOWED_SERVICEACCOUNTS is empty — no caller could ever be admitted")
    return Authenticator(AUDIENCE, ALLOWED)


logging.basicConfig(level=os.environ.get("S1_LOG_LEVEL", "INFO"))
if os.environ.get("S1_NO_AUTOSTART") != "1":
    _extractor = Extractor()
    app = build_app(_extractor, authenticator_from_env())
    _extractor.start()
