[CmdletBinding()]
param([string]$ApiUrl = 'http://localhost:15147/api/v1',
    [Parameter(Mandatory)][string]$Password, [string]$AdminLogin = 'admin')
$ErrorActionPreference = 'Stop'
if ($Password.Length -lt 10) { throw 'Use a demo password with at least 10 characters.' }
if (([uri]$ApiUrl).Port -eq 5147) { throw 'Demo seed refuses the standard working API port. Use the isolated demo stack.' }
function Request([string]$Method, [string]$Path, $Body) {
    $arguments = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $script:headers }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 8 -Compress; $arguments.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @arguments
}
$script:headers = @{}
$start = Request POST '/auth/mobile/login/start' @{ login = $AdminLogin }
$route = if ($start.passwordSetupRequired) { '/auth/mobile/initial-password' } else { '/auth/mobile/login' }
$auth = Request POST $route @{ login = $AdminLogin; password = $Password; device = 'demo-seed' }
$script:headers = @{ Authorization = "Bearer $($auth.accessToken)" }
try {
    $departments = Request GET '/departments' $null
    if ($departments.Count -gt 0 -or (Request GET '/assets/' $null).totalCount -gt 0) { throw 'Demo seed requires empty catalogs; existing data are never overwritten.' }
    Request PUT '/organization' @{name='ООО Демонстрация'; inn='1234567890'; kpp='123456789'; okpo='12345678'; address='Екатеринбург, демонстрационный адрес'; directorName='Директор Д. Д.'} | Out-Null
    $office = Request POST '/departments' @{code='DEP-01'; name='Офис'; isInventoryDepartment=$false}
    $inventory = Request POST '/departments' @{code='INV'; name='Отдел инвентаризации'; isInventoryDepartment=$true}
    Request POST '/locations' @{code='ROOM-01'; name='Кабинет 101'; departmentId=$office.id} | Out-Null
    Request POST '/employees' @{personnelNumber='EMP-001'; fullName='Петров Пётр Петрович'; departmentId=$office.id; isMateriallyResponsible=$true; isEmployed=$true} | Out-Null
    $employee = Request POST '/employees' @{personnelNumber='INV-001'; fullName='Иванов Иван Иванович'; departmentId=$inventory.id; isMateriallyResponsible=$false; isEmployed=$true}
    Request POST '/users' @{login='demo.inventory'; password=$Password; role='InventoryEmployee'; employeeId=$employee.id} | Out-Null
    $file = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'demo-assets.csv')
    $preview = Invoke-RestMethod -Method Post -Uri "$ApiUrl/assets/imports?apply=false" -Headers $script:headers -Form @{file=$file}
    if ($preview.invalidRows -ne 0 -or $preview.validRows -ne 3) { throw 'Demo import dry-run failed.' }
    $applied = Invoke-RestMethod -Method Post -Uri "$ApiUrl/assets/imports?apply=true" -Headers $script:headers -Form @{file=$file}
    Write-Output "Demo ready: $($applied.appliedRows) assets; administrator '$AdminLogin'; inventory employee 'demo.inventory'. Password is the explicitly supplied value."
} finally {
    Request POST '/auth/mobile/logout' @{refreshToken=$auth.refreshToken} | Out-Null
}
