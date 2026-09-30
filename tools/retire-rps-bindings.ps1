param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$petRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$petOutput = [IO.Path]::GetFullPath($OutputDirectory)
Write-Output "Exact staged output: $petOutput"
if (Test-Path -LiteralPath $petOutput) { throw 'Choose a fresh output directory' }
New-Item -ItemType Directory -Path $petOutput | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Read-PetEntry($Entry) {
    $stream = $Entry.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $memory.Dispose(); $stream.Dispose() }
}
function Write-PetEntry($Zip, [string]$Name, [byte[]]$Bytes) {
    $entry = $Zip.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(2026, 9, 30, 0, 0, 0, [TimeSpan]::Zero)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}
function Hash-PetBytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)) }

foreach ($petName in @('Default-v1.zip', 'Office-v1.zip')) {
    $petSource = (Get-Item -LiteralPath (Join-Path $petRoot "DuckDeskPet/Assets/OutfitBindings/$petName")).FullName
    $petTarget = Join-Path $petOutput $petName
    Write-Output "Read-only source: $petSource"
    Write-Output "New archive target: $petTarget"
    $zip = [IO.Compression.ZipFile]::OpenRead($petSource)
    try {
        $manifest = [Text.Encoding]::UTF8.GetString((Read-PetEntry $zip.GetEntry('bindings.json'))) | ConvertFrom-Json -AsHashtable
        $removed = @($manifest.frames.Keys | Where-Object { $_ -match '^Rps(Rock|Paper|Scissors|Win|Lose)/' })
        if ($removed.Count -ne 797) { throw "Expected 797 retired frames in $petName" }
        $maps = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($key in $removed) { [void]$maps.Add($manifest.frames[$key].map); $manifest.frames.Remove($key) }
        if ($manifest.frames.Count -ne 1695) { throw 'Core action coverage changed' }
        $hashes = @{}
        $output = [IO.Compression.ZipFile]::Open($petTarget, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName -eq 'bindings.json' -or $maps.Contains($entry.FullName)) { continue }
                $bytes = Read-PetEntry $entry
                $hashes[$entry.FullName] = Hash-PetBytes $bytes
                Write-PetEntry $output $entry.FullName $bytes
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 20 -Compress))
            Write-PetEntry $output 'bindings.json' $bytes
        } finally { $output.Dispose() }
    } finally { $zip.Dispose() }
    $check = [IO.Compression.ZipFile]::OpenRead($petTarget)
    try {
        if ($check.Entries.Count -ne $hashes.Count + 1) { throw 'Unexpected output entries' }
        foreach ($entry in $check.Entries) {
            if ($entry.FullName -match '/Rps') { throw 'Retired map survived' }
            if ($entry.FullName -ne 'bindings.json' -and (Hash-PetBytes (Read-PetEntry $entry)) -ne $hashes[$entry.FullName]) {
                throw "Unrelated mapping changed: $($entry.FullName)"
            }
        }
    } finally { $check.Dispose() }
    Write-Output "Verified $petName : 1694 core frames + neutral; unrelated map bytes unchanged"
}
