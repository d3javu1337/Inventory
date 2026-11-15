[CmdletBinding()]
param(
    [string]$ApiUrl = 'http://localhost:5147/api/v1',
    [Parameter(Mandatory)][string]$AdminPassword,
    [string]$AdminLogin = 'admin',
    [Parameter(Mandatory)][guid]$InventoryId,
    [string]$WorkerLogin = 'mvp.worker2',
    [string]$WorkerPassword = 'Manual-MVP-2026!'
)
$ErrorActionPreference = 'Stop'
if (-not ([uri]$ApiUrl).IsLoopback) { throw 'This read-only verification is restricted to the local MVP API.' }
$ApiUrl = $ApiUrl.TrimEnd('/')
$adminPair = $null
$workerPair = $null
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Pagination verification failed: $Message" }
    Write-Output "OK: $Message"
}
function Read-Api([string]$Path, $Pair) {
    Invoke-RestMethod -Uri "$ApiUrl$Path" -Headers @{ Authorization = "Bearer $($Pair.accessToken)" }
}
function Check-List([string]$Endpoint) {
    $first = Read-Api "/$Endpoint/paged?page=1&pageSize=20" $adminPair
    $ids = [System.Collections.Generic.HashSet[string]]::new()
    $pages = [Math]::Max(1, [Math]::Ceiling($first.totalCount / 20))
    for ($number = 1; $number -le $pages; $number++) {
        $response = if ($number -eq 1) { $first } else { Read-Api "/$Endpoint/paged?page=$number&pageSize=20" $adminPair }
        if ($response.items.Count -gt 20) { throw "Oversized page: $Endpoint" }
        foreach ($row in $response.items) { if (-not $ids.Add($row.id)) { throw "Duplicate across pages: $Endpoint" } }
    }
    Check ($ids.Count -eq $first.totalCount) "${Endpoint}: all $($ids.Count) rows accessible without overlap"
}
try {
    $adminPair = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType 'application/json' -Body (@{ login = $AdminLogin; password = $AdminPassword; device = 'read-only-pagination-verification' } | ConvertTo-Json -Compress)
    $first = Read-Api "/inventories/$InventoryId`?page=1&pageSize=20&filter=all" $adminPair
    Check ($null -ne $first.counts -and $first.items.Count -le 20) 'Inventory returns a bounded page with global counts'
    Check ($first.counts.total -eq $first.counts.found + $first.counts.pending + $first.counts.notFound) 'Total = found + pending + not found (QR excluded)'
    $ids = [System.Collections.Generic.HashSet[string]]::new()
    $pages = [Math]::Max(1, [Math]::Ceiling($first.itemsTotalCount / 20))
    for ($number = 1; $number -le $pages; $number++) {
        $response = if ($number -eq 1) { $first } else { Read-Api "/inventories/$InventoryId`?page=$number&pageSize=20&filter=all" $adminPair }
        if ($response.counts.total -ne $first.counts.total -or $response.counts.found -ne $first.counts.found) { throw 'Inventory changed during verification; rerun on a closed inventory.' }
        foreach ($row in $response.items) { if (-not $ids.Add($row.id)) { throw 'Duplicate inventory row across pages' } }
    }
    Check ($ids.Count -eq $first.counts.total) "All $($ids.Count) inventory rows accessible without overlap"
    $remaining = Read-Api "/inventories/$InventoryId`?page=1&pageSize=20&filter=remaining" $adminPair
    Check ($remaining.itemsTotalCount -eq $first.counts.total - $first.counts.found -and @($remaining.items | Where-Object result -eq 'Found').Count -eq 0) 'Found devices are excluded from the work list'
    $found = Read-Api "/inventories/$InventoryId`?page=1&pageSize=20&filter=found" $adminPair
    Check ($found.itemsTotalCount -eq $first.counts.found -and @($found.items | Where-Object result -ne 'Found').Count -eq 0) 'Found devices remain accessible in results'
    if ($first.items.Count) {
        $needle = [uri]::EscapeDataString($first.items[0].inventoryNumber)
        $searched = Read-Api "/inventories/$InventoryId`?page=1&pageSize=20&filter=all&search=$needle" $adminPair
        Check ($searched.items.id -contains $first.items[0].id -and $searched.counts.total -eq $first.counts.total) 'Search works without changing global counts'
        Check-List "assets/$($first.items[0].assetId)/history"
    }
    $qr = Read-Api "/inventories/$InventoryId`?page=1&pageSize=20&filter=unexpected" $adminPair
    Check ($qr.unexpectedTotalCount -eq $first.counts.unexpected -and $qr.unexpectedScans.Count -le 20) 'Unexpected QR count and pages are separate from devices'
    foreach ($endpoint in @('departments', 'locations', 'employees', 'users', 'inventory-schedules', 'inventory-events')) { Check-List $endpoint }
    if ($WorkerLogin) {
        $workerPair = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType 'application/json' -Body (@{ login = $WorkerLogin; password = $WorkerPassword; device = 'read-only-pagination-verification' } | ConvertTo-Json -Compress)
        $mobile = Read-Api "/mobile/inventories/$InventoryId`?page=1&pageSize=20&filter=remaining" $workerPair
        Check ($mobile.itemsTotalCount -eq $remaining.itemsTotalCount -and $mobile.counts.total -eq $first.counts.total) 'Assigned mobile user sees matching paginated data'
    }
    Write-Output 'Business data was not modified.'
} finally {
    foreach ($pair in @($adminPair, $workerPair)) {
        if ($pair) { Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/logout" -ContentType 'application/json' -Body (@{ refreshToken = $pair.refreshToken } | ConvertTo-Json -Compress) | Out-Null }
    }
}
