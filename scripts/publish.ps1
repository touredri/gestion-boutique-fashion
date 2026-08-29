$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\win-x64"
dotnet restore (Join-Path $root "BoutiqueFashion.slnx")
dotnet test (Join-Path $root "BoutiqueFashion.slnx") -c Release --no-restore
dotnet publish (Join-Path $root "src\BoutiqueFashion.App\BoutiqueFashion.App.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
Write-Host "Application publiée dans $output"

