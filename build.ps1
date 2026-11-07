<#
.SYNOPSIS
  Builds Emma Space: the C++ engine (CMake), then the .NET solution.
.EXAMPLE
  ./build.ps1                 # Release build of everything
  ./build.ps1 -Test           # ...and run native + managed tests
  ./build.ps1 -Publish        # self-contained single-file app in artifacts/publish/win-x64
  ./build.ps1 -Pack           # Velopack installer + update packages in artifacts/releases/win-x64
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $Test,
    [switch] $Publish,
    [switch] $Pack,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string] $title, [scriptblock] $action) {
    Write-Host "==> $title" -ForegroundColor Magenta
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

$preset = $Runtime
Invoke-Step "Configure native ($preset)" { cmake --preset $preset }
Invoke-Step "Build native" { cmake --build --preset "$preset-release" }
if ($Test) { Invoke-Step "Native tests" { ctest --preset $preset } }

# The native library is already built, so MSBuild can skip its own CMake step.
Invoke-Step "Build .NET ($Configuration)" { dotnet build EmmaSpace.slnx -c $Configuration -p:EmmaSkipNative=true }
if ($Test) { Invoke-Step ".NET tests" { dotnet test --project tests/EmmaSpace.Core.Tests -c $Configuration -p:EmmaSkipNative=true --no-build } }

$common = @('-c', 'Release', '-r', $Runtime, '--self-contained', '-p:EmmaSkipNative=true', "-p:EmmaNativePreset=$preset",
            '-p:DebugType=None', '-p:CopyDebugSymbolFilesFromPackages=false')

if ($Publish) {
    $out = "artifacts/publish/$Runtime"
    Invoke-Step "Publish ($Runtime)" {
        dotnet publish src/EmmaSpace.App @common -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o $out
    }
    Write-Host "Published to $out" -ForegroundColor Green
}

if ($Pack) {
    # Velopack wants a plain folder publish; it adds its own launcher, delta updates and Setup.exe.
    [xml] $props = Get-Content Directory.Build.props
    $version = $props.Project.PropertyGroup.Version
    $folder = "artifacts/pack-input/$Runtime"
    $releases = "artifacts/releases/$Runtime"
    Invoke-Step "Publish for packaging ($Runtime)" { dotnet publish src/EmmaSpace.App @common -o $folder }
    Invoke-Step "Restore vpk" { dotnet tool restore }
    Invoke-Step "Velopack pack $version" {
        dotnet vpk pack --packId EmmaSpace --packVersion $version --packDir $folder --mainExe EmmaSpace.exe `
            --packTitle "Emma Space" --packAuthors "Emma Space" --icon src/EmmaSpace.App/Assets/emmaspace.ico `
            --runtime $Runtime --outputDir $releases
    }
    Write-Host "Installer and update packages in $releases" -ForegroundColor Green
}
