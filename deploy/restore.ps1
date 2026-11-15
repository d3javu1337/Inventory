[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupPath, [Parameter(Mandatory)][string]$ProjectName,
    [switch]$ConfirmReplaceDatabase)
$ErrorActionPreference = 'Stop'
if (!$ConfirmReplaceDatabase) { throw 'Restore replaces the selected database. Pass -ConfirmReplaceDatabase after making a current backup.' }
. (Join-Path $PSScriptRoot 'backup-layout.ps1')
$composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'

function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $($Arguments[0])" }
    return $result
}

function Invoke-DatabaseSql([string]$Postgres, [string]$Sql) {
    $result = $Sql | docker exec -i $Postgres sh -c 'psql -XAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL restore step failed.' }
    return $result
}

function Get-SecurityVersion([string]$Postgres) {
    $value = Invoke-DatabaseSql $Postgres @'
SELECT COALESCE(MAX(COALESCE(to_jsonb(u)->>'security_version', to_jsonb(u)->>'SecurityVersion')::integer), 0)
FROM user_accounts AS u;
'@
    $version = 0
    if (![int]::TryParse($value, [ref]$version) -or $version -lt 0) { throw 'Invalid session version in target database.' }
    return $version
}

function Revoke-RestoredSessions([string]$Postgres, [int]$PreviousVersion) {
    $sql = @'
DO $restore$
DECLARE
    version_floor integer := __PREVIOUS_VERSION__;
    version_column text;
    revoked_column text;
BEGIN
    SELECT column_name INTO version_column FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'user_accounts'
        AND column_name IN ('security_version', 'SecurityVersion');
    IF version_column IS NOT NULL THEN
        EXECUTE format('SELECT GREATEST($1, COALESCE(MAX(%I), 0)) + 1 FROM user_accounts', version_column)
            INTO version_floor USING version_floor;
        EXECUTE format('UPDATE user_accounts SET %I = $1', version_column) USING version_floor;
    ELSE
        CREATE TABLE asset_inventory_restore_sessions (previous_version integer NOT NULL);
        INSERT INTO asset_inventory_restore_sessions VALUES (version_floor);
    END IF;
    SELECT column_name INTO STRICT revoked_column FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'refresh_tokens'
        AND column_name IN ('revoked_at', 'RevokedAt');
    EXECUTE format('UPDATE refresh_tokens SET %1$I = now() WHERE %1$I IS NULL', revoked_column);
END
$restore$;
'@
    Invoke-DatabaseSql $Postgres ($sql.Replace('__PREVIOUS_VERSION__', $PreviousVersion.ToString([Globalization.CultureInfo]::InvariantCulture))) | Out-Null
}

function Restore-DatabaseDump([string]$Postgres, [string]$RemoteDump) {
    # pg_restore --clean drops objects by the names in the dump. Remove the
    # application foreign keys by their current names in the same transaction.
    $prepare = @'
DO $restore$
DECLARE
    entry record;
    app_tables text[] := ARRAY[
        'asset_assignment_history', 'assets', 'audit_logs', 'commission_members',
        'departments', 'employees', 'generated_documents', 'integration_events',
        'inventories', 'inventory_asset_snapshots', 'inventory_assignments', 'inventory_schedules', 'locations',
        'organizations', 'refresh_tokens', 'unexpected_scans', 'user_accounts'
    ];
BEGIN
    FOR entry IN
        SELECT t.relname, c.conname
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_class referenced ON referenced.oid = c.confrelid
        WHERE c.contype = 'f'
            AND t.relnamespace = 'public'::regnamespace AND t.relname = ANY(app_tables)
            AND referenced.relnamespace = 'public'::regnamespace AND referenced.relname = ANY(app_tables)
    LOOP
        EXECUTE format('ALTER TABLE public.%I DROP CONSTRAINT %I', entry.relname, entry.conname);
    END LOOP;
END
$restore$;
'@
    $prepare | docker exec -i $Postgres sh -ec '
        sql="$1.sql"
        trap ''rm -f "$sql"'' EXIT
        pg_restore --clean --if-exists --no-owner --no-acl --exit-on-error --file="$sql" "$1"
        if grep -q "^CREATE TABLE public.inventory_assignments (" "$sql"; then
            obsolete=commission_members
        else
            obsolete=inventory_assignments
        fi
        psql -X -q -v ON_ERROR_STOP=1 --single-transaction -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f - -c "DROP TABLE IF EXISTS public.$obsolete" -f "$sql"
    ' restore $RemoteDump | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL restore failed.' }
}

function Restore-StagedBackup($layout, [string]$source) {
    $api = (Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'ps','-a','-q','api')).Trim()
    $postgres = (Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'ps','-q','postgres')).Trim()
    $redis = (Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'ps','-q','redis')).Trim()
    if (!$api -or !$postgres -or !$redis) { throw 'Target API container and running PostgreSQL and Redis are required.' }
    $image = (Invoke-Docker @('inspect','--format','{{.Image}}',$api)).Trim()
    $owner = (Invoke-Docker @('inspect','--format','{{.Config.User}}',$api)).Trim()
    if (!$owner) { $owner = '0' }
    $remoteDump = "/tmp/asset-restore-$([guid]::NewGuid().ToString('N')).dump"
    Invoke-Docker @('compose','-p',$ProjectName,'-f',$composeFile,'stop','api') | Out-Null
    try {
        $previousVersion = Get-SecurityVersion $postgres
        Invoke-Docker @('cp',(Join-Path $source 'database.dump'),"${postgres}:$remoteDump") | Out-Null
        Restore-DatabaseDump $postgres $remoteDump
        Revoke-RestoredSessions $postgres $previousVersion
        Invoke-Docker @('cp',(Join-Path $source 'documents/.'),"${api}:/app/storage") | Out-Null
        Invoke-Docker @('run','--rm','--user','0','--volumes-from',$api,'--entrypoint','chown',$image,'-R',$owner,'/app/storage') | Out-Null

        $checksums = @($layout.Hashes.GetEnumerator() | Where-Object { $_.Key.StartsWith('documents/', [StringComparison]::Ordinal) } | ForEach-Object {
            "$($_.Value.ToLowerInvariant())  ./$($_.Key.Substring('documents/'.Length))"
        })
        if ($checksums.Count -gt 0) {
            # Validate copied bytes using the API image's default user before starting it.
            $checksums | docker run --rm -i --volumes-from "${api}:ro" --entrypoint sh $image -ec 'cd /app/storage; tr -d "\r" | sha256sum -c --quiet -' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Restored report files do not match the verified backup.' }
        }

        $flushed = Invoke-Docker @('exec',$redis,'redis-cli','-e','FLUSHDB','SYNC')
        if ($flushed -ne 'OK') { throw 'Redis did not acknowledge clearing the restored project cache.' }
        # Preserve the existing container's ports/network/environment, including Compose overrides.
        Invoke-Docker @('start',$api) | Out-Null
        Write-Output 'Database and reports restored. Existing sessions revoked and project Redis cache cleared. Old unreferenced files are retained.'
    } finally {
        Invoke-Docker @('exec',$postgres,'rm','-f',$remoteDump) | Out-Null
        # If restore fails, API remains stopped; do not expose a partially restored state.
    }
}

$inputLayout = Get-BackupLayout $BackupPath
$stage = [IO.Directory]::CreateTempSubdirectory('asset-inventory-restore-').FullName
try {
    $null = [IO.Directory]::CreateDirectory((Join-Path $stage 'documents'))
    Copy-Item -LiteralPath $inputLayout.Manifest -Destination (Join-Path $stage 'manifest.json')
    foreach ($entry in $inputLayout.Paths.GetEnumerator()) {
        $destination = Join-Path $stage $entry.Key
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $entry.Value -Destination $destination
    }
    # Trial restore and real restore read the same staged bytes.
    & (Join-Path $PSScriptRoot 'verify-backup.ps1') -BackupPath $stage
    Restore-StagedBackup (Get-BackupLayout $stage) $stage
} finally {
    $directory = Get-Item -LiteralPath $stage -Force
    $temporaryRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())
    if ($directory.FullName -cne $stage -or $directory.Parent.FullName -ne $temporaryRoot -or
        !$directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected temporary restore directory.'
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}
