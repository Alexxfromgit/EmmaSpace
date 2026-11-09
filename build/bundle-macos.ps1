# Builds "EmmaSpace.app" from a published app folder for `vpk pack --packDir <app>`: Velopack's bundle,
# but with build/macos/Info.plist (which declares .emmasnap documents) and the document icon added to
# Contents/Resources. Velopack packs a --packDir that ends in .app as it is. Prints the bundle's path.
# Usage: pwsh build/bundle-macos.ps1 -Version 0.2.0 -PackDir artifacts/pack-input/osx-arm64 -Runtime osx-arm64 -OutDir <dir>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PackDir,
    [Parameter(Mandatory)] [string] $Runtime,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$assets = Join-Path $root 'src/EmmaSpace.App/Assets'

New-Item -ItemType Directory -Force $OutDir | Out-Null
$plist = Join-Path $OutDir 'Info.plist'
(Get-Content (Join-Path $PSScriptRoot 'macos/Info.plist') -Raw).Replace('@VERSION@', $Version) |
    Set-Content $plist -NoNewline -Encoding utf8NoBOM

dotnet vpk bundle --packId EmmaSpace --packVersion $Version --packDir $PackDir --mainExe EmmaSpace `
    --packTitle 'Emma Space' --packAuthors 'Alexander Rubtsov' --icon (Join-Path $assets 'emmaspace.icns') `
    --plist $plist --runtime $Runtime --outputDir $OutDir | Out-Host

$app = Get-ChildItem $OutDir -Filter '*.app' -Directory | Select-Object -First 1
if (-not $app) { throw "vpk bundle made no .app in $OutDir" }
Copy-Item (Join-Path $assets 'emmasnap.icns') (Join-Path $app.FullName 'Contents/Resources/emmasnap.icns')
$app.FullName
