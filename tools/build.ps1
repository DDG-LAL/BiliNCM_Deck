param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\BiliNCM-Deck\BiliNCM-Deck.csproj'

$dotnet = $null
if (Test-Path (Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe')) {
    $dotnet = Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'
} else {
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}

if (-not $dotnet) {
    throw '未找到 .NET SDK。请安装 .NET 10 SDK，或放到 %USERPROFILE%\.dotnet10。'
}

& $dotnet build $project -c $Configuration
exit $LASTEXITCODE
