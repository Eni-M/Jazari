# Takes Jazari off this machine. Run it from your MyOwnLLM clone, with every jazari window closed:
#
#   .\scripts\uninstall-jazari.ps1
#
# It removes what install-jazari.ps1 put there: the install folder's own files, the jazari command (and its PATH entry
# for a custom folder) and %APPDATA%\Jazari. It asks before deleting the model, a long download to get back, and before
# deleting your data in %LOCALAPPDATA%\MyOwnLLM (reviews, logs, keys, the law book and its search models). Your clone is never touched.
# Not `jazari uninstall`: the command can't remove itself while it runs.

$ErrorActionPreference = "Stop"
$configPath = Join-Path $env:APPDATA "Jazari\config.json"
$dataDir = Join-Path $env:LOCALAPPDATA "MyOwnLLM"

# What install-jazari.ps1 and update-jazari.ps1 create in the install folder, including what an interrupted update
# leaves. Only these are deleted: the folder is whatever the config names, so it's never removed wholesale.
$ownItems = "app", "gateway", "llama", "source", "bin", "update-jazari.ps1",
    "app.new", "app.old", "gateway.new", "gateway.old", "llama.new", "llama.old", "llama.download.zip"

function Ask($question) {
    if ([Console]::IsInputRedirected) { return $false } # nobody to ask: keep it
    (Read-Host "$question [y/n]") -eq "y"
}

if (Get-Process "jazari" -ErrorAction SilentlyContinue) {
    Write-Host "Close every open jazari session first (a running jazari locks its own files), then run this again."
    exit 1
}

$config = $null
if (Test-Path $configPath) {
    try { $config = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json }
    catch { Write-Host "Ignoring the unreadable $configPath" }
}
$installDir = if ($config.installDir) { $config.installDir } else { Join-Path $env:LOCALAPPDATA "Jazari" }
$customDir = $installDir -ne (Join-Path $env:LOCALAPPDATA "Jazari")

# Only this install's servers: a llama-server from anywhere else isn't ours to stop.
$servers = @(Get-Process "llama-server", "MyOwnLLM.Gateway" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith("$installDir\", [StringComparison]::OrdinalIgnoreCase) })
if ($servers.Count -gt 0) {
    Write-Host "Stopping the running servers"
    $servers | Stop-Process -Force
    $servers | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue # stopped processes hold file locks for a moment
}

# The jazari command first: without it, a half-finished uninstall can't be mistaken for a working install.
& { $ErrorActionPreference = "Continue"; dotnet tool uninstall --global Jazari *> $null } # fine if it wasn't installed
if ($customDir) {
    # Read and written as stored, like the installer: other entries keep their %VARIABLES% and the value stays REG_EXPAND_SZ.
    $binDir = Join-Path $installDir "bin"
    $envKey = Get-Item "HKCU:\Environment"
    $userPath = [string]$envKey.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $kept = @($userPath -split ";" | Where-Object { $_ -and $_ -ne $binDir })
    if ($kept.Count -ne @($userPath -split ";" | Where-Object { $_ }).Count) {
        Set-ItemProperty -Path "HKCU:\Environment" -Name "Path" -Value ($kept -join ";") -Type ExpandString
        [Environment]::SetEnvironmentVariable("JAZARI_PATH_REFRESH", "1", "User") # tells open programs, as setx does
        [Environment]::SetEnvironmentVariable("JAZARI_PATH_REFRESH", $null, "User")
        Write-Host "Removed $binDir from your PATH"
    }
}

if (Test-Path $installDir) {
    foreach ($item in $ownItems) {
        $path = Join-Path $installDir $item
        if (Test-Path $path) { Remove-Item -Recurse -Force $path }
    }
    $models = Join-Path $installDir "models"
    if (Test-Path $models) {
        $gb = [math]::Round((Get-ChildItem -Recurse -File $models | Measure-Object Length -Sum).Sum / 1GB, 1)
        if (Ask "Delete the model too ($gb GB in $models)? Keeping it saves the download if you install again.") { Remove-Item -Recurse -Force $models }
        else { Write-Host "Kept the model in $models" }
    }
    # Empty now unless the model was kept or something of the user's own is in there.
    if (-not (Get-ChildItem -Force $installDir)) { Remove-Item -Force $installDir }
}

if (Test-Path (Split-Path $configPath)) { Remove-Item -Recurse -Force (Split-Path $configPath) }

if (Test-Path $dataDir) {
    if (Ask "Delete your Jazari data too (reviews, logs, keys, the law book and its search models in $dataDir)?") { Remove-Item -Recurse -Force $dataDir }
    else { Write-Host "Kept your data in $dataDir" }
}

Write-Host ""
Write-Host "Jazari is uninstalled. Your MyOwnLLM clone is untouched; scripts\install-jazari.ps1 puts Jazari back."
