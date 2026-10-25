"""Smoke test: the REAL Tesseract and packs, through the REAL HTTP contract, on the committed fixtures.

    python smoke.py                       # starts the server in-process on 127.0.0.1 (the image build)
    python smoke.py http://host:8080      # an already-running service (CI's `docker run`)

Every check prints PASS/FAIL; the exit code is the number of failures. It carries its own NEGATIVE
CONTROLS, because a check that cannot fail proves nothing:

- the German+Indonesian assertion is run against the English fixture and must FAIL there;
- a blank page must come back with no text and no languages;
- a hint must RESTRICT what is reported (``languages=en`` on the German+Indonesian page names neither).
"""
from __future__ import annotations

import io
import json
import os
import sys
import threading
import urllib.error
import urllib.request

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.environ.get("OCR_FIXTURES", os.path.join(HERE, "..", "fixtures"))

# What the German+Indonesian page must yield. Words chosen so a wrong pack or a garbled read misses them;
# "Gebäude" checks the umlaut survives (eng alone reads it as "Gebaude").
DE_ID_WORDS = ["Versicherung", "Gebäude", "Rechnung", "asuransi", "kerusakan", "tagihan"]
DE_ID_LANGUAGES = {"de", "id"}

failures = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global failures
    print(("PASS " if ok else "FAIL ") + name + (f" — {detail}" if detail else ""), flush=True)
    if not ok:
        failures += 1


def post(base: str, body: bytes, query: str = "", ctype: str = "image/png") -> tuple[int, dict]:
    req = urllib.request.Request(f"{base}/ocr{query}", data=body, method="POST",
                                 headers={"Content-Type": ctype, "X-File-Name": "page.png"})
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as err:
        return err.code, json.loads(err.read() or b"{}")


def de_id_problems(answer: dict) -> list[str]:
    """Why ``answer`` is NOT the German+Indonesian reading; empty when it is. The one assertion the
    positive case and its negative control share."""
    problems = [f"missing '{w}'" for w in DE_ID_WORDS if w not in answer.get("text", "")]
    if set(answer.get("languages", [])) != DE_ID_LANGUAGES:
        problems.append(f"languages {answer.get('languages')} != {sorted(DE_ID_LANGUAGES)}")
    return problems


def png(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def main(base: str) -> int:
    de_id = open(os.path.join(FIXTURES, "de-id.png"), "rb").read()
    en = open(os.path.join(FIXTURES, "en.png"), "rb").read()

    with urllib.request.urlopen(f"{base}/readyz", timeout=10) as resp:
        ready = json.loads(resp.read())
    check("readyz names tesseract 5 and the five packs",
          ready.get("engine", "").startswith("tesseract 5") and set(ready.get("languages", [])) == {"de", "en", "fr", "id", "it"},
          json.dumps(ready))

    # Positive: no hints → the languages are DETECTED.
    status, answer = post(base, de_id)
    problems = de_id_problems(answer) if status == 200 else [f"status {status}: {answer}"]
    check("German+Indonesian page: text and both languages detected (no hints)", not problems, "; ".join(problems))
    check("engine names tesseract 5", answer.get("engine", "").startswith("tesseract 5"), answer.get("engine", ""))

    # NEGATIVE CONTROL: the same assertion on the English page must fail.
    status, answer_en = post(base, en)
    control = de_id_problems(answer_en) if status == 200 else ["(non-200)"]
    check("NEGATIVE CONTROL: the German+Indonesian assertion FAILS on the English page", bool(control),
          "it failed with: " + "; ".join(control) if control else "it PASSED — the assertion cannot fail")
    check("English page: detected as English only", status == 200 and answer_en.get("languages") == ["en"],
          str(answer_en.get("languages")))

    # NEGATIVE CONTROL: a blank page has no text and no language.
    status, blank = post(base, png(Image.new("L", (800, 400), 255)))
    check("NEGATIVE CONTROL: blank page → no text, no languages",
          status == 200 and blank.get("text") == "" and blank.get("languages") == [], json.dumps(blank))

    # Hints restrict: read with English only, nothing German or Indonesian is reported.
    status, restricted = post(base, de_id, "?languages=en")
    check("hint languages=en restricts what is reported", status == 200 and not set(restricted.get("languages", [])) & DE_ID_LANGUAGES,
          str(restricted.get("languages")))
    # Hints name the set: de,id reads both.
    status, hinted = post(base, de_id, "?languages=de,id")
    problems = de_id_problems(hinted) if status == 200 else [f"status {status}"]
    check("hint languages=de,id reads both", not problems, "; ".join(problems))

    # Orientation: the page turned 90° is detected by osd and read upright.
    turned = png(Image.open(io.BytesIO(de_id)).rotate(90, expand=True))
    status, upright = post(base, turned)
    problems = de_id_problems(upright) if status == 200 else [f"status {status}"]
    check("page rotated 90°: osd turns it upright and it reads the same", not problems, "; ".join(problems))

    # Refusals.
    status, _ = post(base, de_id, "?languages=xx")
    check("hint with no installed pack → 422", status == 422, str(status))
    status, _ = post(base, b"not an image")
    check("a body that is not an image → 415", status == 415, str(status))
    status, _ = post(base, de_id, ctype="text/plain")
    check("Content-Type text/plain → 415", status == 415, str(status))

    print(f"{failures} failure(s)", flush=True)
    return failures


if __name__ == "__main__":
    if len(sys.argv) > 1:
        target = sys.argv[1].rstrip("/")
        # A just-started container: wait for /readyz, bounded, and say so when it never comes.
        import time
        until = time.monotonic() + 60
        while True:
            try:
                urllib.request.urlopen(f"{target}/readyz", timeout=2).close()
                break
            except Exception as exc:  # not up yet
                if time.monotonic() > until:
                    print(f"FAIL {target}/readyz never answered within 60 s ({exc})", flush=True)
                    sys.exit(1)
                time.sleep(1)
        sys.exit(main(target))
    import server
    httpd = server.serve(host="127.0.0.1", port=0)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    try:
        sys.exit(main(f"http://127.0.0.1:{httpd.server_address[1]}"))
    finally:
        httpd.shutdown()
