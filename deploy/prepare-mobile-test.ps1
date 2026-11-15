[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdminPassword)
$ErrorActionPreference = 'Stop'
$api = 'http://localhost:5147/api/v1'
$sourceId = '01a10624-3a80-7ce2-82f6-2b643bc0c9cc'
$adminPair = $null
$workerPair = $null
function Send-Json([string]$Path, $Body, $Headers = @{}, [string]$Method = 'Post') {
    Invoke-RestMethod -Method $Method -Uri "$api/$Path" -Headers $Headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json)
}
try {
    $adminPair = Send-Json 'auth/mobile/login' @{login='admin';password=$AdminPassword;device='local-fixture-preparation'}
    $workerPair = Send-Json 'auth/mobile/login' @{login='mvp.worker2';password='Manual-MVP-2026!';device='local-fixture-preparation'}
    $headers = @{Authorization="Bearer $($adminPair.accessToken)"}
    $source = Invoke-RestMethod "$api/inventories/$sourceId" -Headers $headers
    if ($source.number -ne 'INV-MVP-002' -or $source.status -ne 'Completed') { throw 'Expected completed synthetic INV-MVP-002 source; no clone created.' }
    $clone = Send-Json "inventories/$sourceId/clone" @{plannedAt=[DateTimeOffset]::UtcNow.ToString('o')} $headers
    if ($clone.sourceInventoryId -ne $sourceId -or $clone.status -ne 'Draft') { throw 'Unexpected clone response; start refused.' }
    $assignee = @($clone.commission)
    if ($assignee.Count -gt 1) { throw 'Unexpected multiple assignees; start refused.' }
    if ($assignee.Count -eq 0 -or $assignee[0].userId -ne $workerPair.user.id) {
        $oldUser = if ($assignee.Count -eq 1) { $assignee[0].userId } else { [guid]::Empty.ToString() }
        Send-Json "inventories/$($clone.id)/assignee" @{oldUserId=$oldUser;newUserId=$workerPair.user.id} $headers 'Put' | Out-Null
    }
    Invoke-RestMethod -Method Post "$api/inventories/$($clone.id)/start" -Headers $headers | Out-Null
    Write-Output $clone.id
} finally {
    foreach ($pair in @($adminPair,$workerPair)) {
        if ($pair) { Send-Json 'auth/mobile/logout' @{refreshToken=$pair.refreshToken} | Out-Null }
    }
}
