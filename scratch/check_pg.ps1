$pg16 = "C:\Program Files\PostgreSQL\16\bin\pg_ctl.exe"
Write-Host "pg_ctl 16 exists: $(Test-Path $pg16)"

$pg16Data = "C:\Program Files\PostgreSQL\16\data"
Write-Host "pg 16 data exists: $(Test-Path $pg16Data)"

$pg17 = "C:\Program Files\PostgreSQL\17\bin\pg_ctl.exe"
Write-Host "pg_ctl 17 exists: $(Test-Path $pg17)"

$pg17Data = "C:\Program Files\PostgreSQL\17\data"
Write-Host "pg 17 data exists: $(Test-Path $pg17Data)"

Get-Service | Where-Object { $_.DisplayName -like "*Postgre*" } | Select-Object Name, Status, DisplayName
