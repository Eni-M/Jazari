# Starts llama-server with the settings measured for this laptop (see docs/superpowers/specs/2026-10-30-myownllm-design.md).
param(
    [string]$ModelPath = "$PSScriptRoot\..\spike\models\granite-4.0-h-tiny-Q4_K_M.gguf",
    [int]$Port = 8080
)

$server = "$PSScriptRoot\..\spike\bin\llama-server.exe"
& $server -m $ModelPath `
    -dev none -t 6 --poll 0 `
    -c 16384 --cache-ram 2048 -np 1 --cache-reuse 0 --ctx-checkpoints 8 --no-webui `
    --host 127.0.0.1 --port $Port
