"""Phase 2a — augmentation data + features, faithfully following microWakeWord's
basic_training_notebook (cells 4–9). Run from ~/models/wakeword with the venv.

Upstream's own note: the downloaded augmentation data mixes licenses — a model trained
with it is for NON-COMMERCIAL personal use. Fits the satellite's posture.
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

os.chdir(os.path.expanduser("~/models/wakeword"))

def stage(name):
    print(f"\n===== {name} =====", flush=True)

stage("merge samples into generated_samples")
merged = Path("generated_samples")
merged.mkdir(exist_ok=True)
for src, prefix in [("samples", "hm"), ("samples-alt", "alt")]:
    for f in Path(src).glob("*.wav"):
        target = merged / f"{prefix}_{f.name}"
        if not target.exists():
            shutil.copy(f, target)
print("merged:", len(list(merged.glob('*.wav'))))

import datasets
import numpy as np
import scipy.io.wavfile
from tqdm import tqdm

stage("MIT RIRs (direct 16khz files — the datasets-API row shape changed upstream)")
if not os.path.exists("mit_rirs") or not os.listdir("mit_rirs"):
    os.makedirs("mit_rirs", exist_ok=True)
    import json as _json, urllib.request
    tree = _json.load(urllib.request.urlopen(
        "https://huggingface.co/api/datasets/davidscripka/MIT_environmental_impulse_responses/tree/main/16khz"))
    for entry in tqdm(tree):
        name = entry["path"].split("/")[-1]
        target = os.path.join("mit_rirs", name)
        if not os.path.exists(target):
            urllib.request.urlretrieve(
                "https://huggingface.co/datasets/davidscripka/MIT_environmental_impulse_responses/resolve/main/" + entry["path"],
                target)

stage("AudioSet slice (parquet shard — upstream retired the tar layout)")
if not os.path.exists("audioset/bal_train09.parquet"):
    os.makedirs("audioset", exist_ok=True)
    subprocess.run(["curl", "-fSL", "-o", "audioset/bal_train09.parquet.tmp",
                    "https://huggingface.co/datasets/agkphysics/AudioSet/resolve/main/data/bal_train/09.parquet"],
                   check=True)
    os.rename("audioset/bal_train09.parquet.tmp", "audioset/bal_train09.parquet")
if not os.path.exists("audioset_16k"):
    os.mkdir("audioset_16k")
    import io
    import pyarrow.parquet as pq
    import soundfile as sf
    from scipy.signal import resample_poly
    table = pq.read_table("audioset/bal_train09.parquet", columns=["audio"])
    for i, row in enumerate(tqdm(table.column("audio").to_pylist())):
        try:
            data, rate = sf.read(io.BytesIO(row["bytes"]))
            if data.ndim > 1:
                data = data.mean(axis=1)
            if rate != 16000:
                data = resample_poly(data, 16000, rate)
            scipy.io.wavfile.write(os.path.join("audioset_16k", f"as_{i}.wav"), 16000,
                                   (np.clip(data, -1, 1) * 32767).astype(np.int16))
        except Exception as e:
            print("skip", i, e)

stage("FMA xsmall")
if not os.path.exists("fma"):
    os.mkdir("fma")
    subprocess.run(["curl", "-fSL", "-o", "fma/fma_xs.zip",
                    "https://huggingface.co/datasets/mchl914/fma_xsmall/resolve/main/fma_xs.zip"],
                   check=True)
    subprocess.run(["unzip", "-q", "fma_xs.zip"], cwd="fma", check=True)
if not os.path.exists("fma_16k") or not os.listdir("fma_16k"):
    os.makedirs("fma_16k", exist_ok=True)
    mp3s = list(Path("fma/fma_small").glob("**/*.mp3"))
    for f in tqdm(mp3s):
        target = os.path.join("fma_16k", f.stem + ".wav")
        if not os.path.exists(target):
            r = subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(f),
                                "-ar", "16000", "-ac", "1", target])
            if r.returncode != 0 and os.path.exists(target):
                os.unlink(target)   # never leave a truncated wav for the augmenter

stage("negative feature sets")
if not os.path.exists("negative_datasets"):
    os.mkdir("negative_datasets")
    root = "https://huggingface.co/datasets/kahrendt/microwakeword/resolve/main/"
    for fname in ["dinner_party.zip", "dinner_party_eval.zip", "no_speech.zip", "speech.zip"]:
        subprocess.run(["curl", "-fSL", "-o", f"negative_datasets/{fname}", root + fname], check=True)
        subprocess.run(["unzip", "-q", f"negative_datasets/{fname}", "-d", "negative_datasets"], check=True)

stage("augment + spectrogram features")
from microwakeword.audio.augmentation import Augmentation
from microwakeword.audio.clips import Clips
from microwakeword.audio.spectrograms import SpectrogramGeneration
from mmap_ninja.ragged import RaggedMmap

clips = Clips(input_directory="generated_samples", file_pattern="*.wav",
              max_clip_duration_s=None, remove_silence=False,
              random_split_seed=10, split_count=0.1)
augmenter = Augmentation(augmentation_duration_s=3.2,
                         augmentation_probabilities={
                             "SevenBandParametricEQ": 0.1, "TanhDistortion": 0.1,
                             "PitchShift": 0.1, "BandStopFilter": 0.1,
                             "AddColorNoise": 0.1, "AddBackgroundNoise": 0.75,
                             "Gain": 1.0, "RIR": 0.5},
                         impulse_paths=["mit_rirs"],
                         background_paths=["fma_16k", "audioset_16k"],
                         background_min_snr_db=-5, background_max_snr_db=10,
                         min_jitter_s=0.195, max_jitter_s=0.205)

os.makedirs("generated_augmented_features", exist_ok=True)
for split in ["training", "validation", "testing"]:
    out_dir = os.path.join("generated_augmented_features", split)
    os.makedirs(out_dir, exist_ok=True)
    split_name, repetition = "train", 2
    spectrograms = SpectrogramGeneration(clips=clips, augmenter=augmenter, slide_frames=10, step_ms=10)
    if split == "validation":
        split_name, repetition = "validation", 1
    elif split == "testing":
        split_name, repetition = "test", 1
        spectrograms = SpectrogramGeneration(clips=clips, augmenter=augmenter, slide_frames=1, step_ms=10)
    RaggedMmap.from_generator(
        out_dir=os.path.join(out_dir, "wakeword_mmap"),
        sample_generator=spectrograms.spectrogram_generator(split=split_name, repeat=repetition),
        batch_size=100, verbose=True)

print("\n===== PHASE 2a DONE =====", flush=True)
