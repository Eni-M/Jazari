# `jazari update`: makes this machine's Jazari match GitHub main, then checks and repairs every component.
# The jazari launcher runs this and reopens Jazari afterwards. It only ever touches Jazari's own install folder.
#
#   exit 0  everything is up to date
#   exit 1  something failed (each failure printed its ERROR line); what failed keeps its previous version
#   exit 2  the user chose not to close the open Jazari windows; nothing changed
#
# -Reexec means the source copy is already synced: the script re-runs itself after the sync so that
# changes to this file apply in the same update, and the installer starts here too.
param([switch]$Reexec)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:GIT_TERMINAL_PROMPT = "0" # git must never stop to ask for credentials
$configPath = Join-Path $env:APPDATA "Jazari\config.json"
$script:failed = $false

# A char code, not the literal: PowerShell 5.1 reads a script without a BOM as ANSI.
function Say-Ok($thing) { Write-Host "$([char]0x2713) $thing OK" }
function Say-Repaired($thing) { Write-Host "! $thing repaired" }
function Say-Error($thing, $reason) { Write-Host "ERROR ${thing}: $reason"; $script:failed = $true }

# Git by itself: stderr must not count as a PowerShell error (git writes progress there), so failure is its exit code.
function Invoke-Git {
    $ErrorActionPreference = "Continue"
    $lines = & git -C $sourceDir @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw (($lines | Where-Object { $_ }) -join " ") }
    $lines
}

function Download($url, $target) {
    Write-Host "Downloading $url"
    curl.exe -L --fail --progress-bar -o $target $url
    if ($LASTEXITCODE -ne 0) { throw "download failed: $url" }
}

# Puts a prepared folder in place. If the swap fails half-way the old folder goes back.
function Swap-Folder($new, $target) {
    $old = "$target.old"
    if (Test-Path $old) { Remove-Item -Recurse -Force $old }
    if (Test-Path $target) { Move-Item $target $old }
    try { Move-Item $new $target }
    catch { if (Test-Path $old) { Move-Item $old $target }; throw }
    if (Test-Path $old) { Remove-Item -Recurse -Force $old }
}

function Test-Model($path) {
    (Test-Path $path) -and (Get-FileHash -Algorithm SHA256 $path).Hash -eq $manifest.modelSha256
}

# llama-server reports the release "b11982" as "build 11982".
function Test-Llama($folder) {
    $exe = Join-Path $folder "llama-server.exe"
    if (-not (Test-Path $exe)) { return $false }
    $ErrorActionPreference = "Continue"
    $version = (& $exe --version 2>&1 | ForEach-Object { "$_" }) -join " "
    $version -match "\bbuild $($manifest.llamaRelease.TrimStart('b'))\b"
}

function Repair-Model {
    $model = Join-Path $installDir "models\$($manifest.modelFile)"
    $download = "$model.download"
    if (Test-Path $download) { Remove-Item -Force $download } # left by an interrupted update
    if (Test-Model $model) { Say-Ok "model"; return }
    New-Item -ItemType Directory -Force (Split-Path $model) | Out-Null
    Download $manifest.modelUrl $download
    if (-not (Test-Model $download)) { Remove-Item -Force $download; throw "the download doesn't match the pinned checksum" }
    Move-Item -Force $download $model
    Say-Repaired "model"
}

function Repair-Llama {
    $llama = Join-Path $installDir "llama"
    $new = "$llama.new"
    $zip = "$llama.download.zip" # Expand-Archive insists on .zip
    foreach ($leftover in $new, $zip) { if (Test-Path $leftover) { Remove-Item -Recurse -Force $leftover } }
    if (Test-Llama $llama) { Say-Ok "llama.cpp"; return }
    Download "https://github.com/ggml-org/llama.cpp/releases/download/$($manifest.llamaRelease)/$($manifest.llamaZip)" $zip
    Expand-Archive $zip $new
    Remove-Item -Force $zip
    if (-not (Test-Llama $new)) { Remove-Item -Recurse -Force $new; throw "the download doesn't report release $($manifest.llamaRelease)" }
    Swap-Folder $new $llama
    Say-Repaired "llama.cpp"
}

# A build is current when its exe is there and its marker names the source commit.
function Repair-Build($thing, $project, $folder, $exe) {
    $marker = Join-Path $folder "build-commit.txt"
    if ((Test-Path (Join-Path $folder $exe)) -and (Test-Path $marker) -and (Get-Content -Raw $marker).Trim() -eq $commit) { Say-Ok $thing; return }
    $new = "$folder.new"
    if (Test-Path $new) { Remove-Item -Recurse -Force $new }
    dotnet publish (Join-Path $sourceDir "src\$project") -c Release -o $new --nologo -v q
    if ($LASTEXITCODE -ne 0) { if (Test-Path $new) { Remove-Item -Recurse -Force $new }; throw "the build failed" }
    Set-Content -Encoding ASCII (Join-Path $new "build-commit.txt") $commit
    Swap-Folder $new $folder
    Say-Repaired $thing
}

# 1. config
try {
    $config = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json
    if (-not ($config.installDir -and $config.sourceDir -and $config.repoUrl)) { throw "installDir, sourceDir or repoUrl is missing" }
}
catch {
    Write-Host "ERROR config: $_"
    exit 1
}
$installDir = $config.installDir
$sourceDir = $config.sourceDir

if (-not $Reexec) {
    # 2. nothing of Jazari's may be running while it is replaced. Launchers aren't in app\ and keep waiting.
    $appDir = (Join-Path $installDir "app") + "\"
    $open = @(Get-Process "jazari" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) })
    if ($open.Count -gt 0) {
        Write-Host "Jazari is open in $($open.Count) other window(s):"
        $open | ForEach-Object { Write-Host "  process $($_.Id), started $($_.StartTime)" }
        if ((Read-Host "Close them and continue? [y/n]") -ne "y") { exit 2 }
    }
    $running = $open + @(Get-Process "llama-server", "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue # stopped processes hold file locks for a moment
    }

    # 3. the source copy; nobody edits it, so it can simply be made to match main
    try {
        Invoke-Git remote set-url origin $config.repoUrl | Out-Null
        Invoke-Git fetch origin main | Out-Null
        Invoke-Git reset --hard origin/main | Out-Null
        Invoke-Git clean -fd | Out-Null
    }
    catch {
        Write-Host "ERROR source: could not reach GitHub ($_)"
        exit 1
    }
    Say-Ok "source"
    & $PSCommandPath -Reexec
    exit $LASTEXITCODE
}

$manifest = Get-Content -Raw (Join-Path $PSScriptRoot "jazari-manifest.json") | ConvertFrom-Json
$commit = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()

# 4-6. each component on its own: one that fails keeps its previous version and the others still run
try { Repair-Model } catch { Say-Error "model" $_ }
try { Repair-Llama } catch { Say-Error "llama.cpp" $_ }
try { Repair-Build "gateway" "MyOwnLLM.Gateway" (Join-Path $installDir "gateway") "MyOwnLLM.Gateway.exe" } catch { Say-Error "gateway" $_ }
try { Repair-Build "app" "MyOwnLLM.Cli" (Join-Path $installDir "app") "jazari.exe" } catch { Say-Error "app" $_ }

# 7. config: current paths, and installedCommit only when everything above worked
$paths = @{
    gatewayExe  = Join-Path $installDir "gateway\MyOwnLLM.Gateway.exe"
    llamaServer = Join-Path $installDir "llama\llama-server.exe"
    model       = Join-Path $installDir "models\$($manifest.modelFile)"
}
$stale = @($paths.Keys | Where-Object { $config.$_ -ne $paths[$_] }).Count -gt 0
try {
    # Written beside the config and moved over it, so an interrupted write can't leave a broken file.
    [ordered]@{
        home            = $config.home
        repoUrl         = $config.repoUrl
        installDir      = $installDir
        sourceDir       = $sourceDir
        installedCommit = if ($script:failed) { $config.installedCommit } else { $commit }
        gatewayExe      = $paths.gatewayExe
        llamaServer     = $paths.llamaServer
        model           = $paths.model
    } | ConvertTo-Json | Set-Content -Encoding UTF8 "$configPath.new"
    Move-Item -Force "$configPath.new" $configPath
    if ($stale) { Say-Repaired "config" } else { Say-Ok "config" }
}
catch { Say-Error "config" $_ }

# 8.
if ($script:failed) { exit 1 }
Write-Host "Jazari is up to date ($($commit.Substring(0, 7)))"
exit 0
