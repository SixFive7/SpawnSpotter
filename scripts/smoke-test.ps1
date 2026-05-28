#Requires -RunAsAdministrator
<#
.SYNOPSIS
    End-to-end smoke test for the *published* (Native AOT) SpawnSpotter binary.

.DESCRIPTION
    Unit tests run on the JIT and cannot catch AOT/trim/marshalling regressions or native
    interop faults (e.g. an EVENT_TRACE_LOGFILEW/TIME_ZONE_INFORMATION layout error that makes
    ProcessTrace call a garbage callback and access-violate). This script exercises the real
    published exe for a few seconds: it starts the NT Kernel Logger ETW session + hooks +
    pipeline, then shuts down cleanly. A non-zero exit or a missing exit summary means the live
    lifecycle is broken even if `dotnet test` is green.

    Requires elevation (the NT Kernel Logger and the requireAdministrator manifest both need it).

.EXAMPLE
    dotnet publish SpawnSpotter.csproj -c Release
    pwsh -File scripts/smoke-test.ps1
#>
[CmdletBinding()]
param(
    [int]$DurationSeconds = 6,
    [string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0\win-x64\publish\SpawnSpotter.exe"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    Write-Host "SMOKE TEST FAILED: published exe not found at`n  $Exe`nPublish first:  dotnet publish SpawnSpotter.csproj -c Release"
    exit 2
}

# Start fresh: reclaim the singleton kernel logger if a prior run leaked it.
logman stop "NT Kernel Logger" -ets 2>$null | Out-Null

Write-Host "Running: $Exe watch --duration ${DurationSeconds}s --mode silent"
$out = & $Exe watch --duration "${DurationSeconds}s" --mode silent 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Host "---- output ----`n$out----------------"

$ok = $true
if ($code -ne 0) {
    Write-Host "FAIL: exit code $code (expected 0) - the live ETW/hook lifecycle crashed."
    $ok = $false
} else {
    Write-Host "PASS: clean exit 0"
}

# The exit summary is always printed on graceful shutdown; its presence proves the full
# startup -> capture -> shutdown path ran (ETW session, consumer, pipeline, exporters).
if ($out -match 'Logged STEAL=') {
    Write-Host "PASS: exit summary present (full lifecycle ran)"
} else {
    Write-Host "FAIL: exit summary missing - shutdown path did not complete."
    $ok = $false
}

if ($ok) { Write-Host "`nSMOKE TEST PASSED"; exit 0 } else { Write-Host "`nSMOKE TEST FAILED"; exit 1 }
