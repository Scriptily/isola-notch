<#
.SYNOPSIS
  Scans a release's IsolaSetup.exe on VirusTotal and records the result in
  CHANGELOG.md and the GitHub release notes, so anyone can check the download.

.DESCRIPTION
  Needs a VirusTotal API key (free account) in the VT_API_KEY environment
  variable. The key never goes on a command line or into the repository.

  build.ps1 -Release dot-sources this file and calls Invoke-VirusTotal and
  Add-ScanToChangelog before it publishes. Run it on its own to scan a release
  that already exists and update its notes:

.EXAMPLE
  .\tools\virustotal.ps1 -Version 1.0.0
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function New-VtClient {
    # setx stores it for new processes only; read the saved value too, so a
    # terminal opened before setx works without a restart
    $key = $env:VT_API_KEY
    if (-not $key) { $key = [Environment]::GetEnvironmentVariable('VT_API_KEY', 'User') }
    if (-not $key) { throw "VT_API_KEY isn't set: create a free VirusTotal account, copy the API key from your profile, and run: setx VT_API_KEY <key>  (then open a new terminal)" }
    $c = New-Object System.Net.Http.HttpClient
    $c.Timeout = [TimeSpan]::FromMinutes(5)
    $c.DefaultRequestHeaders.Add('x-apikey', $key)
    $c
}

function Get-VtJson($client, [string]$url) {
    # The free API allows 4 requests a minute: on 429, wait a minute and ask again.
    for ($try = 0; ; $try++) {
        $r = $client.GetAsync($url).GetAwaiter().GetResult()
        if ([int]$r.StatusCode -ne 429 -or $try -ge 5) { break }
        Write-Host "   rate limited; waiting a minute"
        Start-Sleep 61
    }
    if ([int]$r.StatusCode -eq 404) { return $null }
    $body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $r.IsSuccessStatusCode) { throw "VirusTotal $url answered $([int]$r.StatusCode): $body" }
    $body | ConvertFrom-Json
}

<#
  Uploads the file (unless VirusTotal already has it), waits for the analysis,
  and returns the link, the counts and which engines flagged it.
#>
function Invoke-VirusTotal([string]$File) {
    $client = New-VtClient
    $sha = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    $known = Get-VtJson $client "https://www.virustotal.com/api/v3/files/$sha"
    if (-not $known) {
        Write-Host "   uploading $(Split-Path $File -Leaf) to VirusTotal"
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $bytes = New-Object System.Net.Http.ByteArrayContent(, [IO.File]::ReadAllBytes($File))
        $form.Add($bytes, 'file', (Split-Path $File -Leaf))
        $r = $client.PostAsync('https://www.virustotal.com/api/v3/files', $form).GetAwaiter().GetResult()
        $body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $r.IsSuccessStatusCode) { throw "VirusTotal upload answered $([int]$r.StatusCode): $body" }
        $analysis = ($body | ConvertFrom-Json).data.id
        # Engines report over a few minutes; the free API allows 4 requests a minute.
        $t0 = Get-Date
        do {
            Start-Sleep 20
            $a = Get-VtJson $client "https://www.virustotal.com/api/v3/analyses/$analysis"
            Write-Host "   analysis $($a.data.attributes.status)"
        } while ($a.data.attributes.status -ne 'completed' -and ((Get-Date) - $t0).TotalMinutes -lt 15)
        if ($a.data.attributes.status -ne 'completed') { throw "VirusTotal didn't finish the analysis in 15 minutes; run tools\virustotal.ps1 -Version <x.y.z> later" }
        # The file's report can lag the finished analysis by a minute or two.
        $t0 = Get-Date
        do {
            Start-Sleep 20
            $known = Get-VtJson $client "https://www.virustotal.com/api/v3/files/$sha"
        } while (-not $known.data.attributes.last_analysis_results -and ((Get-Date) - $t0).TotalMinutes -lt 5)
    }
    if (-not $known.data.attributes.last_analysis_results) { throw "VirusTotal has no report for $sha yet; run tools\virustotal.ps1 -Version <x.y.z> later" }
    $attr = $known.data.attributes
    $s = $attr.last_analysis_stats
    $flagged = @($attr.last_analysis_results.PSObject.Properties | Where-Object { $_.Value.category -in 'malicious', 'suspicious' } |
        ForEach-Object { "$($_.Name): $($_.Value.result)" } | Sort-Object)
    [pscustomobject]@{
        Sha256   = $sha
        Link     = "https://www.virustotal.com/gui/file/$sha"
        Flagged  = [int]$s.malicious + [int]$s.suspicious
        Total    = [int]$s.malicious + [int]$s.suspicious + [int]$s.undetected + [int]$s.harmless
        Engines  = $flagged
        Date     = [DateTimeOffset]::FromUnixTimeSeconds([long]$attr.last_analysis_date).UtcDateTime.ToString('yyyy-MM-dd')
    }
}

<# The scan as a changelog subsection, plain enough to read without opening the link. #>
function Format-Scan($scan) {
    $lines = @("### VirusTotal", "")
    if ($scan.Flagged -eq 0) {
        $lines += "No security vendor flagged ``IsolaSetup.exe``: 0 of $($scan.Total) on $($scan.Date). [Full report]($($scan.Link))"
    } else {
        $lines += "$($scan.Flagged) of $($scan.Total) security vendors flagged ``IsolaSetup.exe`` on $($scan.Date). [Full report]($($scan.Link))"
        $lines += ""
        $lines += $scan.Engines | ForEach-Object { "- $_" }
        $lines += ""
        $lines += "The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers."
    }
    $lines -join "`n"
}

<# Puts (or replaces) the VirusTotal subsection at the end of this version's section. #>
function Add-ScanToChangelog([string]$Path, [string]$Version, $scan) {
    $log = [IO.File]::ReadAllText($Path)
    $m = [regex]::Match($log, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|\z)")
    if (-not $m.Success) { throw "CHANGELOG.md has no section for $Version" }
    $section = [regex]::Replace($m.Groups[1].Value, "(?ms)^### VirusTotal\r?\n.*?(?=^### |\z)", "").TrimEnd()
    $new = $log.Substring(0, $m.Groups[1].Index) + $section + "`n`n" + (Format-Scan $scan) + "`n`n" + $log.Substring($m.Groups[1].Index + $m.Groups[1].Length).TrimStart("`n")
    [IO.File]::WriteAllText($Path, $new.TrimEnd() + "`n")
}

if ($Version) {
    # An existing release: scan its published asset and refresh the notes.
    $root = Split-Path $PSScriptRoot -Parent
    $repo = "Scriptily/isola-notch"; $tag = "v$Version"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "isola-vt-$Version"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    gh release download $tag --repo $repo --pattern IsolaSetup.exe --dir $tmp --clobber
    if ($LASTEXITCODE) { throw "couldn't download $tag" }
    $scan = Invoke-VirusTotal (Join-Path $tmp 'IsolaSetup.exe')
    Add-ScanToChangelog (Join-Path $root 'CHANGELOG.md') $Version $scan
    $notes = gh release view $tag --repo $repo --json body --jq .body
    if ($LASTEXITCODE) { throw "couldn't read the notes of $tag" }
    $notes = [regex]::Replace(($notes -join "`n"), "(?ms)^### VirusTotal\r?\n.*?(?=^### |^---|\z)", "").TrimEnd()
    $parts = $notes -split "(?m)^---\s*$", 2
    $body = $parts[0].TrimEnd() + "`n`n" + (Format-Scan $scan) + "`n`n---" + $(if ($parts.Count -gt 1) { $parts[1] } else { "" })
    $file = Join-Path $tmp 'notes.md'; [IO.File]::WriteAllText($file, $body)
    gh release edit $tag --repo $repo --notes-file $file
    if ($LASTEXITCODE) { throw "couldn't update the notes of $tag" }
    Write-Host ("{0} of {1} flagged  {2}" -f $scan.Flagged, $scan.Total, $scan.Link) -ForegroundColor Green
    Write-Host "CHANGELOG.md updated: commit it."
}
