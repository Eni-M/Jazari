# Sets Jazari up on this machine. Run it from anywhere inside your MyOwnLLM clone; nothing is hard-coded:
#  1. finds llama-server (spike\bin) or downloads the pinned llama.cpp build,
#  2. finds the Granite model (spike\models) or downloads it from IBM's Hugging Face repo,
#  3. builds a ready-to-run gateway into %LOCALAPPDATA%\Jazari\gateway,
#  4. installs or updates the `jazari` command (.NET global tool),
#  5. writes %APPDATA%\Jazari\config.json so jazari knows where all of this is.
# The API key is created by jazari itself on first launch.
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$local = Join-Path $env:LOCALAPPDATA "Jazari"
$llamaRelease = "b11982"
$modelFile = "granite-4.1-3b-Q4_K_M.gguf"

function Download($url, $target) {
    Write-Host "Downloading $url"
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    curl.exe -L --fail --progress-bar -o $target $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
}

# 1. llama-server
$llama = Join-Path $root "spike\bin\llama-server.exe"
if (-not (Test-Path $llama)) {
    $llamaDir = Join-Path $local "llama"
    $llama = Join-Path $llamaDir "llama-server.exe"
    if (-not (Test-Path $llama)) {
        $zip = Join-Path $env:TEMP "llama-$llamaRelease.zip"
        Download "https://github.com/ggml-org/llama.cpp/releases/download/$llamaRelease/llama-$llamaRelease-bin-win-vulkan-x64.zip" $zip
        Expand-Archive -Force $zip $llamaDir
        Remove-Item $zip
    }
}

# 2. model
$model = Join-Path $root "spike\models\$modelFile"
if (-not (Test-Path $model)) {
    $model = Join-Path $local "models\$modelFile"
    if (-not (Test-Path $model)) {
        Download "https://huggingface.co/ibm-granite/granite-4.1-3b-GGUF/resolve/main/$modelFile" $model
    }
}

# 3. gateway (a running one would lock its files, so stop it first; jazari restarts it on demand)
$gatewayDir = Join-Path $local "gateway"
Get-Process "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "Stopping the running gateway"; $_ | Stop-Process -Force }
dotnet publish "$root\src\MyOwnLLM.Gateway" -c Release -o $gatewayDir --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. the jazari command; each run gets a higher package version so `dotnet tool update` always takes the new build
$artifacts = Join-Path $root "artifacts"
$package = "0.2.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
dotnet pack "$root\src\MyOwnLLM.Cli" -c Release -o $artifacts -p:PackageVersion=$package --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet tool update --global Jazari --add-source $artifacts --version $package
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 5. config
$configPath = Join-Path $env:APPDATA "Jazari\config.json"
New-Item -ItemType Directory -Force (Split-Path $configPath) | Out-Null
[ordered]@{
    home        = $root
    gatewayExe  = Join-Path $gatewayDir "MyOwnLLM.Gateway.exe"
    llamaServer = $llama
    model       = $model
} | ConvertTo-Json | Set-Content -Encoding UTF8 $configPath

Write-Host ""
Write-Host "Jazari is installed. Open a new terminal and type: jazari"
