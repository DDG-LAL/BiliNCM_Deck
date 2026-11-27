@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri 'http://127.0.0.1:5566/api/exit' -Method POST -UseBasicParsing -TimeoutSec 3 | Out-Null } catch { }; Start-Sleep -Milliseconds 800; Get-Process -Name 'BiliNCM-Deck' -ErrorAction SilentlyContinue | Stop-Process -Force"
exit /b 0
