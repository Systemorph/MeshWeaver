# "Hey Memex" — the custom wake word

A microWakeWord model (61 KB streaming tflite) trained 2026-08-19 on 2000 synthetic
voicings ("hey memex" + "hey meemex", piper-sample-generator) with the upstream
basic-training recipe, updated for current dependency reality (see the scripts: parquet
AudioSet, direct 16 kHz RIRs, ffmpeg FMA conversion, setuptools<81, tensorboard,
audiomentations>=0.43).

ROC (streaming, quantized): cutoff **0.89** ships in `hey_memex.json` — frr 11.5%,
0.19 false accepts/hour; 0.99 = zero faph at frr 21%; 0.80 = frr 8% at 0.375 faph.
Tune `probability_cutoff` in the manifest without retraining.

Deploy: add to the Satellite1-ESPHome config's `micro_wake_word.models`:
`- model: hey_memex.json` + `id: hey_memex` (files beside the main yaml; local paths must
not contain `../`), build with ESPHome ≥ 2026.7.0, `esphome run … --device <ip>` (factory
OTA is passwordless on the LAN). The gateway activates it via `WAKE_WORD=hey_memex`.

⚠️ Trained with mixed-license augmentation data (upstream's note): NON-COMMERCIAL
personal use.
