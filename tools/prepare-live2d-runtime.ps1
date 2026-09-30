param(
    [string]$Sdk = "",
    [string]$Framework = "",
    [string]$Model = "",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"
$petRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (!$Sdk) { $Sdk = Join-Path $petRoot ".tool-cache\live2d-official\web-r5\CubismSdkForWeb-5-r.5" }
if (!$Framework) { $Framework = Join-Path $petRoot ".tool-cache\live2d-probe-deps\framework-r5.js" }
if (!$Model) { $Model = Join-Path $petRoot "DuckDeskPet\Assets\Live2DSource\FullActions\Model\eagle.model3.json" }
if (!$Output) { $Output = Join-Path $petRoot ".tool-cache\live2d-runtime\local-runtime.json" }

foreach ($petInput in @($Sdk, $Framework, $Model, $Output)) {
    if (![System.IO.Path]::IsPathFullyQualified($petInput)) { throw "Use absolute paths: $petInput" }
}
$Sdk = (Get-Item -LiteralPath $Sdk).FullName
$Framework = (Get-Item -LiteralPath $Framework).FullName
$Model = (Get-Item -LiteralPath $Model).FullName
$Output = [System.IO.Path]::GetFullPath($Output)
Write-Output "Verified SDK source: $Sdk"
Write-Output "Verified Framework source: $Framework"
Write-Output "Selected local model: $Model"
Write-Output "Exact local configuration target: $Output"

$petInfo = Get-Content -LiteralPath (Join-Path $Sdk "cubism-info.yml") -Raw
if ($petInfo -notmatch 'version: 5-r\.5(?:\s|$)') { throw "Expected official Cubism SDK for Web 5-r.5" }
$petCore = Join-Path $Sdk "Core\live2dcubismcore.js"
$petProvenance = Get-Content -LiteralPath "$Framework.provenance.json" -Raw | ConvertFrom-Json
function Get-PetHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$petFrameworkHash = Get-PetHash $Framework
if ($petProvenance.sdkRelease -ne "5-r.5" -or $petProvenance.sdkInfo -cne $petInfo -or $petProvenance.bundleSha256 -ne $petFrameworkHash) {
    throw "Framework bundle provenance does not match the selected official SDK"
}
foreach ($petInput in $petProvenance.inputs) {
    if ($petInput.name -match '[:\\]' -or @($petInput.name.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Unsafe Framework provenance path"
    }
    $petSource = [System.IO.Path]::GetFullPath((Join-Path $Sdk $petInput.name))
    if (!$petSource.StartsWith($Sdk + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        (Get-PetHash $petSource) -ne $petInput.sha256) { throw "Framework source hash mismatch: $petSource" }
}
$petModelJson = Get-Content -LiteralPath $Model -Raw | ConvertFrom-Json
if ($petModelJson.Version -ne 3 -or !$petModelJson.FileReferences.Moc -or !$petModelJson.FileReferences.Textures.Count) {
    throw "Expected a complete model3.json, not a sprite manifest"
}
$petModelRoot = Split-Path -Parent $Model
foreach ($petResource in @($petModelJson.FileReferences.Moc) + @($petModelJson.FileReferences.Textures)) {
    if ($petResource -match '[:\\]' -or @($petResource.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Unsafe model reference"
    }
    $petResourcePath = Join-Path $petModelRoot $petResource
    if (!(Test-Path -LiteralPath $petResourcePath -PathType Leaf)) { throw "Missing model resource: $petResourcePath" }
}
$petConfiguration = [ordered]@{
    sdkRoot = $Sdk
    framework = $Framework
    model = $Model
    assetsRoot = Join-Path $petRoot "DuckDeskPet\Assets"
    coreSha256 = Get-PetHash $petCore
    frameworkSha256 = $petFrameworkHash
    modelSha256 = Get-PetHash $Model
}
$petOutputFolder = Split-Path -Parent $Output
New-Item -ItemType Directory -Path $petOutputFolder -Force | Out-Null
$petConfiguration | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
Write-Output "Prepared local development runtime; no SDK, Core or Framework was copied into source or release packages"
Write-Output "Start the development EXE normally, or set EAGLE_PET_LIVE2D_CONFIG to this file for an isolated local build"
