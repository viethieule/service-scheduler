<#
.SYNOPSIS
    Fires concurrent booking requests at one identical start time.

.DESCRIPTION
    With two service bays and two technicians seeded, the expected outcome is exactly
    two 201 responses and the rest 409. Anything else means the overlap constraints are
    missing or the conflict is not being surfaced.

.EXAMPLE
    ./scripts/burst.ps1 -Count 20
#>
[CmdletBinding()]
param(
    [string] $BaseUrl       = 'http://localhost:5080',
    [int]    $Count         = 20,
    [string] $ServiceTypeId = '66666666-6666-6666-6666-666666666604',  # Brake service, 120 min
    [string] $DealershipId  = '11111111-1111-1111-1111-111111111111',
    [string] $VehicleId     = '55555555-5555-5555-5555-555555555555',
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
if ($created -eq 2 -and $other -eq 0) {
    Write-Host "PASS - capacity honoured exactly." -ForegroundColor Green
}
else {
    Write-Host "FAIL - expected exactly 2 confirmations and no other status." -ForegroundColor Red
    exit 1
}
