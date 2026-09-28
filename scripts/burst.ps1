<#
.SYNOPSIS
    Fires concurrent booking requests at one identical start time.

.DESCRIPTION
    Seeded capacity is two service bays and two technicians, so the expected result is
    exactly two 201 responses and the rest 409.

    The dealership-day advisory lock makes this deterministic. Every caller reads committed
    state under the lock, so capacity is fully used and never exceeded. A 503 means the wait
    for the lock exceeded lock_timeout, which is a load signal rather than a capacity answer.

.EXAMPLE
    ./scripts/burst.ps1 -Count 20
#>
[CmdletBinding()]
param(
    [string] $BaseUrl       = 'http://localhost:5080',
    [int]    $Count         = 20,
    [int]    $ServiceTypeId = 4,   # Brake service, 120 min
    [int]    $DealershipId  = 1,
    [int]    $VehicleId     = 1,
    [int]    $Capacity      = 2,   # seeded bays and technicians
    [string] $StartAt
)

$ErrorActionPreference = 'Stop'

if (-not $StartAt) {
    $StartAt = (Get-Date).ToUniversalTime().Date.AddDays(14).AddHours(9).ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$body = @{
    dealershipId  = $DealershipId
    vehicleId     = $VehicleId
    serviceTypeId = $ServiceTypeId
    startAt       = $StartAt
} | ConvertTo-Json -Compress

Write-Host "Firing $Count concurrent bookings at $StartAt" -ForegroundColor Cyan

$results = 1..$Count | ForEach-Object -ThrottleLimit $Count -Parallel {
    try {
        $response = Invoke-WebRequest -Uri "$using:BaseUrl/bookings" `
            -Method Post -ContentType 'application/json' -Body $using:body `
            -SkipHttpErrorCheck
        [int] $response.StatusCode
    }
    catch {
        -1
    }
}

$created  = ($results | Where-Object { $_ -eq 201 }).Count
$conflict = ($results | Where-Object { $_ -eq 409 }).Count
$busy     = ($results | Where-Object { $_ -eq 503 }).Count
$other    = ($results | Where-Object { $_ -notin 201, 409, 503 }).Count

Write-Host ""
Write-Host "  201 Created  : $created"  -ForegroundColor Green
Write-Host "  409 Conflict : $conflict" -ForegroundColor Yellow
if ($busy  -gt 0) { Write-Host "  503 Busy     : $busy"  -ForegroundColor DarkYellow }
if ($other -gt 0) { Write-Host "  other        : $other" -ForegroundColor Red }
Write-Host ""

if ($created -ne $Capacity -or $other -gt 0) {
    Write-Host "FAIL - expected exactly $Capacity confirmations and no unexpected status." -ForegroundColor Red
    exit 1
}

Write-Host "PASS - capacity honoured exactly ($created of $Capacity)." -ForegroundColor Green

if ($busy -gt 0) {
    Write-Host ""
    Write-Host "NOTE: $busy request(s) timed out waiting for the dealership-day lock." -ForegroundColor DarkYellow
    Write-Host "      Correct behaviour under load, but worth watching if it grows." -ForegroundColor DarkYellow
}
