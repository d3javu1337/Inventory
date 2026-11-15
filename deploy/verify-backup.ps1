[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backup-layout.ps1')
$layout = Get-BackupLayout $BackupPath -VerifyHashes
function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $($Arguments[0])" }
    return $result
}
$container = 'asset-backup-verify-' + [guid]::NewGuid().ToString('N')
try {
    Invoke-Docker @('run','-d','--name',$container,'-e','POSTGRES_HOST_AUTH_METHOD=trust','postgres:17-alpine') | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        & docker exec $container pg_isready -U postgres 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Disposable PostgreSQL did not start.' }
    Invoke-Docker @('cp',$layout.Paths['database.dump'],"${container}:/tmp/database.dump") | Out-Null
    Invoke-Docker @('exec',$container,'pg_restore','-U','postgres','-d','postgres','--no-owner','--no-acl','--exit-on-error','/tmp/database.dump') | Out-Null
    $sql = @'
SELECT COALESCE(json_agg(json_build_object(
    'key', COALESCE(to_jsonb(d)->>'storage_key', to_jsonb(d)->>'StorageKey'),
    'hash', COALESCE(to_jsonb(d)->>'sha256', to_jsonb(d)->>'Sha256')
)), '[]'::json)::text FROM generated_documents AS d;
'@
    $json = Invoke-Docker @('exec',$container,'psql','-U','postgres','-d','postgres','-A','-t','-v','ON_ERROR_STOP=1','-c',$sql)
    $reports = $json | ConvertFrom-Json
    foreach ($report in $reports) {
        $key = "documents/" + $report.key.Replace('\','/')
        if (!$layout.Hashes.ContainsKey($key) -or $layout.Hashes[$key] -ne $report.hash) {
            throw "Report metadata does not match backed-up file: $($report.key)"
        }
    }
    Invoke-Docker @('exec',$container,'psql','-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c','SELECT count(*) AS inventories FROM inventories; SELECT count(*) AS documents FROM generated_documents;')
    Write-Output 'Restore succeeded in disposable PostgreSQL; all backup file hashes are valid.'
} finally {
    # Only this randomly named verification container and its anonymous volume are removed.
    & docker rm -f -v $container | Out-Null
}
