[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Get-Command docker, node -ErrorAction Stop | Out-Null
& docker info --format '{{.ServerVersion}}'
if ($LASTEXITCODE -ne 0) { throw 'Start Docker Desktop before running this script.' }
$composeArguments = @('compose', '-f', "$PSScriptRoot/docker-compose.yml", 'up', '-d')
if (-not $SkipBuild) { $composeArguments += '--build' }
else { $composeArguments += '--no-build' }
& docker @composeArguments
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose startup failed.' }
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    try { $ready = (Invoke-WebRequest 'http://localhost:5147/health/ready' -TimeoutSec 3).StatusCode -eq 200 } catch { $ready = $false }
    if (-not $ready) { Start-Sleep -Seconds 1 }
} until ($ready -or [DateTime]::UtcNow -gt $deadline)
if (-not $ready) { throw 'API readiness did not succeed within 120 seconds.' }
$mobileRoot = Join-Path $projectRoot 'mobile'
$vite = Join-Path $mobileRoot 'node_modules/vite/bin/vite.js'
if (-not (Test-Path -LiteralPath $vite)) { throw 'Install mobile dependencies first: cd mobile; npm ci' }
$listeners = @(Get-NetTCPConnection -LocalPort 5174 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -eq 0) {
    $logDirectory = Join-Path $projectRoot 'tmp/local-mvp'
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $nodePath = (Get-Command node).Source
    Start-Process -FilePath $nodePath -ArgumentList @("`"$vite`"", '--config', 'vite.config.ts', '--host', '127.0.0.1', '--port', '5174', '--strictPort') -WorkingDirectory $mobileRoot -WindowStyle Hidden -RedirectStandardOutput "$logDirectory/mobile.stdout.log" -RedirectStandardError "$logDirectory/mobile.stderr.log" | Out-Null
}
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    try {
        $mobile = Invoke-WebRequest 'http://127.0.0.1:5174/' -TimeoutSec 3
        $ready = $mobile.StatusCode -eq 200 -and $mobile.Content -match 'Asset Inventory'
    } catch { $ready = $false }
    if (-not $ready) { Start-Sleep -Seconds 1 }
} until ($ready -or [DateTime]::UtcNow -gt $deadline)
if (-not $ready) { throw 'Mobile web is unavailable or port 5174 belongs to another application. Existing processes were not stopped.' }
if ((Invoke-WebRequest 'http://localhost:5173/' -TimeoutSec 5).StatusCode -ne 200) { throw 'Admin web is unavailable.' }
Write-Output 'Ready: API http://localhost:5147 | admin http://localhost:5173 | mobile web http://127.0.0.1:5174'
Write-Output 'Existing database and report volumes preserved. No seed, credentials reset, or Android launch performed.'
