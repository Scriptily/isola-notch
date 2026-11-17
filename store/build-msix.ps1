<#
.SYNOPSIS
  Builds the Microsoft Store package: dist\Isola-<version>.msix, with the same
  version as the GitHub release (VERSION, as x.y.z.0).

.DESCRIPTION
  The package is left unsigned: Partner Center signs it with Microsoft's
  certificate when it passes certification. To try it on this PC first, turn
  on Developer Mode (Settings -> System -> For developers) and register the
  unpacked layout instead:

    .\store\build-msix.ps1 -Register

.EXAMPLE
  .\store\build-msix.ps1            # dist\Isola-1.2.0.msix
  .\store\build-msix.ps1 -Register  # also install the layout on this PC (Developer Mode)
#>
param([switch]$Register, [switch]$SkipBuild, [switch]$SkipTiles)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = (Get-Content -Raw VERSION).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must be x.y.z, got '$version'" }
$id = Get-Content -Raw store\identity.json | ConvertFrom-Json

# NativeAOT links with MSVC through vswhere, which isn't on PATH by default
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH += ";$vsInstaller" }

$layout = Join-Path $root "publish\msix-$version"
if (-not $SkipBuild) {
    if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
    & { $ErrorActionPreference = 'Continue'; dotnet publish Isola.csproj -c Release -p:Aot=true -p:Version=$version -o $layout -nologo -v q }
    if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
    Get-ChildItem $layout -Recurse -Include *.pdb, *.dbg | Remove-Item
}

# tiles and logos, under names the manifest uses (Windows picks the scale). They're
# committed; redrawn from assets\island.png only when missing or asked for.
if (-not $SkipTiles -and -not (Test-Path store\assets\Square44x44Logo.png)) {
    python store\make_tiles.py | Out-Null
    if ($LASTEXITCODE) { throw "make_tiles.py failed (needs Python with Pillow)" }
}
$tiles = Join-Path $layout 'store'
New-Item -ItemType Directory -Force $tiles | Out-Null
Copy-Item store\assets\*.png $tiles -Force

$manifest = (Get-Content -Raw store\AppxManifest.xml).
    Replace('{{Name}}', $id.Name).
    Replace('{{Publisher}}', [System.Security.SecurityElement]::Escape($id.Publisher)).
    Replace('{{PublisherDisplayName}}', [System.Security.SecurityElement]::Escape($id.PublisherDisplayName)).
    Replace('{{Version}}', "$version.0")
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest)

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } | Sort-Object Name | Select-Object -Last 1
if (-not $sdkBin) { throw "makeappx.exe not found: install the Windows SDK" }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$makepri = Join-Path $sdkBin.FullName 'x64\makepri.exe'

# the resource index lets Windows pick the tile size for the screen's scaling
$priconfig = Join-Path $env:TEMP "isola-priconfig.xml"
& $makepri createconfig /cf $priconfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE) { throw "makepri createconfig failed ($LASTEXITCODE)" }
& $makepri new /pr $layout /cf $priconfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE) { throw "makepri new failed ($LASTEXITCODE)" }

if ($Register) {
    # the unpacked layout, registered in place: needs Developer Mode, no certificate
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceUpdateFromAnyVersion
    Write-Host "registered $($id.Name) $version.0 from $layout" -ForegroundColor Green
}

New-Item -ItemType Directory -Force dist | Out-Null
$msix = Join-Path $root "dist\Isola-$version.msix"
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE) { throw "makeappx failed ($LASTEXITCODE)" }
Write-Host ("dist\Isola-$version.msix  {0:0.0} MB" -f ((Get-Item $msix).Length / 1MB)) -ForegroundColor Green
