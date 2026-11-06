# Starts llama-server with the settings measured for this laptop.
param(
    [string]$ModelPath = "$PSScriptRoot\..\spike\models\granite-4.0-h-tiny-Q4_K_M.gguf",
    [int]$Port = 8080
)

$server = "$PSScriptRoot\..\spike\bin\llama-server.exe"
& $server -m $ModelPath `
    -dev none -t 6 --poll 0 `
    -c 16384 --cache-ram 1024 -ctxcp 8 -np 1 --cache-reuse 256 --no-webui `
    --host 127.0.0.1 --port $Port
