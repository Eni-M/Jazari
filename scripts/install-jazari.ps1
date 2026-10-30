# Sets Jazari up on this machine. Run it from anywhere inside your MyOwnLLM clone; nothing is hard-coded.
#
#   .\scripts\install-jazari.ps1                         # installs into %LOCALAPPDATA%\Jazari (or where you chose last time)
#   .\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari   # installs wherever you want
#
#  1. finds llama-server (spike\bin) or downloads the pinned llama.cpp build into <InstallDir>\llama,
#  2. finds the Granite model (spike\models) or downloads it into <InstallDir>\models,
#  3. builds a ready-to-run gateway into <InstallDir>\gateway,
#  4. installs or updates the `jazari` command (a global .NET tool by default, <InstallDir>\bin on PATH for a custom folder),
#  5. writes %APPDATA%\Jazari\config.json so jazari knows where all of this is.
# The API key is created by jazari itself on first launch.
param([string]$InstallDir)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$configPath = Join-Path $env:APPDATA "Jazari\config.json"
$defaultDir = Join-Path $env:LOCALAPPDATA "Jazari"
$llamaRelease = "b11982"
$modelFile = "granite-4.1-3b-Q4_K_M.gguf"

# Where to install: the -InstallDir you pass, else the folder from the last install, else the default.
if (-not $InstallDir -and (Test-Path $configPath)) {
    $previous = (Get-Content -Raw $configPath | ConvertFrom-Json).installDir
    if ($previous) { $InstallDir = $previous }
}
if (-not $InstallDir) { $InstallDir = $defaultDir }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$InstallDir = (Resolve-Path $InstallDir).Path
$customDir = $InstallDir -ne $defaultDir
Write-Host "Installing Jazari into $InstallDir"

function Download($url, $target) {
    Write-Host "Downloading $url"
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    curl.exe -L --fail --progress-bar -o $target $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
}

# 1. llama-server
$llama = Join-Path $root "spike\bin\llama-server.exe"
if (-not (Test-Path $llama)) {
    $llamaDir = Join-Path $InstallDir "llama"
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
    $model = Join-Path $InstallDir "models\$modelFile"
    if (-not (Test-Path $model)) {
        Download "https://huggingface.co/ibm-granite/granite-4.1-3b-GGUF/resolve/main/$modelFile" $model
    }
}

# 3. gateway (a running one would lock its files, so stop it first; jazari restarts it on demand)
$gatewayDir = Join-Path $InstallDir "gateway"
Get-Process "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "Stopping the running gateway"; $_ | Stop-Process -Force }
dotnet publish "$root\src\MyOwnLLM.Gateway" -c Release -o $gatewayDir --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. the jazari command; each run gets a higher package version so the tool update always takes the new build
$artifacts = Join-Path $root "artifacts"
$package = "0.2.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
dotnet pack "$root\src\MyOwnLLM.Cli" -c Release -o $artifacts -p:PackageVersion=$package --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($customDir) {
    $binDir = Join-Path $InstallDir "bin"
    # One jazari on PATH only: drop a global copy from an earlier default install.
    & { $ErrorActionPreference = "Continue"; dotnet tool uninstall --global Jazari *> $null }   # fine if it wasn't installed
    dotnet tool update --tool-path $binDir Jazari --add-source $artifacts --version $package
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not (($userPath -split ";") -contains $binDir)) {
        [Environment]::SetEnvironmentVariable("Path", (($userPath.TrimEnd(";"), $binDir) -join ";"), "User")
        Write-Host "Added $binDir to your PATH"
    }
} else {
    dotnet tool update --global Jazari --add-source $artifacts --version $package
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# 5. config
New-Item -ItemType Directory -Force (Split-Path $configPath) | Out-Null
[ordered]@{
    home        = $root
    installDir  = $InstallDir
    gatewayExe  = Join-Path $gatewayDir "MyOwnLLM.Gateway.exe"
    llamaServer = $llama
    model       = $model
} | ConvertTo-Json | Set-Content -Encoding UTF8 $configPath

Write-Host ""
Write-Host "Jazari is installed in $InstallDir. Open a new terminal and type: jazari"
