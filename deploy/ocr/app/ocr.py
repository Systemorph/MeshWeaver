"""The OCR core: Tesseract 5 with baked-in language packs, plus script/orientation and language detection.

Pure except for :func:`run_tesseract` (a subprocess), which every caller can replace, so the decisions
(which packs to run, which languages a text is in) are testable on their own.

The pipeline for ONE image:

1. **Bound it.** The pixel count is read from the header before anything is decoded (Pillow opens
   lazily); an image over ``max_pixels`` is refused, so a decompression bomb never reaches Tesseract.
2. **Orientation and script** — ``tesseract --psm 0 -l osd``. A page Tesseract reads as rotated is
   turned upright before recognition. OSD needs a page of text; on a small image it answers
   "too few characters" and the image is read as it is.
3. **Recognition** with the language set: the caller's hints when there are any (they may RESTRICT the
   default set or EXTEND it to any installed pack), otherwise the deployment's default set — every
   installed pack unless ``OCR_DEFAULT_LANGUAGES`` narrows it.
4. **Language identification** of what was read, per paragraph, by distinctive function words of each
   language in the set (a word two languages share counts for neither). A paragraph with too little
   evidence is not assigned; a language is reported when its paragraphs carry a real share of the
   assigned words. Only languages in the recognition set are ever reported.
"""
from __future__ import annotations

import io
import os
import re
import subprocess
import tempfile
import time
from dataclasses import dataclass
from typing import Callable, Iterable, Sequence

from PIL import Image, ImageOps

# ISO 639-1 (what the client sends and gets back) ↔ the Tesseract pack (ISO 639-2/T).
ISO1_TO_PACK = {"en": "eng", "de": "deu", "fr": "fra", "it": "ita", "id": "ind"}
PACK_TO_ISO1 = {v: k for k, v in ISO1_TO_PACK.items()}

# Function words per language. Only the words NO other language in this table also lists are used
# (DISTINCTIVE below), so "di" (Italian AND Indonesian) or "in" (German, English, Italian) never decide.
FUNCTION_WORDS = {
    "de": "der die das und ist nicht mit den dem des ein eine einen einem einer auf für von zu im bis "
          "wird werden wurde sich auch auf aus bei nach über unter oder aber wenn dass haben hat sind "
          "war wir sie ihr ihre sein seine noch nur schon sehr diese dieser dieses kein keine am zum zur",
    "en": "the and is are was were of to with for that this these those from by not have has had "
          "will would be been which their there they you your our it its at as an or but if than "
          "then also only into about after before over under",
    "fr": "le la les et est des une un du de au aux pour pas que qui dans sur avec par ce cette ces "
          "sont été être avoir nous vous ils elles leur leurs mais ou donc ni car sera fait plus",
    "it": "il lo gli la le e è di da del della dei delle un una uno per che non con sono stato essere "
          "nel nella questo questa questi anche ma o se come più dal dalla alla allo agli ha hanno",
    "id": "yang dan di ini itu dengan untuk tidak dari ke akan ada pada adalah juga sudah dalam "
          "kami kita mereka saya anda atau bisa oleh karena sebagai tersebut telah belum harus "
          "sebelum sesudah setelah bagi para hanya lebih banyak",
}


def _distinctive(words: dict[str, str]) -> dict[str, frozenset[str]]:
    sets = {lang: frozenset(text.split()) for lang, text in words.items()}
    counts: dict[str, int] = {}
    for s in sets.values():
        for w in s:
            counts[w] = counts.get(w, 0) + 1
    return {lang: frozenset(w for w in s if counts[w] == 1) for lang, s in sets.items()}


DISTINCTIVE = _distinctive(FUNCTION_WORDS)

# A paragraph is assigned only with at least this many function-word hits AND a clear lead.
MIN_HITS = 2
# A language is reported when its paragraphs carry at least this share of the assigned words.
MIN_SHARE = 0.15

WORD = re.compile(r"[^\W\d_]+", re.UNICODE)


class Refused(Exception):
    """A request this service will not serve; ``status`` is the HTTP answer, the message says why."""

    def __init__(self, status: int, why: str):
        super().__init__(why)
        self.status = status
        self.why = why


@dataclass(frozen=True)
class Settings:
    installed: frozenset[str]          # ISO 639-1 codes of the installed packs (osd excluded)
    default: tuple[str, ...]           # ISO 639-1 codes read when the caller gives no hints
    max_pixels: int
    engine: str                        # e.g. "tesseract 5.5.0"


def parse_hints(raw: Iterable[str], installed: frozenset[str]) -> tuple[str, ...]:
    """The caller's ``languages=`` hints as ISO 639-1 codes, in the order given, deduplicated.

    Accepts ISO 639-1 (``de``) or the pack name (``deu``), comma-separated or repeated. A code with no
    installed pack is REFUSED (422) naming what is installed — reading the page with a pack the caller
    did not ask for would answer a different question. Pure.
    """
    out: list[str] = []
    for item in raw:
        for code in item.split(","):
            code = code.strip().lower()
            if not code:
                continue
            iso = PACK_TO_ISO1.get(code, code)
            if iso not in installed:
                raise Refused(422, f"language '{code}' has no installed pack; installed: "
                                   f"{', '.join(sorted(installed))}")
            if iso not in out:
                out.append(iso)
    return tuple(out)


def identify(paragraphs: Sequence[str], candidates: Sequence[str]) -> list[str]:
    """The languages of ``paragraphs`` among ``candidates`` (ISO 639-1), most words first. Pure."""
    weight: dict[str, int] = {}
    total = 0
    for para in paragraphs:
        words = [w.lower() for w in WORD.findall(para)]
        if not words:
            continue
        hits = {lang: sum(1 for w in words if w in DISTINCTIVE.get(lang, frozenset())) for lang in candidates}
        ranked = sorted(hits.items(), key=lambda kv: kv[1], reverse=True)
        best, best_hits = ranked[0]
        runner_up = ranked[1][1] if len(ranked) > 1 else 0
        if best_hits < MIN_HITS or best_hits < 2 * runner_up:
            continue
        weight[best] = weight.get(best, 0) + len(words)
        total += len(words)
    if total == 0:
        return []
    found = [lang for lang, n in weight.items() if n / total >= MIN_SHARE]
    return sorted(found, key=lambda lang: weight[lang], reverse=True)


def reported_languages(text: str, paragraphs: Sequence[str], read_with: Sequence[str]) -> list[str]:
    """What the answer names: the identified languages; or, when the page was read with ONE pack and
    carries text the identifier could not assign, that pack's language; or nothing. Pure."""
    found = identify(paragraphs, read_with)
    if found:
        return found
    if text.strip() and len(read_with) == 1:
        return [read_with[0]]
    return []


# ── Tesseract ──────────────────────────────────────────────────────────────────────────────────

Runner = Callable[[list[str], float], subprocess.CompletedProcess]


def run_tesseract(args: list[str], timeout: float) -> subprocess.CompletedProcess:
    """One Tesseract process, killed at ``timeout`` seconds (TimeoutExpired propagates)."""
    return subprocess.run(["tesseract", *args], capture_output=True, timeout=timeout, check=False,
                          env={**os.environ, "OMP_THREAD_LIMIT": os.environ.get("OMP_THREAD_LIMIT", "1")})


def tesseract_version(run: Runner = run_tesseract) -> str:
    out = run(["--version"], 10.0)
    first = (out.stdout or out.stderr).decode("utf-8", "replace").splitlines()[0].strip()
    return first  # "tesseract 5.5.0"


def installed_packs(run: Runner = run_tesseract) -> frozenset[str]:
    out = run(["--list-langs"], 10.0).stdout.decode("utf-8", "replace").splitlines()
    return frozenset(line.strip() for line in out[1:] if line.strip())


def parse_osd(stdout: str) -> tuple[int, str | None]:
    """``Rotate:`` and ``Script:`` out of ``--psm 0`` output; (0, None) when absent. Pure."""
    rotate = re.search(r"^Rotate:\s*(\d+)", stdout, re.MULTILINE)
    script = re.search(r"^Script:\s*(\S+)", stdout, re.MULTILINE)
    return (int(rotate.group(1)) % 360 if rotate else 0, script.group(1) if script else None)


def paragraphs_from_tsv(tsv: str) -> list[str]:
    """Words grouped into paragraphs (block, paragraph) from Tesseract's TSV output. Pure."""
    groups: dict[tuple[int, int], list[str]] = {}
    for line in tsv.splitlines()[1:]:
        cols = line.split("\t")
        if len(cols) < 12 or cols[0] != "5":
            continue
        word = cols[11].strip()
        if word:
            groups.setdefault((int(cols[2]), int(cols[3])), []).append(word)
    return [" ".join(words) for _, words in sorted(groups.items())]


@dataclass(frozen=True)
class Reading:
    text: str
    languages: list[str]
    engine: str


def decode(data: bytes, max_pixels: int) -> Image.Image:
    """The upright image, refused before decoding when it is not an image or too large."""
    try:
        img = Image.open(io.BytesIO(data))
        width, height = img.size
    except Exception as exc:
        raise Refused(415, f"the body is not an image this service reads ({type(exc).__name__})") from None
    if width * height > max_pixels:
        raise Refused(413, f"the image is {width}x{height} = {width * height} pixels; the limit is {max_pixels}")
    try:
        img = ImageOps.exif_transpose(img)  # a phone photo's EXIF orientation, before OSD looks at it
        img.load()
    except Exception as exc:
        raise Refused(415, f"the image could not be decoded ({type(exc).__name__})") from None
    if img.mode in ("RGBA", "LA", "PA", "La", "RGBa") or "transparency" in img.info:
        # Composite onto WHITE: a plain convert("RGB") drops alpha and keeps the hidden colour, so black
        # text on a transparent background whose hidden pixels are also black becomes one black page.
        rgba = img.convert("RGBA")
        img = Image.alpha_composite(Image.new("RGBA", rgba.size, (255, 255, 255, 255)), rgba).convert("RGB")
    elif img.mode not in ("L", "RGB"):
        img = img.convert("RGB")
    return img


def read(data: bytes, hints: tuple[str, ...], settings: Settings, deadline: float,
         run: Runner = run_tesseract) -> Reading:
    """OCR one image. ``deadline`` is a ``time.monotonic()`` instant; past it the read is abandoned
    (504), and the Tesseract process it was waiting on is killed."""
    img = decode(data, settings.max_pixels)
    read_with = hints or settings.default
    with tempfile.TemporaryDirectory(prefix="ocr-") as tmp:
        src = os.path.join(tmp, "in.png")
        img.save(src, format="PNG")

        osd = run(["--psm", "0", "-l", "osd", src, "stdout"], _remaining(deadline))
        rotate, _script = parse_osd(osd.stdout.decode("utf-8", "replace")) if osd.returncode == 0 else (0, None)
        if rotate:
            # Tesseract's "Rotate: N" is the CLOCKWISE turn that makes the page upright; PIL turns counter-clockwise.
            img.rotate(-rotate, expand=True).save(src, format="PNG")

        base = os.path.join(tmp, "out")
        packs = "+".join(ISO1_TO_PACK[c] for c in read_with)
        done = run([src, base, "-l", packs, "--psm", "3", "txt", "tsv"], _remaining(deadline))
        if done.returncode != 0:
            raise Refused(500, "tesseract failed: " + done.stderr.decode("utf-8", "replace").strip()[-300:])
        with open(base + ".txt", encoding="utf-8") as fh:
            text = fh.read().strip()
        with open(base + ".tsv", encoding="utf-8") as fh:
            paragraphs = paragraphs_from_tsv(fh.read())
    return Reading(text=text, languages=reported_languages(text, paragraphs, read_with), engine=settings.engine)


def _remaining(deadline: float) -> float:
    left = deadline - time.monotonic()
    if left <= 0:
        raise subprocess.TimeoutExpired("tesseract", 0)
    return left
