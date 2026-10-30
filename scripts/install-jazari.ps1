# Sets Jazari up on this machine, once. Run it from anywhere inside your MyOwnLLM clone; nothing is hard-coded.
# After this, `jazari update` keeps everything current and the installer isn't needed again.
#
#   .\scripts\install-jazari.ps1                         # installs into %LOCALAPPDATA%\Jazari (or where you chose last time)
#   .\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari   # installs wherever you want
#
#  1. clones GitHub main into <InstallDir>\source, Jazari's private copy that nobody edits,
#  2. reuses the Granite model and llama.cpp from this clone's spike folder when it has them (skips a 2 GB download),
#  3. installs the `jazari` command (a global .NET tool by default, <InstallDir>\bin on PATH for a custom folder),
#  4. writes %APPDATA%\Jazari\config.json so jazari knows where all of this is,
#  5. runs the updater from the private copy, which downloads, builds and checks the rest.
# The API key is created by jazari itself on first launch.
param([string]$InstallDir)

$ErrorActionPreference = "Stop"
if (Get-Process "jazari" -ErrorAction SilentlyContinue) {
    Write-Host "Close every open jazari session first (a running jazari locks its own files), then run this again."
    exit 1
}
$root = (Resolve-Path "$PSScriptRoot\..").Path
$configPath = Join-Path $env:APPDATA "Jazari\config.json"
$defaultDir = Join-Path $env:LOCALAPPDATA "Jazari"

# Where to install: the -InstallDir you pass, else the folder from the last install, else the default.
if (-not $InstallDir -and (Test-Path $configPath)) {
    $previous = (Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json).installDir
    if ($previous) { $InstallDir = $previous }
}
if (-not $InstallDir) { $InstallDir = $defaultDir }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$InstallDir = (Resolve-Path $InstallDir).Path
$customDir = $InstallDir -ne $defaultDir
Write-Host "Installing Jazari into $InstallDir"

# Running servers would lock their files; jazari restarts them on demand.
$servers = @(Get-Process "llama-server", "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue)
if ($servers.Count -gt 0) {
    Write-Host "Stopping the running servers"
    $servers | Stop-Process -Force
    $servers | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 1. the private source copy
$repoUrl = git -C $root remote get-url origin
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$source = Join-Path $InstallDir "source"
if (Test-Path (Join-Path $source ".git")) {
    git -C $source remote set-url origin $repoUrl
    git -C $source fetch origin main
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    git -C $source reset --hard origin/main
} else {
    git clone --branch main $repoUrl $source
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. what this clone already has; the updater verifies both and downloads whatever is still missing
$manifest = Get-Content -Raw (Join-Path $source "scripts\jazari-manifest.json") | ConvertFrom-Json
$localModel = Join-Path $root "spike\models\$($manifest.modelFile)"
$model = Join-Path $InstallDir "models\$($manifest.modelFile)"
if ((Test-Path $localModel) -and -not (Test-Path $model)) {
    Write-Host "Copying the model from this clone"
    New-Item -ItemType Directory -Force (Split-Path $model) | Out-Null
    Copy-Item $localModel $model
}
$localLlama = Join-Path $root "spike\bin"
$llama = Join-Path $InstallDir "llama"
if ((Test-Path (Join-Path $localLlama "llama-server.exe")) -and -not (Test-Path $llama)) {
    Write-Host "Copying llama.cpp from this clone"
    Copy-Item -Recurse $localLlama $llama
}

# 3. the jazari command (the launcher); each run gets a higher package version so the tool update always takes the new build
$artifacts = Join-Path $source "artifacts"
$package = "0.3.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
dotnet pack (Join-Path $source "src\Jazari.Launcher") -c Release -o $artifacts -p:PackageVersion=$package --nologo -v q
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

# 4. config; the updater fills in the component paths and installedCommit
New-Item -ItemType Directory -Force (Split-Path $configPath) | Out-Null
[ordered]@{
    home       = $root
    repoUrl    = $repoUrl
    installDir = $InstallDir
    sourceDir  = $source
} | ConvertTo-Json | Set-Content -Encoding UTF8 $configPath

# 5. everything else
& (Join-Path $source "scripts\update-jazari.ps1") -Reexec
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Jazari is installed, but something above failed. Fix it, then run: jazari update"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Jazari is installed in $InstallDir. Open a new terminal and type: jazari"
