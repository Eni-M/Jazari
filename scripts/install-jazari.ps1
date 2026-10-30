# Sets Jazari up on this machine, once. Run it from anywhere inside your MyOwnLLM clone; nothing is hard-coded.
# After this, `jazari update` keeps everything current and the installer isn't needed again.
#
#   .\scripts\install-jazari.ps1                         # installs into %LOCALAPPDATA%\Jazari (or where you chose last time)
#   .\scripts\install-jazari.ps1 -InstallDir D:\Tools\Jazari   # installs wherever you want
#
#  1. clones GitHub main into <InstallDir>\source, Jazari's private copy that nobody edits,
#  2. reuses the Granite model and llama.cpp from this clone's spike folder when it has them (skips a 2 GB download),
#  3. writes %APPDATA%\Jazari\config.json so jazari knows where all of this is,
#  4. runs the updater from the private copy, which downloads, builds and checks the rest,
#  5. installs the `jazari` command (a global .NET tool by default, <InstallDir>\bin on PATH for a custom folder).
# The command is replaced last, so an install that fails half-way leaves the Jazari you had still working.
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
$previous = $null
if (Test-Path $configPath) {
    try { $previous = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json }
    catch { Write-Host "Ignoring the unreadable $configPath" }
}
if (-not $InstallDir) { $InstallDir = $previous.installDir }
if (-not $InstallDir) { $InstallDir = $defaultDir }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$InstallDir = (Resolve-Path $InstallDir).Path
$customDir = $InstallDir -ne $defaultDir
Write-Host "Installing Jazari into $InstallDir"

# This install's running servers would lock their files; jazari restarts them on demand.
$servers = @(Get-Process "llama-server", "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith("$InstallDir\", [StringComparison]::OrdinalIgnoreCase) })
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

# 3. config; the previous values stay until the updater has built their replacements
New-Item -ItemType Directory -Force (Split-Path $configPath) | Out-Null
[ordered]@{
    home            = $root
    repoUrl         = $repoUrl
    installDir      = $InstallDir
    sourceDir       = $source
    installedCommit = $previous.installedCommit
    gatewayExe      = $previous.gatewayExe
    llamaServer     = $previous.llamaServer
    model           = $previous.model
} | ConvertTo-Json | Set-Content -Encoding UTF8 "$configPath.new"
Move-Item -Force "$configPath.new" $configPath

# 4. everything else
& (Join-Path $source "scripts\update-jazari.ps1") -Reexec
$updated = $LASTEXITCODE
if (-not (Test-Path (Join-Path $InstallDir "app\jazari.exe"))) {
    Write-Host ""
    Write-Host "Jazari couldn't be built. Fix what failed above, then run this installer again."
    exit 1
}

# 5. the jazari command (the launcher); each run gets a higher package version so the tool update always takes the new build
$artifacts = Join-Path $source "artifacts"
$package = "0.3.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
dotnet pack (Join-Path $source "src\Jazari.Launcher") -c Release -o $artifacts -p:PackageVersion=$package --nologo -v q --disable-build-servers
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

if ($updated -ne 0) {
    Write-Host ""
    Write-Host "Jazari is installed, but something above failed. Fix it, then run: jazari update"
    exit $updated
}

Write-Host ""
Write-Host "Jazari is installed in $InstallDir. Open a new terminal and type: jazari"
