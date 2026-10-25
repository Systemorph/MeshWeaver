"""ocr — a stateless, cluster-internal OCR service: Tesseract 5 with its language packs baked in.

The contract is the one MeshWeaver.Plugins' ``MeshWeaver.Ocr`` HttpService engine speaks
(``Ocr:Engine=HttpService``, ``Ocr:HttpService:Endpoint=http://<release>:8080/ocr``):

    POST /ocr?languages=de,id      the image bytes as the body; Content-Type image/*;
                                   languages = optional ISO 639-1 hints (comma-separated)
                                 → 200 {"text": "…", "languages": ["de", "id"], "engine": "tesseract 5.5.0"}
    GET  /healthz                  process alive
    GET  /readyz                   tesseract runs and every pack the default set needs is installed

Refusals answer JSON ``{"error": "…"}``: 401 (a key is configured and the bearer is not it), 404, 411
(no Content-Length), 413 (body or pixel count over the limit), 415 (not an image), 422 (a hint with no
installed pack), 503 (every admission place taken — refused before the body is read — or every OCR
slot busy for the whole deadline), 504 (the deadline passed mid-read).

Memory is bounded by ADMISSION, not by the OCR slots: a request takes one of ``OCR_MAX_ADMITTED``
places BEFORE its body is read and holds it until it is answered, so at most that many bodies are ever
buffered (``OCR_MAX_ADMITTED`` x ``OCR_MAX_BYTES``). A request that finds no free place is answered 503
and its body is never read. The ``OCR_CONCURRENCY`` slots then bound how many of the admitted run
Tesseract at once.

Nothing is stored and no text is logged — only sizes, timings and the languages found. The service
needs no network beyond its listening socket; the chart's NetworkPolicy denies all egress.
"""
from __future__ import annotations

import hmac
import json
import logging
import os
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

import ocr

log = logging.getLogger("ocr")


def _int_env(name: str, default: int) -> int:
    raw = os.environ.get(name, "").strip()
    return int(raw) if raw else default


class Config:
    def __init__(self, run: ocr.Runner = ocr.run_tesseract):
        self.max_bytes = _int_env("OCR_MAX_BYTES", 20 * 1024 * 1024)
        self.max_pixels = _int_env("OCR_MAX_PIXELS", 40_000_000)
        self.timeout = float(_int_env("OCR_TIMEOUT_SECONDS", 150))
        self.concurrency = _int_env("OCR_CONCURRENCY", 2)
        self.read_timeout = float(_int_env("OCR_READ_TIMEOUT_SECONDS", 30))
        # Bodies held at once (being read, waiting for a slot, or being read by Tesseract).
        self.max_admitted = _int_env("OCR_MAX_ADMITTED", 4 * self.concurrency)
        if self.max_admitted < self.concurrency:
            raise SystemExit(f"OCR_MAX_ADMITTED ({self.max_admitted}) is below OCR_CONCURRENCY ({self.concurrency})"
                             " — slots could never all be used")
        self.api_key = os.environ.get("OCR_API_KEY", "").strip() or None
        packs = ocr.installed_packs(run)
        if "osd" not in packs:
            raise SystemExit("the osd pack is not installed — script and orientation detection cannot run")
        installed = frozenset(ocr.PACK_TO_ISO1[p] for p in packs if p in ocr.PACK_TO_ISO1)
        default_raw = os.environ.get("OCR_DEFAULT_LANGUAGES", "").strip()
        try:
            default = ocr.parse_hints([default_raw], installed) if default_raw else tuple(sorted(installed))
        except ocr.Refused as exc:
            raise SystemExit(f"OCR_DEFAULT_LANGUAGES: {exc.why}") from None
        if not default:
            raise SystemExit("no language pack is installed — the service could read nothing")
        self.settings = ocr.Settings(installed=installed, default=default, max_pixels=self.max_pixels,
                                     engine=ocr.tesseract_version(run))
        self.slots = threading.BoundedSemaphore(self.concurrency)
        self.admission = threading.BoundedSemaphore(self.max_admitted)


def make_handler(cfg: Config, run: ocr.Runner = ocr.run_tesseract):
    class Handler(BaseHTTPRequestHandler):
        server_version = "ocr"
        sys_version = ""
        protocol_version = "HTTP/1.1"
        timeout = cfg.read_timeout  # a client that stops sending is dropped, never waited on

        def log_message(self, fmt, *args):  # the default writes every request line to stderr
            pass

        def _send(self, status: int, payload: dict, close: bool = False) -> None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            if close:
                self.send_header("Connection", "close")
                self.close_connection = True
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = urlsplit(self.path).path
            if path == "/healthz":
                return self._send(200, {"ok": True})
            if path == "/readyz":
                return self._send(200, {"ok": True, "engine": cfg.settings.engine,
                                        "languages": sorted(cfg.settings.installed),
                                        "default": list(cfg.settings.default)})
            return self._send(404, {"error": "not found"})

        def do_POST(self):
            url = urlsplit(self.path)
            if url.path != "/ocr":
                return self._send(404, {"error": "not found"}, close=True)
            if cfg.api_key is not None:
                given = self.headers.get("Authorization", "")
                if not hmac.compare_digest(given.encode(), f"Bearer {cfg.api_key}".encode()):
                    return self._send(401, {"error": "missing or wrong bearer key"}, close=True)
            length = self.headers.get("Content-Length")
            if length is None or not length.isdigit():
                return self._send(411, {"error": "Content-Length is required"}, close=True)
            size = int(length)
            if size > cfg.max_bytes:
                # Never read it: answer and drop the connection.
                return self._send(413, {"error": f"the body is {size} bytes; the limit is {cfg.max_bytes}"}, close=True)
            ctype = (self.headers.get("Content-Type") or "").split(";")[0].strip().lower()
            if ctype and not (ctype.startswith("image/") or ctype == "application/octet-stream"):
                return self._send(415, {"error": f"Content-Type {ctype} is not an image"}, close=True)
            # Take an admission place BEFORE buffering the body; none free ⇒ refuse it unread.
            if not cfg.admission.acquire(blocking=False):
                return self._send(503, {"error": f"every admission place ({cfg.max_admitted}) is taken — retry later"},
                                  close=True)
            try:
                return self._read_admitted(url, size)
            finally:
                cfg.admission.release()

        def _read_admitted(self, url, size: int):
            data = self.rfile.read(size)
            if len(data) != size:
                return self._send(400, {"error": "the body ended before Content-Length"}, close=True)

            started = time.monotonic()
            deadline = started + cfg.timeout
            try:
                hints = ocr.parse_hints(parse_qs(url.query).get("languages", []), cfg.settings.installed)
            except ocr.Refused as exc:
                return self._send(exc.status, {"error": exc.why})
            if not cfg.slots.acquire(timeout=max(0.0, deadline - time.monotonic())):
                return self._send(503, {"error": f"every OCR slot ({cfg.concurrency}) stayed busy for {cfg.timeout:.0f} s"})
            try:
                reading = ocr.read(data, hints, cfg.settings, deadline, run)
            except ocr.Refused as exc:
                log.info("refused %d bytes: %d %s", size, exc.status, exc.why)
                return self._send(exc.status, {"error": exc.why})
            except subprocess.TimeoutExpired:
                log.warning("read of %d bytes passed the %.0f s deadline", size, cfg.timeout)
                return self._send(504, {"error": f"the read did not finish within {cfg.timeout:.0f} s"})
            finally:
                cfg.slots.release()
            log.info("read %d bytes in %d ms: %d chars, languages=%s (hints=%s)", size,
                     int((time.monotonic() - started) * 1000), len(reading.text), reading.languages, list(hints))
            return self._send(200, {"text": reading.text, "languages": reading.languages, "engine": reading.engine})

    return Handler


def serve(host: str = "0.0.0.0", port: int = 8080, cfg: Config | None = None) -> ThreadingHTTPServer:
    cfg = cfg or Config()
    httpd = ThreadingHTTPServer((host, port), make_handler(cfg))
    httpd.daemon_threads = True
    return httpd


if __name__ == "__main__":
    logging.basicConfig(level=os.environ.get("OCR_LOG_LEVEL", "INFO"),
                        format="%(asctime)s %(levelname)s %(name)s %(message)s")
    config = Config()
    server = serve(port=_int_env("OCR_PORT", 8080), cfg=config)
    log.info("ocr serving on :%d — %s, packs %s, default %s, auth %s", server.server_address[1],
             config.settings.engine, sorted(config.settings.installed), list(config.settings.default),
             "bearer key" if config.api_key else "none (NetworkPolicy is the boundary)")
    server.serve_forever()
