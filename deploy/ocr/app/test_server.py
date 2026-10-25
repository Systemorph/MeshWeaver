"""Offline tests of the decisions and the HTTP bounds, with a FAKE Tesseract (no packs needed).

    python test_server.py      # exit code = failures (unittest)

The real engine on the real fixtures is smoke.py; this file pins what must hold whatever the engine
reads: hint parsing, language identification, the size/type/auth/deadline refusals.
"""
from __future__ import annotations

import io
import json
import os
import subprocess
import threading
import time
import unittest
import urllib.error
import urllib.request

from PIL import Image

import ocr
import server

INSTALLED = frozenset({"de", "en", "fr", "it", "id"})


def fake_runner(text: str = "", tsv_words: list[tuple[int, int, str]] | None = None, sleep: float = 0.0):
    """A Tesseract stand-in: answers --version/--list-langs, osd, and writes txt+tsv for a read."""

    def run(args, timeout):
        if args == ["--version"]:
            return subprocess.CompletedProcess(args, 0, b"tesseract 5.5.0\n leptonica-1.84.1\n", b"")
        if args == ["--list-langs"]:
            return subprocess.CompletedProcess(args, 0, b"List of available languages (6):\ndeu\neng\nfra\nind\nita\nosd\n", b"")
        if sleep:
            if sleep > timeout:
                raise subprocess.TimeoutExpired("tesseract", timeout)
            time.sleep(sleep)
        if "osd" in args:
            return subprocess.CompletedProcess(args, 0, b"Rotate: 0\nScript: Latin\n", b"")
        base = args[1]
        with open(base + ".txt", "w", encoding="utf-8") as fh:
            fh.write(text)
        rows = ["level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext"]
        for block, par, word in tsv_words or []:
            rows.append(f"5\t1\t{block}\t{par}\t1\t1\t0\t0\t1\t1\t95\t{word}")
        with open(base + ".tsv", "w", encoding="utf-8") as fh:
            fh.write("\n".join(rows))
        return subprocess.CompletedProcess(args, 0, b"", b"")

    return run


def png(w=200, h=100) -> bytes:
    buf = io.BytesIO()
    Image.new("L", (w, h), 255).save(buf, format="PNG")
    return buf.getvalue()


class Decisions(unittest.TestCase):
    def test_hints_accept_iso1_and_pack_names_dedup_in_order(self):
        self.assertEqual(ocr.parse_hints(["de,id", "deu", " EN "], INSTALLED), ("de", "id", "en"))

    def test_hint_without_pack_is_refused_naming_installed(self):
        with self.assertRaises(ocr.Refused) as ctx:
            ocr.parse_hints(["de,xx"], INSTALLED)
        self.assertEqual(ctx.exception.status, 422)
        self.assertIn("installed: de, en, fr, id, it", ctx.exception.why)

    def test_shared_function_words_decide_nothing(self):
        self.assertNotIn("di", ocr.DISTINCTIVE["it"])
        self.assertNotIn("di", ocr.DISTINCTIVE["id"])
        self.assertNotIn("la", ocr.DISTINCTIVE["fr"])

    def test_identifies_two_languages_by_paragraph(self):
        paras = ["Die Versicherung hat den Schaden geprüft und wird die Rechnung bezahlen",
                 "Pihak asuransi sudah memeriksa kerusakan dan akan membayar tagihan itu"]
        self.assertEqual(set(ocr.identify(paras, ["de", "en", "fr", "it", "id"])), {"de", "id"})

    def test_reports_only_languages_in_the_read_set(self):
        paras = ["Die Versicherung hat den Schaden geprüft und wird die Rechnung bezahlen"]
        self.assertEqual(ocr.identify(paras, ["en", "id"]), [])

    def test_too_little_evidence_assigns_nothing(self):
        self.assertEqual(ocr.identify(["Rechnung 2026"], ["de", "en"]), [])

    def test_single_pack_with_text_reports_that_pack(self):
        self.assertEqual(ocr.reported_languages("Rechnung 2026", ["Rechnung 2026"], ["de"]), ["de"])
        self.assertEqual(ocr.reported_languages("", [], ["de"]), [])

    def test_osd_parse(self):
        self.assertEqual(ocr.parse_osd("Page number: 0\nRotate: 90\nScript: Latin\n"), (90, "Latin"))
        self.assertEqual(ocr.parse_osd(""), (0, None))

    def test_tsv_paragraphs(self):
        tsv = "h\n5\t1\t1\t1\t1\t1\t0\t0\t1\t1\t9\tA\n5\t1\t1\t1\t1\t2\t0\t0\t1\t1\t9\tB\n5\t1\t2\t1\t1\t1\t0\t0\t1\t1\t9\tC\n"
        self.assertEqual(ocr.paragraphs_from_tsv(tsv), ["A B", "C"])

    def test_transparency_is_composited_onto_white(self):
        # Black ink on a transparent background whose hidden colour is ALSO black: only alpha carries the
        # text. Dropping alpha (convert("RGB")) yields one black page and erases the text.
        def encoded(img):
            buf = io.BytesIO()
            img.save(buf, format="PNG")
            return buf.getvalue()

        rgba = Image.new("RGBA", (60, 40), (0, 0, 0, 0))
        rgba.paste((0, 0, 0, 255), (20, 10, 40, 30))
        la = Image.new("LA", (60, 40), (0, 0))
        la.paste((0, 255), (20, 10, 40, 30))
        pal = Image.new("P", (60, 40), 0)
        pal.putpalette([0, 0, 0, 0, 0, 0])   # both indexes are black; index 0 is the transparent one
        pal.paste(1, (20, 10, 40, 30))
        pal.info["transparency"] = 0
        for name, img in (("RGBA", rgba), ("LA", la), ("P+tRNS", pal)):
            with self.subTest(name):
                out = ocr.decode(encoded(img), max_pixels=1_000_000).convert("L")
                self.assertEqual(out.getpixel((2, 2)), 255, "the transparent background must read WHITE")
                self.assertEqual(out.getpixel((30, 20)), 0, "the opaque ink must stay black")

    def test_pixel_bomb_is_refused_before_decoding(self):
        with self.assertRaises(ocr.Refused) as ctx:
            ocr.decode(png(3000, 3000), max_pixels=1_000_000)
        self.assertEqual(ctx.exception.status, 413)


class Http(unittest.TestCase):
    def start(self, runner, **env):
        saved = {k: os.environ.get(k) for k in env}
        os.environ.update({k: str(v) for k, v in env.items()})
        try:
            cfg = server.Config(runner)
        finally:
            for k, v in saved.items():
                if v is None:
                    os.environ.pop(k, None)
                else:
                    os.environ[k] = v
        httpd = server.ThreadingHTTPServer(("127.0.0.1", 0), server.make_handler(cfg, runner))
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        self.addCleanup(httpd.shutdown)
        return f"http://127.0.0.1:{httpd.server_address[1]}"

    def post(self, base, body, query="", headers=None):
        req = urllib.request.Request(f"{base}/ocr{query}", data=body, method="POST",
                                     headers={"Content-Type": "image/png", **(headers or {})})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return resp.status, json.loads(resp.read())
        except urllib.error.HTTPError as err:
            return err.code, json.loads(err.read() or b"{}")

    def test_contract_shape(self):
        base = self.start(fake_runner("Die Rechnung ist nicht bezahlt und wird gemahnt",
                                      [(1, 1, w) for w in "Die Rechnung ist nicht bezahlt und wird gemahnt".split()]))
        status, body = self.post(base, png())
        self.assertEqual(status, 200)
        self.assertEqual(set(body), {"text", "languages", "engine"})
        self.assertEqual(body["languages"], ["de"])
        self.assertEqual(body["engine"], "tesseract 5.5.0")

    def test_oversized_body_is_refused_unread(self):
        base = self.start(fake_runner(), OCR_MAX_BYTES=100)
        status, body = self.post(base, png())
        self.assertEqual(status, 413)
        self.assertIn("limit is 100", body["error"])

    def test_bearer_key_when_configured(self):
        base = self.start(fake_runner(), OCR_API_KEY="k1")
        self.assertEqual(self.post(base, png())[0], 401)
        self.assertEqual(self.post(base, png(), headers={"Authorization": "Bearer k2"})[0], 401)
        self.assertEqual(self.post(base, png(), headers={"Authorization": "Bearer k1"})[0], 200)

    def test_deadline_answers_504(self):
        base = self.start(fake_runner(sleep=3.0), OCR_TIMEOUT_SECONDS=1)
        status, body = self.post(base, png())
        self.assertEqual(status, 504)

    def test_default_set_narrowed_by_config_and_extended_by_hint(self):
        words = "Pihak asuransi sudah memeriksa kerusakan dan akan membayar".split()
        base = self.start(fake_runner(" ".join(words), [(1, 1, w) for w in words]), OCR_DEFAULT_LANGUAGES="de,en")
        self.assertEqual(self.post(base, png())[1]["languages"], [])          # id is not in the default set
        self.assertEqual(self.post(base, png(), "?languages=de,id")[1]["languages"], ["id"])  # the hint extends it

    def test_admission_refuses_beyond_the_bound_without_reading_the_body(self):
        # One slot, one admission place. While request A holds both, request B must be refused at once
        # with 503 and its body never buffered — not queued behind the slot with its upload in memory.
        started = threading.Event()
        inner = fake_runner(sleep=3.0)

        def runner(args, timeout):
            if args not in (["--version"], ["--list-langs"]) and "osd" in args:
                started.set()
            return inner(args, timeout)

        base = self.start(runner, OCR_CONCURRENCY=1, OCR_MAX_ADMITTED=1)
        first = {}
        worker = threading.Thread(target=lambda: first.update(answer=self.post(base, png())))
        worker.start()
        self.addCleanup(worker.join)
        self.assertTrue(started.wait(10), "request A never reached the engine")
        t0 = time.monotonic()
        status, body = self.post(base, png())
        self.assertEqual(status, 503, body)
        self.assertIn("admission", body["error"])
        self.assertLess(time.monotonic() - t0, 2.0, "B was refused only after waiting — it was queued, not refused")
        worker.join(30)
        self.assertEqual(first["answer"][0], 200)

    def test_admission_below_concurrency_refuses_to_start(self):
        with self.assertRaises(SystemExit):
            self.start(fake_runner(), OCR_CONCURRENCY=2, OCR_MAX_ADMITTED=1)

    def test_unknown_default_language_refuses_to_start(self):
        with self.assertRaises(SystemExit):
            self.start(fake_runner(), OCR_DEFAULT_LANGUAGES="de,xx")

    def test_health(self):
        base = self.start(fake_runner())
        with urllib.request.urlopen(f"{base}/healthz", timeout=5) as resp:
            self.assertEqual(json.loads(resp.read()), {"ok": True})


if __name__ == "__main__":
    unittest.main()
