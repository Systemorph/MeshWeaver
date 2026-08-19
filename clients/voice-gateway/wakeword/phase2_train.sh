#!/bin/sh
# Phase 2 chain: data+features (2a), then training config + 10k-step training + quantized
# streaming tflite export (2b) — the notebook's cells 9–10 verbatim.
set -e
cd "$HOME/models/wakeword"
V=./venv/bin

echo "===== PHASE 2a: data + features ====="
$V/python phase2_data.py

echo "===== PHASE 2b: training config ====="
$V/python - <<'EOF'
import yaml
config = {
    "window_step_ms": 10,
    "train_dir": "trained_models/wakeword",
    "features": [
        {"features_dir": "generated_augmented_features", "sampling_weight": 2.0,
         "penalty_weight": 1.0, "truth": True, "truncation_strategy": "truncate_start", "type": "mmap"},
        {"features_dir": "negative_datasets/speech", "sampling_weight": 10.0,
         "penalty_weight": 1.0, "truth": False, "truncation_strategy": "random", "type": "mmap"},
        {"features_dir": "negative_datasets/dinner_party", "sampling_weight": 10.0,
         "penalty_weight": 1.0, "truth": False, "truncation_strategy": "random", "type": "mmap"},
        {"features_dir": "negative_datasets/no_speech", "sampling_weight": 5.0,
         "penalty_weight": 1.0, "truth": False, "truncation_strategy": "random", "type": "mmap"},
        {"features_dir": "negative_datasets/dinner_party_eval", "sampling_weight": 0.0,
         "penalty_weight": 1.0, "truth": False, "truncation_strategy": "split", "type": "mmap"},
    ],
    "training_steps": [10000],
    "positive_class_weight": [1],
    "negative_class_weight": [20],
    "learning_rates": [0.001],
    "batch_size": 128,
    "time_mask_max_size": [0], "time_mask_count": [0],
    "freq_mask_max_size": [0], "freq_mask_count": [0],
    "eval_step_interval": 500,
    "clip_duration_ms": 1500,
    "target_minimization": 0.9,
    "minimization_metric": None,
    "maximization_metric": "average_viable_recall",
}
yaml.dump(config, open("training_parameters.yaml", "w"))
print("training_parameters.yaml written")
EOF

echo "===== PHASE 2b: train (10k steps) ====="
$V/python -m microwakeword.model_train_eval \
  --training_config=training_parameters.yaml \
  --train 1 --restore_checkpoint 1 \
  --test_tf_nonstreaming 0 --test_tflite_nonstreaming 0 \
  --test_tflite_nonstreaming_quantized 0 --test_tflite_streaming 0 \
  --test_tflite_streaming_quantized 1 \
  --use_weights best_weights \
  mixednet \
  --pointwise_filters "64,64,64,64" \
  --repeat_in_block "1, 1, 1, 1" \
  --mixconv_kernel_sizes '[5], [7,11], [9,15], [23]' \
  --residual_connection "0,0,0,0" \
  --first_conv_filters 32 --first_conv_kernel_size 5 --stride 3

echo "===== PHASE 2 COMPLETE ====="
ls -lh trained_models/wakeword/tflite_stream_state_internal_quant/ 2>/dev/null
