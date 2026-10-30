# Builds the jazari CLI and installs (or updates) it as a .NET global tool, so `jazari` works in any terminal.
$root = Resolve-Path "$PSScriptRoot\.."
$artifacts = Join-Path $root "artifacts"

dotnet pack "$root\src\MyOwnLLM.Cli" -c Release -o $artifacts
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet tool update --global Jazari --add-source $artifacts
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installed. Open a new terminal and run: jazari"
