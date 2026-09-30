[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$petPackageScript = Join-Path $PSScriptRoot 'package_release.ps1'
$petTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('EaglePackageSelfTest-' + [Guid]::NewGuid().ToString('N'))
Write-Output "Isolated packaging fixtures: $petTestRoot"
New-Item -ItemType Directory -Path $petTestRoot | Out-Null
$petRuntimeNames = @('EagleDeskPet.exe', 'EagleDeskPet.Mcp.exe', 'THIRD-PARTY-NOTICES.md',
    'Live2DStage/index.html', 'Live2DStage/stage.css', 'Live2DStage/stage.js',
    'Live2DStage/actions.js', 'Live2DStage/bridge.js', 'Live2DStage/geometry.js')
$petChecks = 0
function Assert-PetPackage([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:petChecks++
    Write-Output "PASS $Message"
}
function New-PetPackageFixture([string]$Name, [string]$Omit = '') {
    $directory = Join-Path $petTestRoot $Name
    New-Item -ItemType Directory -Path $directory, (Join-Path $directory 'Live2DStage') | Out-Null
    foreach ($file in $petRuntimeNames) {
        if ($file -ne $Omit) { [IO.File]::WriteAllText((Join-Path $directory $file), 'fixture only: ' + $file) }
    }
    return $directory
}
function Assert-PetPackageRejected([string]$Directory, [string]$Archive, [string]$Expected) {
    $rejected = $false
    try { & $petPackageScript -ReleaseDirectory $Directory -ArchivePath $Archive | Out-Null }
    catch {
        if ($_.Exception.Message -notlike ('*' + $Expected + '*')) { throw }
        $rejected = $true
    }
    Assert-PetPackage $rejected "rejects $Expected"
}

$petRelease = New-PetPackageFixture 'valid'
foreach ($extra in @('local-runtime.json', 'pet-state.json', 'Core.js', 'Live2DStage/actions.test.cjs', 'Live2DStage/unexpected.js')) {
    [IO.File]::WriteAllText((Join-Path $petRelease $extra), 'must never be archived')
}
$petArchive = Join-Path $petTestRoot 'valid.zip'
& $petPackageScript -ReleaseDirectory $petRelease -ArchivePath $petArchive | Out-Null
$petZip = [IO.Compression.ZipFile]::OpenRead($petArchive)
try {
    $expected = @($petRuntimeNames + 'README.md' + 'SHA256SUMS.txt')
    Assert-PetPackage ($petZip.Entries.Count -eq 11) 'archive contains exactly eleven allowlisted files'
    Assert-PetPackage (@(Compare-Object $expected @($petZip.Entries.FullName) -CaseSensitive).Count -eq 0) 'tests, SDK-like files, secrets and unlisted scripts stay out'
    Assert-PetPackage (@(Get-Content -LiteralPath (Join-Path $petRelease 'SHA256SUMS.txt')).Count -eq 10) 'hash manifest includes six runtime files and four distribution files'
    foreach ($entry in $petZip.Entries) {
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $petRelease $entry.FullName) -Algorithm SHA256).Hash
        Assert-PetPackage ($actual -eq $expectedHash) ('verified content hash: ' + $entry.FullName)
    }
} finally { $petZip.Dispose() }

foreach ($missing in @('EagleDeskPet.exe', 'Live2DStage/geometry.js')) {
    $name = [IO.Path]::GetFileNameWithoutExtension($missing)
    $directory = New-PetPackageFixture ('missing-' + $name) $missing
    $archive = Join-Path $petTestRoot ('missing-' + $name + '.zip')
    Assert-PetPackageRejected $directory $archive ('Missing published file: ' + $missing)
    Assert-PetPackage (-not (Test-Path -LiteralPath $archive)) 'incomplete runtime never creates an archive'
    Assert-PetPackage (-not (Test-Path -LiteralPath (Join-Path $directory 'README.md'))) 'incomplete runtime leaves metadata untouched'
}
$petOriginalHash = (Get-FileHash -LiteralPath $petArchive -Algorithm SHA256).Hash
Assert-PetPackageRejected $petRelease $petArchive 'Archive already exists'
Assert-PetPackage ((Get-FileHash -LiteralPath $petArchive -Algorithm SHA256).Hash -eq $petOriginalHash) 'existing archive is preserved'
$petMetadataRelease = New-PetPackageFixture 'existing-metadata'
[IO.File]::WriteAllText((Join-Path $petMetadataRelease 'README.md'), 'keep this file')
Assert-PetPackageRejected $petMetadataRelease (Join-Path $petTestRoot 'existing-metadata.zip') 'Package metadata already exists'
Assert-PetPackage ((Get-Content -LiteralPath (Join-Path $petMetadataRelease 'README.md') -Raw) -eq 'keep this file') 'existing package metadata is preserved'
$petLinkedRelease = Join-Path $petTestRoot 'linked-stage'
New-Item -ItemType Directory -Path $petLinkedRelease | Out-Null
foreach ($file in $petRuntimeNames[0..2]) {
    [IO.File]::WriteAllText((Join-Path $petLinkedRelease $file), 'fixture only: ' + $file)
}
New-Item -ItemType Junction -Path (Join-Path $petLinkedRelease 'Live2DStage') -Target (Join-Path $petRelease 'Live2DStage') | Out-Null
Assert-PetPackageRejected $petLinkedRelease (Join-Path $petTestRoot 'linked-stage.zip') 'Published directories cannot be links'
Assert-PetPackage (-not (Test-Path -LiteralPath (Join-Path $petLinkedRelease 'README.md'))) 'linked input is rejected before metadata creation'
Write-Output "Package release: $petChecks checks passed; fixture evidence retained at $petTestRoot"
