#!/usr/bin/env bash
# Runs every candidate in turn; a model that can't load is recorded by its own failure message and the rest still run.
M=spike/models
run() { label="$1"; shift; echo "=== $label $(date +%H:%M)"; py -I eval/run_eval.py --label "$label" --scenarios "$@" 2>&1 | tail -1; }
run 3b-mx150 --model "$M/granite-4.1-3b-Q4_K_M.gguf" --threads 4 --device Vulkan1 --gpu-layers 18
run tiny-cpu --model "$M/granite-4.0-h-tiny-Q4_K_M.gguf" --threads 4
run 3b-cpu --model "$M/granite-4.1-3b-Q4_K_M.gguf" --threads 4
run g42-cpu --model "$M/granite-4.2-3b-Q4_K_M.gguf" --threads 4 --reasoning off
run qwen3-cpu --model "$M/qwen3-4b-instruct-2507-Q4_K_M.gguf" --threads 4
echo "DONE $(date +%H:%M)"
