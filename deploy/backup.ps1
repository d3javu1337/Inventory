[CmdletBinding()]
param([string]$Destination, [string]$ProjectName = 'deploy')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backup-layout.ps1')
$composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $($Arguments[0])" }
    return $result
}
if (!$Destination) { $Destination = Join-Path $PSScriptRoot "../backups/$(Get-Date -Format yyyyMMdd-HHmmss)-$([guid]::NewGuid().ToString('N').Substring(0,8))" }
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Backup destination must be a new directory.' }
$api = (Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'ps','-q','api')).Trim()
$postgres = (Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'ps','-q','postgres')).Trim()
if (!$api -or !$postgres) { throw 'API and PostgreSQL must be running.' }
New-Item -ItemType Directory -Path (Join-Path $target 'documents') -Force | Out-Null
$remoteDump = "/tmp/asset-backup-$([guid]::NewGuid().ToString('N')).dump"
$paused = $false
try {
    # No application writes while the consistent DB snapshot and report files are copied.
    Invoke-Docker @('pause',$api) | Out-Null
    $paused = $true
    Invoke-Docker @('exec',$postgres,'sh','-c','pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom --file="$1"','backup',$remoteDump) | Out-Null
    Invoke-Docker @('cp',"${postgres}:$remoteDump",(Join-Path $target 'database.dump')) | Out-Null
    Invoke-Docker @('cp',"${api}:/app/storage/.",(Join-Path $target 'documents')) | Out-Null
    $files = @(Get-ChildItem -LiteralPath $target -File -Force -Recurse | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($target, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ version = 1; createdAt = [DateTimeOffset]::UtcNow.ToString('O'); project = $ProjectName; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
    $null = Get-BackupLayout $target
    Write-Output $target
} finally {
    if ($paused) { Invoke-Docker @('unpause',$api) | Out-Null }
    Invoke-Docker @('exec',$postgres,'rm','-f',$remoteDump) | Out-Null
}
