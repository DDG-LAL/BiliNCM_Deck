param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root "src\BiliNCM-Deck\bin\$Configuration\net10.0-windows\BiliNCM-Deck.exe"
if (-not (Test-Path $exe)) { throw "找不到 $exe" }

$sdkRoot = Join-Path $env:USERPROFILE '.dotnet10'
if (Test-Path $sdkRoot) {
    $env:DOTNET_ROOT = $sdkRoot
    $env:PATH = $sdkRoot + ';' + $env:PATH
}
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
