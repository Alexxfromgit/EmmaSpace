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

# Terminals opened before CMake was installed have a stale PATH; reload it from the registry,
# then fall back to the usual install folders (CMake installer, Visual Studio's bundled copy).
function Resolve-CMake {
    if (Get-Command cmake -ErrorAction SilentlyContinue) { return }
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (Get-Command cmake -ErrorAction SilentlyContinue) { return }
    $candidates = @("$env:ProgramFiles\CMake\bin\cmake.exe") +
        (Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | ForEach-Object FullName)
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw "CMake was not found. Install it (winget install Kitware.CMake) or add 'C++ CMake tools for Windows' in the Visual Studio Installer, then open a new terminal."
    }
    $env:Path = (Split-Path $found) + ';' + $env:Path
}
Resolve-CMake

$preset = $Runtime
Invoke-Step "Configure native ($preset)" { cmake --preset $preset }
Invoke-Step "Build native" { cmake --build --preset "$preset-release" }
if ($Test) {
    # Native test binaries need a host that can run them: an x64 host cannot run ARM64 code, while
    # Windows on ARM runs x64 code under emulation.
    $hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($Runtime -eq "win-$hostArch" -or ($Runtime -eq 'win-x64' -and $hostArch -eq 'arm64')) {
        Invoke-Step "Native tests" { ctest --preset $preset }
    } else {
        Write-Host "Skipping native tests: $Runtime binaries cannot run on this $hostArch host" -ForegroundColor Yellow
    }
}

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
    # Velopack refuses to pack a version that is already in the releases folder. Local builds of an
    # unchanged version replace the old output; real releases bump <Version> (and CI publishes them).
    $index = Join-Path $releases 'releases.win.json'
    if ((Test-Path $index) -and ((Get-Content $index -Raw | ConvertFrom-Json).Assets.Version -contains $version)) {
        Write-Host "Replacing the existing $version build in $releases" -ForegroundColor Yellow
        Remove-Item -Recurse -Force $releases
    }
    Invoke-Step "Publish for packaging ($Runtime)" { dotnet publish src/EmmaSpace.App @common -o $folder }
    Invoke-Step "Restore vpk" { dotnet tool restore }
    Invoke-Step "Velopack pack $version" {
        dotnet vpk pack --packId EmmaSpace --packVersion $version --packDir $folder --mainExe EmmaSpace.exe `
            --packTitle "Emma Space" --packAuthors "Emma Space" --icon src/EmmaSpace.App/Assets/emmaspace.ico `
            --runtime $Runtime --outputDir $releases
    }
    Write-Host "Installer and update packages in $releases" -ForegroundColor Green
}
