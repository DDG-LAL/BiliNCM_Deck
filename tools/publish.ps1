param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\BiliNCM-Deck\BiliNCM-Deck.csproj'
$out = Join-Path $root 'publish'

$dotnet = $null
if (Test-Path (Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe')) {
    $dotnet = Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'
} else {
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) { throw '未找到 .NET SDK。' }

if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }

& $dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# web assets are content, not embedded in the single file
$webSrc = Join-Path $root 'src\BiliNCM-Deck\web'
$webDst = Join-Path $out 'web'
New-Item -ItemType Directory -Path $webDst -Force | Out-Null
Copy-Item -Path (Join-Path $webSrc '*') -Destination $webDst -Recurse -Force
$appIcon = Join-Path $root 'src\BiliNCM-Deck\Assets\BiliNCM-Deck.ico'
if (Test-Path $appIcon) { Copy-Item -LiteralPath $appIcon -Destination (Join-Path $out 'BiliNCM-Deck.ico') -Force }
foreach ($name in @('README.md', 'LICENSE', 'NOTICE')) {
    $src = Join-Path $root $name
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $out $name) -Force }
}
$packaging = Join-Path $root 'packaging'
if (Test-Path $packaging) {
    Copy-Item -Path (Join-Path $packaging '*') -Destination $out -Force
}
$ncmPath = Join-Path $root 'ncm-path.txt'
if (Test-Path $ncmPath) {
    Copy-Item -LiteralPath $ncmPath -Destination (Join-Path $out 'ncm-path.txt') -Force
}
Write-Host "Published to $out"
