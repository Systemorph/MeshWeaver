#!/bin/sh
# Phase 1 of the "Hey Memex" wake word: training environment + synthetic sample generation.
# microWakeWord (the satellite's on-device engine) + piper-sample-generator (synthetic
# voicings). Output: ~/models/wakeword/samples/ ready for feature extraction + training.
set -e
W="$HOME/models/wakeword"
mkdir -p "$W" && cd "$W"

echo "=== clone"
[ -d microWakeWord ] || git clone --depth 1 https://github.com/kahrendt/microWakeWord.git
[ -d piper-sample-generator ] || git clone --depth 1 https://github.com/rhasspy/piper-sample-generator.git

echo "=== venv (3.12) + deps"
[ -d venv ] || python3.12 -m venv venv
./venv/bin/pip -q install --upgrade pip
./venv/bin/pip -q install torch torchaudio numpy
./venv/bin/pip -q install -e ./microWakeWord || echo "microWakeWord editable install failed — will pip its requirements at training time"

echo "=== sample generation model"
cd piper-sample-generator
[ -f models/en_US-libritts_r-medium.pt ] || { mkdir -p models; curl -fSL -o models/en_US-libritts_r-medium.pt \
  "https://github.com/rhasspy/piper-sample-generator/releases/download/v2.0.0/en_US-libritts_r-medium.pt"; }

echo "=== generate 'hey memex' samples (multiple pronunciations, many voices)"
mkdir -p "$W/samples"
../venv/bin/python generate_samples.py "hey memex" \
  --max-samples 1500 --batch-size 50 --output-dir "$W/samples" 2>&1 | tail -3
# A second phonetic spelling catches the 'MEE-mex' reading:
../venv/bin/python generate_samples.py "hey meemex" \
  --max-samples 500 --batch-size 50 --output-dir "$W/samples-alt" 2>&1 | tail -3 || true

echo "=== DONE phase 1"
ls "$W/samples" | wc -l
