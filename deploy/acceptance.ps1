[CmdletBinding()]
param([string]$ApiUrl = 'http://localhost:15147/api/v1', [Parameter(Mandatory)][string]$Password,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../tmp/acceptance'))
$ErrorActionPreference = 'Stop'
$api = [uri]$ApiUrl
if (!$api.IsLoopback -or $api.Port -in @(5147, 5173, 5174)) { throw 'Acceptance must use an isolated loopback demo API.' }
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw "Acceptance failed: $Message" }; Write-Output "OK: $Message" }
function Request([string]$Method, [string]$Path, $Body, [bool]$Worker = $false) {
    $arguments = @{ Method=$Method; Uri="$ApiUrl$Path"; Headers=@{Authorization="Bearer $(if ($Worker) {$script:worker.accessToken} else {$script:admin.accessToken})"} }
    if ($null -ne $Body) { $arguments.Body=$Body | ConvertTo-Json -Depth 8 -Compress; $arguments.ContentType='application/json; charset=utf-8' }
    Invoke-RestMethod @arguments
}
$script:admin = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType application/json -Body (@{login='admin'; password=$Password; device='acceptance'} | ConvertTo-Json)
$script:worker = Invoke-RestMethod -Method Post -Uri "$ApiUrl/auth/mobile/login" -ContentType application/json -Body (@{login='demo.inventory'; password=$Password; device='acceptance'} | ConvertTo-Json)
try {
    $assets = (Request GET '/assets/?pageSize=200' $null).items
    Assert ($assets.Count -eq 3) 'Imported demo registry contains three assets'
    $output = [IO.Path]::GetFullPath($OutputDirectory); New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-WebRequest "$ApiUrl/assets/$($assets[0].id)/qr" -Headers @{Authorization="Bearer $($admin.accessToken)"} -OutFile (Join-Path $output 'qr.png')
    Assert ([IO.File]::ReadAllBytes((Join-Path $output 'qr.png'))[0] -eq 137) 'PNG QR label downloaded'
    $draft = Request POST '/inventories' @{type='Planned'; reason='AnnualReporting'; plannedAt=[DateTimeOffset]::UtcNow.ToString('O')}
    $full = Request POST "/inventories/$($draft.id)/start" $null
    Assert ($full.items.Count -eq 3 -and $full.commission.Count -eq 1 -and $full.commission[0].userId -eq $worker.user.id) 'Planned inventory covers all assets and assigns one employee'
    foreach ($item in $full.items) { Request POST "/mobile/inventories/$($full.id)/scan" @{qrValue=$item.assetId; condition='Serviceable'} $true | Out-Null }
    $duplicate = Request POST "/mobile/inventories/$($full.id)/scan" @{qrValue=$full.items[0].assetId; condition='Serviceable'} $true
    Assert $duplicate.duplicate 'Final scan retry is idempotent'
    Assert ((Request GET "/inventories/$($full.id)" $null).status -eq 'AwaitingConfirmation') 'Full traversal closes automatically'
    Request POST "/mobile/inventories/$($full.id)/confirm" @{password=$Password} $true | Out-Null
    foreach ($format in @('xlsx','pdf')) {
        Invoke-WebRequest "$ApiUrl/inventories/$($full.id)/reports/inv-1.$format" -Headers @{Authorization="Bearer $($admin.accessToken)"} -OutFile (Join-Path $output "inv-1.$format")
    }
    $hash = (Get-FileHash (Join-Path $output 'inv-1.pdf')).Hash
    $asset = $assets[0]
    $body = @{ inventoryNumber=$asset.inventoryNumber; name="Изменено после подтверждения $([guid]::NewGuid().ToString('N').Substring(0,8))"; accountingAccount=$asset.accountingAccount; serialNumber=$asset.serialNumber; manufactureYear=$asset.manufactureYear; acceptedAt=$asset.acceptedAt; initialCost=777; departmentId=$asset.departmentId; locationId=$asset.locationId; materiallyResponsibleEmployeeId=$asset.materiallyResponsibleEmployeeId }
    Invoke-RestMethod -Method Put -Uri "$ApiUrl/assets/$($asset.id)" -Headers @{Authorization="Bearer $($admin.accessToken)"; 'If-Match'="`"$($asset.version)`""} -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json) | Out-Null
    Invoke-WebRequest "$ApiUrl/inventories/$($full.id)/reports/inv-1.pdf" -Headers @{Authorization="Bearer $($admin.accessToken)"} -OutFile (Join-Path $output 'inv-1-after.pdf')
    Assert ((Get-FileHash (Join-Path $output 'inv-1-after.pdf')).Hash -eq $hash -and (Request GET "/inventories/$($full.id)" $null).items[0].name -ne $body.name) 'Old accounting snapshot and final report remain unchanged'
    $clone = Request POST "/inventories/$($full.id)/clone" @{plannedAt=[DateTimeOffset]::UtcNow.AddDays(1).ToString('O')}
    Assert ($clone.status -eq 'Draft' -and $clone.sourceInventoryId -eq $full.id -and $clone.items.Count -eq 0 -and $clone.unexpectedScans.Count -eq 0) 'Completed inventory creates a linked clean draft'
    $clone = Request POST "/inventories/$($clone.id)/start" $null
    Assert ($clone.items.Count -eq 3 -and ($clone.items | Where-Object assetId -eq $asset.id).name -eq $body.name) 'Cloned inventory takes a fresh accounting snapshot on start'
    Request POST "/inventories/$($clone.id)/cancel" $null | Out-Null
    Invoke-WebRequest "$ApiUrl/inventories/$($full.id)/reports/inv-1.pdf" -Headers @{Authorization="Bearer $($admin.accessToken)"} -OutFile (Join-Path $output 'inv-1-after-clone.pdf')
    Assert ((Get-FileHash (Join-Path $output 'inv-1-after-clone.pdf')).Hash -eq $hash) 'Cloning and cancellation do not change the original report'
    $partial = Request POST '/inventories' @{type='Manual'; reason='AnnualReporting'; plannedAt=[DateTimeOffset]::UtcNow.ToString('O')}
    Request POST "/inventories/$($partial.id)/start" $null | Out-Null
    Request POST "/mobile/inventories/$($partial.id)/scan" @{qrValue=$assets[0].id; condition='Damaged'; comment='Демонстрационная царапина'} $true | Out-Null
    $finished = Request POST "/mobile/inventories/$($partial.id)/finish-scanning" $null $true
    Assert (@($finished.items | Where-Object result -eq 'NotFound').Count -eq 2) 'Partial finish marks two remaining assets NotFound'
    Request POST "/mobile/inventories/$($partial.id)/confirm" @{password=$Password} $true | Out-Null
    $employees = Request GET '/employees' $null
    $mol = $employees | Where-Object personnelNumber -eq 'EMP-001'
    $event = @{eventId="demo-dismissal-$([guid]::NewGuid())"; employeeId=$mol.id; occurredAt=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(5)).ToString('O')}
    $internal = ([uri]$ApiUrl).GetLeftPart([UriPartial]::Authority) + '/internal/v1/hr-events/dismissals'
    $first = Invoke-RestMethod -Method Post -Uri $internal -ContentType application/json -Body ($event | ConvertTo-Json)
    $second = Invoke-RestMethod -Method Post -Uri $internal -ContentType application/json -Body ($event | ConvertTo-Json)
    Assert ($first.status -eq 'Accepted') 'HR event with a timezone offset is accepted for processing'
    Assert ($first.id -eq $second.id) 'Repeated HR eventId returns one event'
    $processed = $null
    for ($i=0; $i -lt 40; $i++) {
        $events = Request GET '/inventory-events' $null
        $processed = $events | Where-Object eventId -eq $event.eventId
        if ($processed.status -eq 'Processed') { break }
        if ($processed.status -eq 'Failed') { throw "HR event processing failed: $($processed.lastError)" }
        Start-Sleep -Seconds 1
    }
    Assert ($processed.status -eq 'Processed' -and $null -ne $processed.createdInventoryId) 'Background worker creates one event-driven inventory'
    $eventInventory = Request GET "/inventories/$($processed.createdInventoryId)" $null
    Assert ($eventInventory.status -eq 'Active' -and $eventInventory.items.Count -gt 0) 'Event-driven inventory starts immediately and captures its asset scope'
    Assert ($eventInventory.commission.Count -eq 1 -and $eventInventory.commission[0].userId -eq $worker.user.id) 'Target MOL is not the assigned inventory employee'
    # Leave one fresh active inventory for the actual Android UI scan/partial-finish/confirmation pass.
    $android = Request POST '/inventories' @{type='Manual'; reason='AnnualReporting'; plannedAt=[DateTimeOffset]::UtcNow.ToString('O'); reasonDetails='Android acceptance'}
    $android = Request POST "/inventories/$($android.id)/start" $null
    Write-Output "ANDROID: $($android.number), ID=$($android.id), QR=$($android.items[0].assetId)"
    Write-Output "Acceptance artifacts: $([IO.Path]::GetFullPath($output))"
} finally {
    if ($worker) { Request POST '/auth/mobile/logout' @{refreshToken=$worker.refreshToken} $true | Out-Null }
    if ($admin) { Request POST '/auth/mobile/logout' @{refreshToken=$admin.refreshToken} | Out-Null }
}
