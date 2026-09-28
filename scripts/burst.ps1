<#
.SYNOPSIS
    Fires concurrent booking requests at one identical start time.

.DESCRIPTION
    Checks the safety property: never more confirmations than there is capacity, and no
    status other than 201 or 409. Seeded capacity is two service bays and two technicians.

    It does NOT require capacity to be fully used. The service picks one candidate bay and
    technician and does not retry, so under tight concurrency every caller can choose the
    same pair and all but one lose. That is under-use, not over-booking. Closing it is the
    contention work; this script reports it rather than failing on it.

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
$other    = ($results | Where-Object { $_ -ne 201 -and $_ -ne 409 }).Count

Write-Host ""
Write-Host "  201 Created  : $created"  -ForegroundColor Green
Write-Host "  409 Conflict : $conflict" -ForegroundColor Yellow
if ($other -gt 0) { Write-Host "  other        : $other" -ForegroundColor Red }
Write-Host ""

$safe = ($created -le $Capacity) -and ($created -ge 1) -and ($other -eq 0)

if (-not $safe) {
    Write-Host "FAIL - expected between 1 and $Capacity confirmations and no other status." -ForegroundColor Red
    exit 1
}

Write-Host "PASS - no over-booking ($created of $Capacity capacity confirmed)." -ForegroundColor Green

if ($created -lt $Capacity) {
    Write-Host ""
    Write-Host "NOTE: capacity under-used. Every caller picked the same bay and technician," -ForegroundColor DarkYellow
    Write-Host "      and the losers were rejected although another pair was free. Retrying" -ForegroundColor DarkYellow
    Write-Host "      across candidates is the next piece of work." -ForegroundColor DarkYellow
}
