param(
    [string]$Version,
    [string]$Notes = "",
    [string]$Dotnet = "dotnet",
    [switch]$Prerelease,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) {
    $metadata = Get-Content "Plain Craft Launcher 2/metadata.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $Version = $metadata.version.base
}

$output = Join-Path $PSScriptRoot "_release"
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

if (-not $SkipBuild) {
    & $Dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

$source = Join-Path $output "Plain Craft Launcher 2.exe"
if (-not (Test-Path $source)) {
    throw "Published executable not found: $source"
}

$assetName = "PCL-SUPER-$Version-win-x64.exe"
$asset = Join-Path $output $assetName
Copy-Item $source $asset -Force

$hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Asset : $asset"
Write-Host "SHA256: $hash"

if (-not $Notes) {
    $Notes = "PCL SUPER $Version"
}

$tag = "v$Version"
$arguments = @("release", "create", $tag, $asset, "--title", "PCL SUPER $Version", "--notes", $Notes)
if ($Prerelease) {
    $arguments += "--prerelease"
}

& gh @arguments
if ($LASTEXITCODE -ne 0) {
    throw "gh release create failed with exit code $LASTEXITCODE"
}

Write-Host "Release $tag created."
