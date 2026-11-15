[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$InventoryId,
    [Parameter(Mandatory)][string]$AdminPassword,
    [string]$ApiUrl = 'http://localhost:5147/api/v1',
    [string]$WorkerLogin = 'mvp.worker2',
    [string]$WorkerPassword = 'Manual-MVP-2026!'
)
$ErrorActionPreference = 'Stop'
if (-not ([uri]$ApiUrl).IsLoopback) { throw 'Only the local MVP API is permitted.' }
$ApiUrl = $ApiUrl.TrimEnd('/')
$adminPair = $null
$workerPair = $null
function Read-Worker([string]$Filter, [string]$Search = '') {
    Invoke-RestMethod -Uri "$ApiUrl/mobile/inventories/$InventoryId`?page=1&pageSize=20&filter=$Filter&search=$Search" -Headers @{Authorization = "Bearer $($workerPair.accessToken)"}
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Scan verification failed: $Message" }
    Write-Output "OK: $Message"
}
try {
    $adminPair = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType 'application/json' -Body (@{login='admin';password=$AdminPassword;device='local-scan-verification'} | ConvertTo-Json)
    $workerPair = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType 'application/json' -Body (@{login=$WorkerLogin;password=$WorkerPassword;device='local-scan-verification'} | ConvertTo-Json)
    $detail = Read-Worker 'all'
    # Mutations are restricted to a clone of our existing synthetic inventory.
    Check ($detail.sourceInventoryNumber -eq 'INV-MVP-002') 'Target is a synthetic test clone, not the original inventory'
    if ($detail.status -eq 'Draft') {
        Invoke-RestMethod -Method Post -Uri "$ApiUrl/inventories/$InventoryId/start" -Headers @{Authorization="Bearer $($adminPair.accessToken)"} | Out-Null
        $detail = Read-Worker 'all'
    }
    Check ($detail.status -eq 'Active') 'Test clone is active'
    $before = Read-Worker 'remaining' 'MVP-0001'
    $target = @($before.items | Where-Object inventoryNumber -eq 'MVP-0001')
    Check ($target.Count -eq 1 -and $target[0].result -eq 'Pending') 'Synthetic MVP-0001 is pending in work list'
    $body = @{qrValue=$target[0].assetId;condition='Damaged';comment='API regression: test casing damage'} | ConvertTo-Json
    $scan = Invoke-RestMethod -Method Post -Uri "$ApiUrl/mobile/inventories/$InventoryId/scan" -Headers @{Authorization="Bearer $($workerPair.accessToken)"} -ContentType 'application/json' -Body $body
    Check ($scan.result -eq 'Found' -and -not $scan.duplicate) 'Scan is saved as Found'
    $after = Read-Worker 'remaining' 'MVP-0001'
    Check ($after.itemsTotalCount -eq 0 -and $after.items.Count -eq 0) 'Saved device disappears from remaining search'
    Check ($after.counts.found -eq $before.counts.found + 1 -and $after.counts.pending -eq $before.counts.pending - 1 -and $after.counts.total -eq $before.counts.total) 'Global counts change by exactly one'
    $found = Read-Worker 'found' 'MVP-0001'
    Check ($found.items.Count -eq 1 -and $found.items[0].condition -eq 'Damaged' -and $found.items[0].comment -eq 'API regression: test casing damage') 'Found result retains condition and comment'
    $repeat = Invoke-RestMethod -Method Post -Uri "$ApiUrl/mobile/inventories/$InventoryId/scan" -Headers @{Authorization="Bearer $($workerPair.accessToken)"} -ContentType 'application/json' -Body (@{qrValue=$target[0].assetId;condition='Serviceable';comment=$null}|ConvertTo-Json)
    $unchanged = Read-Worker 'found' 'MVP-0001'
    Check ($repeat.duplicate -and $unchanged.counts.found -eq $found.counts.found -and $unchanged.items[0].comment -eq $found.items[0].comment -and $unchanged.items[0].condition -eq 'Damaged') 'Duplicate scan does not overwrite result or increment counts'
    Write-Output "Test clone $InventoryId remains Active. Original completed inventory was not modified. This is HTTP verification, not a manual UI scan."
} finally {
    foreach ($pair in @($adminPair,$workerPair)) {
        if ($pair) { Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/logout" -ContentType 'application/json' -Body (@{refreshToken=$pair.refreshToken}|ConvertTo-Json) | Out-Null }
    }
}
