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
$names = @("EagleDeskPet.exe", "EagleDeskPet.Mcp.exe", "THIRD-PARTY-NOTICES.md", "README.md")
foreach ($name in $names[0..2]) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $name) -PathType Leaf)) {
        throw "Missing published file: $name"
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\docs\DISTRIBUTION-README.md") -Destination $readmeTarget
$lines = foreach ($name in $names) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines($hashTarget, $lines, [System.Text.UTF8Encoding]::new($false))

# Explicit allowlist: never package saves, settings, credentials or build intermediates.
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
    if ($archive.Entries.Count -ne 5) { throw "Unexpected archive entry count." }
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
