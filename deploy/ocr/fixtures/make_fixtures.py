"""Renders the smoke-test fixtures committed beside this file. Run it only to change them:

    docker run --rm -v "$PWD/deploy/ocr/fixtures:/f" -w /f debian:trixie-slim sh -c \
      'apt-get update -qq && apt-get install -y -qq python3-pil fonts-dejavu-core >/dev/null && python3 make_fixtures.py'

de-id.png  a German paragraph above an Indonesian one — the positive case (text AND both languages).
en.png     English only — the negative control: the same German/Indonesian assertions must FAIL on it.
"""
from PIL import Image, ImageDraw, ImageFont

FONT = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 34)

PAGES = {
    "de-id.png": [
        "Die Versicherung hat den Schaden am Gebäude",
        "geprüft und wird die Rechnung bis Ende des",
        "Monats bezahlen. Der Bericht ist nicht vollständig.",
        "",
        "Pihak asuransi sudah memeriksa kerusakan pada",
        "bangunan dan akan membayar tagihan itu sebelum",
        "akhir bulan. Laporan yang dikirim belum lengkap.",
    ],
    "en.png": [
        "The insurer has reviewed the damage to the building",
        "and will pay the invoice before the end of the month.",
        "The report that was sent is not complete.",
    ],
}

for name, lines in PAGES.items():
    img = Image.new("L", (1200, 80 + 52 * len(lines)), 255)
    draw = ImageDraw.Draw(img)
    for i, line in enumerate(lines):
        draw.text((60, 40 + 52 * i), line, font=FONT, fill=0)
    img.save(name, optimize=True)
    print("wrote", name, img.size)
