[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$DotnetRoot,
    [switch]$SkipOutfit
)

$ErrorActionPreference = 'Stop'
$petExe = (Get-Item -LiteralPath $Exe -ErrorAction Stop).FullName
if ([IO.Path]::GetExtension($petExe) -ne '.exe') { throw 'Pass the real Release EagleDeskPet.exe, not a DLL or build directory.' }
if ($DotnetRoot) {
    $petRuntime = (Get-Item -LiteralPath $DotnetRoot -ErrorAction Stop).FullName
    if (-not (Test-Path -LiteralPath (Join-Path $petRuntime 'dotnet.exe'))) { throw 'DotnetRoot must contain dotnet.exe.' }
}

# Always create a new temp root. There is intentionally no parameter for reusing
# another data directory: seeding test currency must never touch an existing pet.
$petRunId = [Guid]::NewGuid().ToString('N')
$petRunRoot = Join-Path ([IO.Path]::GetTempPath()) ('EagleExpansionSmoke-' + $petRunId)
$petData = Join-Path $petRunRoot 'data'
$petEvidence = Join-Path $petRunRoot 'evidence'
if (Test-Path -LiteralPath $petRunRoot) { throw 'Fresh smoke directory unexpectedly already exists.' }
Write-Output "Executable: $petExe"
Write-Output "Isolated run root: $petRunRoot"
New-Item -ItemType Directory -Path $petRunRoot, $petData, $petEvidence | Out-Null
[IO.File]::WriteAllText((Join-Path $petRunRoot 'expansion-smoke.sentinel'), $petRunId)

$petStart = [Diagnostics.ProcessStartInfo]::new()
$petStart.FileName = $petExe
$petStart.WorkingDirectory = [IO.Path]::GetDirectoryName($petExe)
$petStart.UseShellExecute = $false
$petStart.CreateNoWindow = $true
$petStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$petStart.EnvironmentVariables['EAGLE_PET_DATA_DIR'] = $petData
$petStart.EnvironmentVariables['EAGLE_PET_SMOKE_DIR'] = $petEvidence
$petStart.EnvironmentVariables['EAGLE_PET_SMOKE_RUN_ROOT'] = $petRunRoot
$petStart.EnvironmentVariables['EAGLE_PET_SMOKE_MODE'] = 'expansion'
$petStart.EnvironmentVariables['EAGLE_PET_TEST_CHANNEL'] = $petRunId
$petStart.EnvironmentVariables['EAGLE_PET_SMOKE_SKIP_OUTFIT'] = $(if ($SkipOutfit) { '1' } else { '0' })
if ($DotnetRoot) { $petStart.EnvironmentVariables['DOTNET_ROOT'] = $petRuntime }

$petChild = [Diagnostics.Process]::Start($petStart)
$petElapsed = [Diagnostics.Stopwatch]::StartNew()
$petTimedOut = $false
$petCloseRequested = $false
try {
    # Each wait is bounded to 250 ms; only this exact child PID is controlled.
    while (-not $petChild.WaitForExit(250)) {
        if ($petElapsed.Elapsed.TotalSeconds -ge 295 -and -not $petCloseRequested) {
            $petCloseRequested = $true
            $petChild.CloseMainWindow() | Out-Null
        }
        if ($petElapsed.Elapsed.TotalSeconds -ge 300) {
            $petTimedOut = $true
            $petChild.Kill()
            break
        }
    }
    $petReportPath = Join-Path $petEvidence 'expansion-smoke.json'
    if ($petTimedOut -or -not (Test-Path -LiteralPath $petReportPath)) {
        $petFailure = [ordered]@{
            passed = $false
            completed = $false
            launcherFailure = $(if ($petTimedOut) { 'Child exceeded the 300 second hard limit; only the isolated child was terminated.' } else { 'The child exited without an expansion report. Check that the supplied Release includes expansion smoke.' })
            executable = $petExe
            processId = $petChild.Id
            dataDirectory = $petData
            elapsedSeconds = [Math]::Round($petElapsed.Elapsed.TotalSeconds, 2)
        }
        $petFailure | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $petEvidence 'launcher-failure.json') -Encoding utf8
        Write-Output ($petFailure | ConvertTo-Json -Depth 8)
        throw "Expansion smoke failed. Evidence retained at $petEvidence"
    }
    $petReport = Get-Content -LiteralPath $petReportPath -Raw | ConvertFrom-Json
    Write-Output (Get-Content -LiteralPath $petReportPath -Raw)
    Write-Output "Evidence: $petReportPath"
    if (-not $petReport.passed -or -not $petReport.completed) { throw "Expansion smoke did not pass. Evidence retained at $petEvidence" }
    if ($petReport.cases | Where-Object { $_.Status -eq 'skipped' }) {
        Write-Warning 'This run explicitly skipped an acceptance case; it is not full expansion acceptance.'
    }
}
finally {
    if (-not $petChild.HasExited) {
        $petChild.CloseMainWindow() | Out-Null
        if (-not $petChild.WaitForExit(500)) { $petChild.Kill() }
    }
    $petChild.Dispose()
}
