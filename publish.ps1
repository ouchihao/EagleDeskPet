param(
    [string]$DotNetPath = "dotnet",
    [string]$PackageSource = "",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist\v1.8.0")
)

$ErrorActionPreference = "Stop"
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
Write-Output "Release destination: $OutputDirectory"
$projectPath = Join-Path $PSScriptRoot "DuckDeskPet\DuckDeskPet.csproj"
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

$outputPath = Join-Path $OutputDirectory "EagleDeskPet.exe"
Write-Output "Published: $outputPath"
Write-Output "Optional AI bridge: $(Join-Path $OutputDirectory 'EagleDeskPet.Mcp.exe')"
