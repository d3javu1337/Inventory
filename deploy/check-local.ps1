[CmdletBinding()]
param([switch]$HealthOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
function Invoke-Check([string]$Directory, [string]$Command, [string[]]$Arguments) {
    Push-Location $Directory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Verification failed: $Command $($Arguments -join ' ')" }
    } finally { Pop-Location }
}
foreach ($url in @('http://localhost:5147/health/ready', 'http://localhost:5173/', 'http://127.0.0.1:5174/')) {
    if ((Invoke-WebRequest $url -TimeoutSec 5).StatusCode -ne 200) { throw "Unavailable: $url" }
    Write-Output "OK: $url"
}
if ($HealthOnly) { return }
Invoke-Check "$projectRoot/backend" 'dotnet' @('test', 'AssetInventory.sln', '--no-restore')
Invoke-Check "$projectRoot/web" 'npm' @('test')
Invoke-Check "$projectRoot/web" 'npm' @('run', 'build')
Invoke-Check "$projectRoot/web" 'npm' @('run', 'typecheck:live')
Invoke-Check "$projectRoot/web" 'npm' @('run', 'test:e2e')
Invoke-Check "$projectRoot/web" 'npm' @('run', 'test:e2e:mobile-reports')
Invoke-Check "$projectRoot/mobile" 'npm' @('test', '--', '--runInBand')
Invoke-Check "$projectRoot/mobile" 'npm' @('run', 'typecheck')
Invoke-Check "$projectRoot/mobile" 'npm' @('run', 'lint')
Invoke-Check "$projectRoot/mobile" 'npm' @('run', 'build:web')
Write-Output 'All local checks passed. Real business data were not changed. Native Android was not launched.'
