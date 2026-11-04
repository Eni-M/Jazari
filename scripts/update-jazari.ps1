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
    # An empty stderr line comes back as the name of PowerShell's wrapper exception.
    if ($LASTEXITCODE -ne 0) { throw (($lines | Where-Object { $_ -and $_ -notmatch "RemoteException" }) -join " ") }
    $lines
}

function Download($url, $target, [switch]$Resume) {
    $ErrorActionPreference = "Continue" # the progress bar is on stderr; failure is curl's exit code
    $from = if ($Resume -and (Test-Path $target)) { @("-C", "-") } else { @() }
    Write-Host "$(if ($from) { 'Resuming' } else { 'Downloading' }) $url"
    curl.exe -L --fail --progress-bar @from -o $target $url
    # 22 (an HTTP error, as for a range past the end) and 33 (a server without ranges) won't pass by resuming again.
    if ($from -and $LASTEXITCODE -in 22, 33) { Remove-Item -Force $target; Download $url $target; return }
    if ($LASTEXITCODE -ne 0) { throw "download failed: $url" }
}

# Downloads into $download until & $verified passes. What an earlier update didn't finish carries on where it stopped:
# the model is 4 GB, and over Wi-Fi it rarely arrives in one go. A resumed file that doesn't verify is fetched once more
# from the start, since the part kept from before may be what's wrong.
function Get-Verified($url, $download, $verified) {
    $resumed = Test-Path $download
    if (-not (& $verified $download)) { Download $url $download -Resume }
    if (-not (& $verified $download) -and $resumed) { Remove-Item -Force $download; Download $url $download }
    if (-not (& $verified $download)) { Remove-Item -Force $download; throw "the download doesn't match the pinned checksum" }
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
    try { $version = (& $exe --version 2>&1 | ForEach-Object { "$_" }) -join " " }
    catch { return $false } # a damaged exe won't even start
    $version -match "\bbuild $($manifest.llamaRelease.TrimStart('b'))\b"
}

# The law book (lawbook/build.py): Microsoft's .NET docs the gateway searches. Optional, so it's installed only once the
# manifest names it (lawBookUrl and lawBookSha256), and where the gateway looks for it by default.
function Repair-LawBook {
    if (-not $manifest.lawBookUrl) { return }
    $book = Join-Path $env:LOCALAPPDATA "MyOwnLLM\law-book.db"
    $download = "$book.download"
    $verified = { param($file) (Test-Path $file) -and (Get-FileHash -Algorithm SHA256 $file).Hash -eq $manifest.lawBookSha256 }
    if (& $verified $book) {
        if (Test-Path $download) { Remove-Item -Force $download }
        Say-Ok "law book"; return
    }
    New-Item -ItemType Directory -Force (Split-Path $book) | Out-Null
    Get-Verified $manifest.lawBookUrl $download $verified
    Move-Item -Force $download $book # the gateway opens it only per search, so it's never held open
    Say-Repaired "law book"
}

# The smart model /deep uses. Optional, like the law book: installed only once the manifest names it, and Jazari works without it.
function Repair-DeepModel {
    if (-not $manifest.deepModelUrl) { return }
    $model = Join-Path $installDir "models\$($manifest.deepModelFile)"
    $download = "$model.download"
    $verified = { param($file) (Test-Path $file) -and (Get-FileHash -Algorithm SHA256 $file).Hash -eq $manifest.deepModelSha256 }
    if (& $verified $model) {
        if (Test-Path $download) { Remove-Item -Force $download }
        Say-Ok "smart model"; return
    }
    New-Item -ItemType Directory -Force (Split-Path $model) | Out-Null
    Get-Verified $manifest.deepModelUrl $download $verified
    Move-Item -Force $download $model
    Say-Repaired "smart model"
}

function Repair-Model {
    $model = Join-Path $installDir "models\$($manifest.modelFile)"
    $download = "$model.download"
    if (Test-Model $model) {
        if (Test-Path $download) { Remove-Item -Force $download }
        Say-Ok "model"; return
    }
    New-Item -ItemType Directory -Force (Split-Path $model) | Out-Null
    Get-Verified $manifest.modelUrl $download ${function:Test-Model}
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
    # Checked before it's unpacked or run, like the model: the version it prints is no proof the files weren't changed.
    if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $manifest.llamaSha256) { Remove-Item -Force $zip; throw "the download doesn't match the pinned checksum" }
    Expand-Archive $zip $new
    Remove-Item -Force $zip
    if (-not (Test-Llama $new)) { Remove-Item -Recurse -Force $new; throw "the download doesn't report release $($manifest.llamaRelease)" }
    Swap-Folder $new $llama
    Say-Repaired "llama.cpp"
}

# A build is current when its exe is there and its marker names the source commit.
function Test-Build($folder, $exe) {
    $marker = Join-Path $folder "build-commit.txt"
    (Test-Path (Join-Path $folder $exe)) -and (Test-Path $marker) -and "$(Get-Content -Raw $marker)".Trim() -eq $commit
}

# Builds into <folder>.new and leaves it there; the caller swaps it in. Throws when the build fails.
function New-Build($thing, $project, $folder) {
    $new = "$folder.new"
    if (Test-Path $new) { Remove-Item -Recurse -Force $new }
    # No shared compiler server: one left over from an app with redirected AppData (Claude Desktop) would write the build there.
    # Its output stays hidden unless it fails. Own scope: "Continue" must not reach the swap below, which relies on throwing.
    Write-Host "Building $thing..."
    $output = & { $ErrorActionPreference = "Continue"; dotnet publish (Join-Path $sourceDir "src\$project") -c Release -o $new --nologo -v q --disable-build-servers 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path $new) { Remove-Item -Recurse -Force $new }
        $errors = @($output | Where-Object { $_ -match ": error " } | Select-Object -Unique -First 3)
        if ($errors.Count -eq 0) { $errors = @($output | Where-Object { $_ -and $_ -notmatch "RemoteException" }) }
        throw "the build failed`n$($errors -join "`n")"
    }
    Set-Content -Encoding ASCII (Join-Path $new "build-commit.txt") $commit
}

# The gateway and the app talk to each other, so they move together: both are built first, and only when both
# built are they swapped in. A failed build leaves both at the previous version, which the launcher then reopens.
function Repair-Builds {
    $builds = @(
        @{ Thing = "gateway"; Project = "MyOwnLLM.Gateway"; Folder = (Join-Path $installDir "gateway"); Exe = "MyOwnLLM.Gateway.exe" },
        @{ Thing = "app"; Project = "MyOwnLLM.Cli"; Folder = (Join-Path $installDir "app"); Exe = "jazari.exe" }
    )
    # Both are rebuilt when either is stale: a gateway from one commit and an app from another may not understand each other.
    if (@($builds | Where-Object { -not (Test-Build $_.Folder $_.Exe) }).Count -eq 0) { foreach ($b in $builds) { Say-Ok $b.Thing }; return }
    try { foreach ($b in $builds) { New-Build $b.Thing $b.Project $b.Folder } }
    catch {
        foreach ($b in $builds) { if (Test-Path "$($b.Folder).new") { Remove-Item -Recurse -Force "$($b.Folder).new" } }
        throw
    }
    foreach ($b in $builds) { Swap-Folder "$($b.Folder).new" $b.Folder; Say-Repaired $b.Thing }
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
    # 2. other Jazari windows hold files that get replaced. Launchers aren't in app\ and keep waiting.
    $ownFiles = { $_.Path -and $_.Path.StartsWith("$installDir\", [StringComparison]::OrdinalIgnoreCase) }
    $open = @(Get-Process "jazari" -ErrorAction SilentlyContinue | Where-Object $ownFiles | Where-Object { $_.Path -like "*\app\jazari.exe" })
    if ($open.Count -gt 0) {
        Write-Host "Jazari is open in $($open.Count) other window(s):"
        $open | ForEach-Object { Write-Host "  process $($_.Id), started $($_.StartTime)" }
        if ((Read-Host "Close them and continue? [y/n]") -ne "y") { exit 2 }
    }

    # 3. the source copy; nobody edits it, so it can simply be made to match main. Fetch first: offline, nothing gets closed.
    try {
        Invoke-Git remote set-url origin $config.repoUrl | Out-Null
        Invoke-Git fetch origin main | Out-Null
    }
    catch {
        Write-Host "ERROR source: could not reach GitHub ($_)"
        exit 1
    }

    # Only this install's servers: a llama-server from anywhere else locks nothing here and isn't ours to stop.
    $running = $open + @(Get-Process "llama-server", "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue | Where-Object $ownFiles)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue # stopped processes hold file locks for a moment
    }

    try {
        Invoke-Git reset --hard origin/main | Out-Null
        Invoke-Git clean -fd | Out-Null
    }
    catch {
        Write-Host "ERROR source: could not update Jazari's copy ($_)"
        exit 1
    }
    Say-Ok "source"

    # The synced updater runs the rest, so changes to it apply in this same update; but only if it parses. A broken
    # one on main would otherwise break every later `jazari update` before it could fetch the fix. The launcher runs
    # the copy kept in the install folder, which is replaced only by an updater that parses.
    $synced = Join-Path $sourceDir "scripts\update-jazari.ps1"
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($synced, [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count -eq 0) {
        try { Copy-Item -Force $synced (Join-Path $installDir "update-jazari.ps1") }
        catch { Write-Host "Couldn't keep a copy of the updater ($_); the next update uses the one in the source copy." }
        & $synced -Reexec
        exit $LASTEXITCODE
    }
    Write-Host "ERROR updater: the new update-jazari.ps1 on main doesn't parse ($($parseErrors[0].Message)); this update carries on with the previous one, and the next jazari update tries again."
    $script:failed = $true
}

$manifest = Get-Content -Raw (Join-Path $sourceDir "scripts\jazari-manifest.json") | ConvertFrom-Json
$commit = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()

# 4-6. each component on its own: one that fails keeps its previous version and the others still run
try { Repair-Model } catch { Say-Error "model" $_ }
try { Repair-Llama } catch { Say-Error "llama.cpp" $_ }
# Jazari answers without the law book, so a failed download only warns: it's no reason to call the whole update failed.
try { Repair-LawBook } catch { Write-Host "! law book not installed ($_); Jazari answers without it, and the next update tries again" }
try { Repair-DeepModel } catch { Write-Host "! smart model not installed ($_); /deep stays off, and the next update tries again" }
try { Repair-Builds } catch { Say-Error "gateway and app" $_ }

# 7. config: current paths, and installedCommit only when everything above worked
$paths = @{
    gatewayExe  = Join-Path $installDir "gateway\MyOwnLLM.Gateway.exe"
    llamaServer = Join-Path $installDir "llama\llama-server.exe"
    model       = Join-Path $installDir "models\$($manifest.modelFile)"
}
# A component that couldn't be put in place (say, a new model whose download failed) keeps the path that still works.
foreach ($key in @($paths.Keys)) {
    if ($config.$key -and -not (Test-Path $paths[$key])) { $paths[$key] = $config.$key }
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
        deepModel       = if ($manifest.deepModelFile) { Join-Path $installDir "models\$($manifest.deepModelFile)" } else { $null }
    } | ConvertTo-Json | Set-Content -Encoding UTF8 "$configPath.new"
    Move-Item -Force "$configPath.new" $configPath
    if ($stale) { Say-Repaired "config" } else { Say-Ok "config" }
}
catch { Say-Error "config" $_ }

# 8.
if ($script:failed) { exit 1 }
Write-Host "Jazari is up to date ($($commit.Substring(0, 7)))"

# 9. measuring takes the whole PC for minutes, so it's the user's call, and only once the new files are in place
if (-not [Console]::IsInputRedirected) {
    if ((Read-Host "Tune Jazari for this PC now? It measures for a few minutes. [y/n]") -eq "y") {
        & (Join-Path $installDir "app\jazari.exe") tune
    }
    else { Write-Host "Skipped. Run jazari tune whenever you like." }
}
exit 0
