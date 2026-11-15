<#
.SYNOPSIS
  Builds dist\IsolaSetup.exe: tests, NativeAOT island, zipped payload,
  NativeAOT installer around it. With -Release, publishes it as a GitHub release
  the installed island's updater will pick up.

.EXAMPLE
  .\build.ps1                 # build and test only
  .\build.ps1 -Release        # ...then tag v<VERSION> and upload

  Bump VERSION first for a release: the updater only installs a higher version.
#>
param([switch]$Release, [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = (Get-Content -Raw VERSION).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must be x.y.z, got '$version'" }
$repo = "Scriptily/isola-notch"
$tag = "v$version"

# NativeAOT links with MSVC and finds it through vswhere, which the Build Tools
# installer puts here but never on PATH.
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH += ";$vsInstaller" }

function Step($what, [scriptblock]$run) {
    Write-Host "== $what" -ForegroundColor Cyan
    # Native tools write progress and "not found" to stderr, which Windows
    # PowerShell turns into a fatal error under 'Stop'. Judge them by exit code.
    $ErrorActionPreference = 'Continue'
    & $run
    if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" }
}

if ($Release) {
    # refuse before spending minutes on a build that can't be released
    & { $ErrorActionPreference = 'Continue'; gh release view $tag --repo $repo *> $null }
    if ($LASTEXITCODE -eq 0) { throw "$tag already exists: bump VERSION" }
    $global:LASTEXITCODE = 0
}

if (-not $SkipTests) { Step "tests" { dotnet run -c Release --project tests\Isola.Tests.csproj } }

# Fresh output folders each time: nothing from an older build can end up in the payload.
$out = Join-Path $PSScriptRoot "publish\$version-$(Get-Date -Format yyyyMMddHHmmss)"
$app = Join-Path $out 'app'
$payload = Join-Path $out 'payload.zip'
Step "island $version (NativeAOT)" { dotnet publish Isola.csproj -c Release -p:Aot=true -p:Version=$version -o $app -nologo -v q }

# Only what the island needs at run time: no debug symbols.
Get-ChildItem $app -Recurse -Include *.pdb, *.dbg | Remove-Item
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $payload
Write-Host ("   payload {0:0.0} MB" -f ((Get-Item $payload).Length / 1MB))

Step "installer (NativeAOT)" { dotnet publish installer\IsolaSetup.csproj -c Release -p:Version=$version "-p:PayloadZip=$payload" -o (Join-Path $out 'setup') -nologo -v q }

New-Item -ItemType Directory -Force dist | Out-Null
$setup = Join-Path $PSScriptRoot 'dist\IsolaSetup.exe'
Copy-Item (Join-Path $out 'setup\IsolaSetup.exe') $setup -Force
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path 'dist\SHA256.txt' -Value "$sha  IsolaSetup.exe" -Encoding ascii
Write-Host ("   dist\IsolaSetup.exe  {0:0.0} MB  sha256 {1}" -f ((Get-Item $setup).Length / 1MB), $sha) -ForegroundColor Green

if ($Release) {
    # The notes are this version's section of CHANGELOG.md, then the sha256
    # line the updater checks the download against.
    $log = Get-Content (Join-Path $PSScriptRoot 'CHANGELOG.md') -Raw
    $m = [regex]::Match($log, "(?ms)^## \[$([regex]::Escape($version))\][^\n]*\n(.*?)(?=^## \[|\z)")
    if (-not $m.Success) { throw "CHANGELOG.md has no section for $version" }
    $notes = $m.Groups[1].Value.Trim() + "`n`n---`nDownload **IsolaSetup.exe** below and run it (per-user, no administrator rights). Isola updates itself after that.`n`nsha256: $sha"
    $notesFile = Join-Path $out 'notes.md'; Set-Content $notesFile $notes -Encoding utf8
    Step "GitHub release $tag" { gh release create $tag $setup --repo $repo --title "Isola $version" --notes-file $notesFile --target main }
}
