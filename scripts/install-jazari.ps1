# Builds the jazari CLI and installs (or updates) it as a .NET global tool, so `jazari` works in any terminal.
# Each run gets a unique package version, so `dotnet tool update` always picks up the new build
# (the version Jazari shows on its welcome screen stays the one in the project file).
$root = Resolve-Path "$PSScriptRoot\.."
$artifacts = Join-Path $root "artifacts"
$package = "0.2.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"   # always higher than the last install

dotnet pack "$root\src\MyOwnLLM.Cli" -c Release -o $artifacts -p:PackageVersion=$package
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet tool update --global Jazari --add-source $artifacts --version $package
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installed $package. Open a new terminal and run: jazari"
