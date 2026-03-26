$publishDir = 'C:\Users\Hakan\Documents\TamirBakimTalepYayin_v7'
$sourceDataDir = Join-Path $PSScriptRoot 'Data'
$publishDataDir = Join-Path $publishDir 'Data'
$logPath = Join-Path $PSScriptRoot 'start-app.log'
$url = 'http://localhost:5079/login'

"[$(Get-Date -Format s)] Starting published app" | Set-Content -Encoding UTF8 $logPath

if (Test-Path $sourceDataDir) {
    New-Item -ItemType Directory -Force -Path $publishDataDir | Out-Null
    Copy-Item (Join-Path $sourceDataDir '*') $publishDataDir -Recurse -Force -ErrorAction SilentlyContinue
}

$listener = Get-NetTCPConnection -LocalPort 5079 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    foreach ($item in $listener) {
        try {
            Stop-Process -Id $item.OwningProcess -Force -ErrorAction Stop
            "[$(Get-Date -Format s)] Stopped old PID $($item.OwningProcess)" | Add-Content -Encoding UTF8 $logPath
        }
        catch {
            "[$(Get-Date -Format s)] Could not stop PID $($item.OwningProcess): $($_.Exception.Message)" | Add-Content -Encoding UTF8 $logPath
        }
    }
    Start-Sleep -Seconds 2
}

Set-Location $publishDir
$env:ASPNETCORE_URLS = 'http://localhost:5079'

try {
    Start-Process "http://localhost:5079/login" | Out-Null
}
catch {
    "[$(Get-Date -Format s)] Browser open skipped: $($_.Exception.Message)" | Add-Content -Encoding UTF8 $logPath
}

dotnet .\TamirBakimTalepApp.dll *>> $logPath
