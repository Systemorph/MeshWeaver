# ocr — Tesseract 5 behind a minimal HTTP server, cluster-internal. The language packs are BAKED IN
# (eng, deu, fra, ita, ind + osd for script/orientation): the runtime never downloads anything, the same
# reasoning as deploy/whisper and deploy/s1-extract — the registry the pod already pulls from is the one
# distribution channel.
#
#   docker build -t ocr:dev deploy/ocr
#
# The registry image is built by CI (.github/workflows/ocr-image.yml), never by hand.
# Debian trixie ships tesseract-ocr 5.5 and its packs from tessdata_fast (Apache-2.0).

FROM debian:trixie-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 python3-pil \
        tesseract-ocr tesseract-ocr-osd \
        tesseract-ocr-eng tesseract-ocr-deu tesseract-ocr-fra tesseract-ocr-ita tesseract-ocr-ind \
    && rm -rf /var/lib/apt/lists/* /usr/share/doc /usr/share/man
COPY app /app
COPY fixtures/*.png /fixtures/
WORKDIR /app
# The decisions and the HTTP bounds, offline, with a fake engine.
RUN python3 test_server.py
# The REAL engine and packs through the REAL contract on the fixtures, with its negative controls —
# in the stage that ships, so a green build IS the test.
RUN OCR_FIXTURES=/fixtures python3 smoke.py
RUN useradd --uid 10001 --no-create-home ocr && rm -rf /app/__pycache__
USER 10001
ENV OCR_FIXTURES=/fixtures OMP_THREAD_LIMIT=1 HOME=/tmp
EXPOSE 8080
CMD ["python3", "server.py"]
