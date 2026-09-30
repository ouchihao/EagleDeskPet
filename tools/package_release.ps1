param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$ArchivePath
)

$ErrorActionPreference = "Stop"
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$archiveTarget = [System.IO.Path]::GetFullPath($ArchivePath)
Write-Output "Package source: $releaseRoot"
Write-Output "Archive destination: $archiveTarget"
if (Test-Path -LiteralPath $archiveTarget) {
    throw "Archive already exists; choose a new path. Existing files were preserved."
}
$readmeTarget = Join-Path $releaseRoot "README.md"
$hashTarget = Join-Path $releaseRoot "SHA256SUMS.txt"
if ((Test-Path -LiteralPath $readmeTarget) -or (Test-Path -LiteralPath $hashTarget)) {
    throw "Package metadata already exists; use a fresh published directory."
}
$requiredNames = @(
    "EagleDeskPet.exe", "EagleDeskPet.Mcp.exe", "THIRD-PARTY-NOTICES.md",
    "Live2DStage/index.html", "Live2DStage/stage.css", "Live2DStage/stage.js",
    "Live2DStage/actions.js", "Live2DStage/bridge.js", "Live2DStage/geometry.js"
)
foreach ($directory in @($releaseRoot, (Join-Path $releaseRoot "Live2DStage"))) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing published directory: $directory" }
    if ((Get-Item -LiteralPath $directory).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Published directories cannot be links: $directory"
    }
}
foreach ($name in $requiredNames) {
    $source = Join-Path $releaseRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing published file: $name"
    }
    if ((Get-Item -LiteralPath $source).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Published files cannot be links: $name"
    }
}
$names = $requiredNames + "README.md"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\docs\DISTRIBUTION-README.md") -Destination $readmeTarget
$lines = foreach ($name in $names) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines($hashTarget, $lines, [System.Text.UTF8Encoding]::new($false))

# Explicit allowlist excludes tests, saves, credentials, local model configuration and official SDK files
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [System.IO.File]::Open($archiveTarget, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in ($names + "SHA256SUMS.txt")) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, (Join-Path $releaseRoot $name), $name,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$archive = [System.IO.Compression.ZipFile]::OpenRead($archiveTarget)
try {
    if ($archive.Entries.Count -ne ($names.Count + 1)) { throw "Unexpected archive entry count." }
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notin ($names + "SHA256SUMS.txt")) { throw "Unexpected archive entry." }
        $expected = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $entry.FullName) -Algorithm SHA256).Hash
        $inputStream = $entry.Open()
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hasher.ComputeHash($inputStream)).Replace("-", "") }
        finally { $hasher.Dispose(); $inputStream.Dispose() }
        if ($actual -ne $expected) { throw "Archive verification failed: $($entry.FullName)" }
        Write-Output "Verified: $($entry.FullName)"
    }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $archiveTarget -Algorithm SHA256
