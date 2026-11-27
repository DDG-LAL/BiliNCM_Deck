param(
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root 'src\BiliNCM-Deck\bin\Debug\net10.0-windows\BiliNCM-Deck.exe'
}
if (-not (Test-Path $Exe)) { throw "找不到 $Exe，请先 build" }

$sdkRoot = Join-Path $env:USERPROFILE '.dotnet10'
if (Test-Path $sdkRoot) {
    $env:DOTNET_ROOT = $sdkRoot
    $env:PATH = $sdkRoot + ';' + $env:PATH
}

$proc = Start-Process -FilePath $Exe -ArgumentList '--no-browser' -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(25)
    $ok = $false
    do {
        try {
            $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5566/health' -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $ok = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    if (-not $ok) { throw '5566 /health 未就绪' }

    '--- 5566 health ---'
    (Invoke-WebRequest -Uri 'http://127.0.0.1:5566/health' -UseBasicParsing -TimeoutSec 5).Content
    '--- 5566 settings ---'
    $s = Invoke-WebRequest -Uri 'http://127.0.0.1:5566/settings' -UseBasicParsing -TimeoutSec 5
    'status=' + $s.StatusCode + ' bytes=' + $s.RawContentLength
    '--- 5566 status ---'
    (Invoke-WebRequest -Uri 'http://127.0.0.1:5566/state' -UseBasicParsing -TimeoutSec 5).Content
    '--- 5566 NCM bridge ---'
    try {
        $ncm = (Invoke-WebRequest -Uri 'http://127.0.0.1:5566/api/ncm/state' -UseBasicParsing -TimeoutSec 15).Content | ConvertFrom-Json
        'ncm_ok={0} current={1} queueLen={2}' -f $ncm.ok, $ncm.current.id, $ncm.queueLen
    } catch {
        'ncm_unavailable (网易云未运行或没有开启 CDP 9222)'
    }
    '--- 5566 API config (safe fields only) ---'
    $cfg = (Invoke-WebRequest -Uri 'http://127.0.0.1:5566/api/config' -UseBasicParsing -TimeoutSec 5).Content | ConvertFrom-Json
    'biliLogin={0} roomId={1} uid={2}' -f $cfg.biliLogin, $cfg.roomId, $cfg.uid
    '--- 5566 memory logs ---'
    $logs = (Invoke-WebRequest -Uri 'http://127.0.0.1:5566/api/logs' -UseBasicParsing -TimeoutSec 5).Content | ConvertFrom-Json
    'entries=' + @($logs).Count
    '--- persistent deck.log ---'
    $exeDir = Split-Path -Parent (Resolve-Path $Exe)
    $logPath = Join-Path $exeDir 'deck.log'
    if (-not (Test-Path -LiteralPath $logPath)) { throw "未生成 deck.log: $logPath" }
    'path={0} bytes={1}' -f $logPath, (Get-Item -LiteralPath $logPath).Length
    '--- 5555 should be closed ---'
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:5555/' -UseBasicParsing -TimeoutSec 2 | Out-Null
        throw '5555 仍在监听'
    } catch {
        if ($_.Exception.Message -eq '5555 仍在监听') { throw }
        '5555 closed'
    }
} finally {
    try { Invoke-WebRequest -Uri 'http://127.0.0.1:5566/api/exit' -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch { }
    Start-Sleep -Milliseconds 800
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
