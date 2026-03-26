@echo off
cd /d C:\Users\Hakan\Documents\TamirBakimTalepApp
start "TamirBakimTalepApp" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Hakan\Documents\TamirBakimTalepApp\start-app.ps1"
timeout /t 3 >nul
start "" "http://localhost:5079/login"
