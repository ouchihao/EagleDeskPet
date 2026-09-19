param(
    [string]$DotNetPath = "dotnet",
    [string]$PackageSource = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "DuckDeskPet\DuckDeskPet.csproj"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [xml]$petProject = Get-Content -LiteralPath $projectPath -Raw
    $petVersion = @($petProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if ($petVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Cannot determine a safe release version from $projectPath"
    }
    $OutputDirectory = Join-Path $PSScriptRoot "dist\v$petVersion"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
Write-Output "Release destination: $OutputDirectory"
if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not (Get-Item -LiteralPath $OutputDirectory).PSIsContainer -or
        @((Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)).Count -gt 0) {
        throw "Release destination is not empty. Existing files were preserved; choose a new -OutputDirectory."
    }
}
$arguments = @(
    "publish",
    $projectPath,
    "-c", "Release",
    "-p:PublishProfile=win-x64",
    "-o", $OutputDirectory,
    "--nologo"
)

if (-not [string]::IsNullOrWhiteSpace($PackageSource)) {
    $arguments += @("--source", $PackageSource, "--ignore-failed-sources")
}

& $DotNetPath @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$bridgeArguments = @(
    "publish", (Join-Path $PSScriptRoot "EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $OutputDirectory, "--nologo"
)
if (-not [string]::IsNullOrWhiteSpace($PackageSource)) {
    $bridgeArguments += @("--source", $PackageSource, "--ignore-failed-sources")
}
& $DotNetPath @bridgeArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$thirdPartyNotices = Join-Path $PSScriptRoot "docs\THIRD-PARTY-NOTICES.md"
Copy-Item -LiteralPath $thirdPartyNotices -Destination (Join-Path $OutputDirectory "THIRD-PARTY-NOTICES.md")

$outputPath = Join-Path $OutputDirectory "EagleDeskPet.exe"
Write-Output "Published: $outputPath"
Write-Output "Optional AI bridge: $(Join-Path $OutputDirectory 'EagleDeskPet.Mcp.exe')"
Write-Output "Third-party notices: $(Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.md')"
