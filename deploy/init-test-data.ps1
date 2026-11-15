[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Deliberately fixed to the current LOCAL stack; no arbitrary production target.
$postgres = 'deploy-postgres-1'
$sqlFile = Join-Path $PSScriptRoot 'init-test-data.sql'
& docker cp (Join-Path $PSScriptRoot 'repair-test-data.sql') "${postgres}:/tmp/repair-test-data.sql"
if ($LASTEXITCODE -ne 0) { throw 'Cannot copy local fixture repair.' }
& docker exec $postgres psql -U asset_inventory -d asset_inventory --set=ON_ERROR_STOP=1 --file=/tmp/repair-test-data.sql
if ($LASTEXITCODE -ne 0) { throw 'Fixture repair failed; transaction rolled back.' }
& docker cp $sqlFile "${postgres}:/tmp/init-test-data.sql"
if ($LASTEXITCODE -ne 0) { throw 'Cannot copy fixture to the local PostgreSQL container.' }
& docker exec $postgres psql -U asset_inventory -d asset_inventory --set=ON_ERROR_STOP=1 --file=/tmp/init-test-data.sql
if ($LASTEXITCODE -ne 0) { throw 'Fixture transaction failed; changes were rolled back.' }
# Direct SQL bypasses application cache invalidation. Let all catalog/dashboard
# entries expire before returning; do not clear Redis or disturb other keys.
Start-Sleep -Seconds 31
Write-Output 'Current local DB seeded. Worker logins: mvp.worker1..3; LOCAL TEST password: Manual-MVP-2026! Existing administrator credentials are unchanged.'
