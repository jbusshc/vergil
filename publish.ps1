# Genera dist\VergilHud.exe: un único ejecutable autocontenido (no necesita .NET instalado).
# Uso:  .\publish.ps1
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:PATH = "C:\Program Files\dotnet;" + $env:PATH
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish (Join-Path $root "src\VergilHud.App\VergilHud.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló (código $LASTEXITCODE)" }

$exe = Join-Path $dist "VergilHud.exe"
if (-not (Test-Path $exe)) { throw "No se generó $exe" }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "OK: $exe ($mb MB)"
