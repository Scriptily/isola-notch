# Checks that GitHub Actions can still reach the Microsoft Store, without
# submitting anything: signs in to Partner Center with the repository's Entra
# app secret and reads the listing. Run by .github/workflows/store-check.yml
# every month and by release.yml before it submits.
#
# A client secret can't renew itself, so when it's within -WarnDays of
# STORE_SECRET_EXPIRES, or sign-in fails, this opens a GitHub issue with the
# renewal steps (GitHub emails the owner). Once the check passes again, the
# issue is closed.
param(
    [int]$WarnDays = 60,
    [int]$PretendDaysLeft = -1   # tests the reminder without waiting two years
)
$ErrorActionPreference = 'Stop'

$title = 'Renew the Microsoft Store client secret'
$repo = $env:GITHUB_REPOSITORY

function Get-ReminderIssue {
    $open = gh issue list --repo $repo --state open --limit 100 --json number,title | ConvertFrom-Json
    $open | Where-Object { $_.title -eq $title } | Select-Object -First 1
}

$problem = $null
foreach ($name in 'TENANT', 'CLIENT', 'SECRET', 'PRODUCT', 'EXPIRES') {
    if (-not (Get-Item "env:$name" -ErrorAction SilentlyContinue).Value) { $problem = "$name isn't set" }
}

$daysLeft = $null
if (-not $problem) {
    $expires = [datetime]::ParseExact($env:EXPIRES, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)
    $daysLeft = if ($PretendDaysLeft -ge 0) { $PretendDaysLeft } else { [int]($expires - [datetime]::UtcNow.Date).TotalDays }
    "Client secret expires $($env:EXPIRES): $daysLeft days left"

    try {
        $token = Invoke-RestMethod -Method Post "https://login.microsoftonline.com/$env:TENANT/oauth2/token" -Body @{
            grant_type    = 'client_credentials'
            client_id     = $env:CLIENT
            client_secret = $env:SECRET
            resource      = 'https://manage.devcenter.microsoft.com'
        }
        $app = Invoke-RestMethod "https://manage.devcenter.microsoft.com/v1.0/my/applications/$env:PRODUCT" -Headers @{ Authorization = "Bearer $($token.access_token)" }
        "Signed in to Partner Center; can manage '$($app.primaryName)' ($($app.id))"
        if ($app.pendingApplicationSubmission) { "Note: a submission is already in progress ($($app.pendingApplicationSubmission.id))" }
    } catch {
        $detail = "$($_.ErrorDetails.Message) $($_.Exception.Message)"
        $problem = switch -Regex ($detail) {
            'AADSTS7000222' { 'the client secret has expired'; break }
            'AADSTS7000215' { 'the client secret is wrong'; break }
            'AADSTS700016'  { 'the app "Isola releases" is missing from the directory'; break }
            '401|403'       { 'Partner Center refused the app: check it still has the Manager role under Microsoft Entra applications'; break }
            default         { "sign-in failed: $detail" }
        }
    }
}

$issue = Get-ReminderIssue
if ($problem -or $daysLeft -le $WarnDays) {
    $why = if ($problem) { "The monthly check failed: $problem. Releases can't reach the Store until this is fixed." }
           else { "The secret GitHub uses to submit releases to the Microsoft Store expires on $($env:EXPIRES) ($daysLeft days). After that, releases still reach GitHub but not the Store." }
    $next = [datetime]::UtcNow.Date.AddMonths(24).ToString('yyyy-MM-dd')
    $body = @"
$why

About five minutes:

1. Sign in at https://portal.azure.com with the directory's admin account (``admin@...onmicrosoft.com``). Open **App registrations** -> **Isola releases** -> **Certificates & secrets** -> **New client secret**, choose **24 months**, and copy the **Value**.
2. In a terminal, store it (it asks for the value without showing it), and the new expiry date:
   ``````
   gh secret set AZURE_AD_APPLICATION_SECRET --repo $repo
   gh variable set STORE_SECRET_EXPIRES --repo $repo --body $next
   ``````
3. Run the check: ``gh workflow run store-check.yml --repo $repo``. When it passes, it closes this issue.
4. Back in **Certificates & secrets**, delete the old secret.
"@
    if ($issue) {
        gh issue comment $issue.number --repo $repo --body $why | Out-Null
        "Reminder issue #$($issue.number) updated"
    } else {
        $url = gh issue create --repo $repo --title $title --body $body
        "Reminder issue opened: $url"
    }
    if ($problem) { throw $problem }
} elseif ($issue) {
    gh issue close $issue.number --repo $repo --comment "The Store check passes again; the secret now expires $($env:EXPIRES)." | Out-Null
    "Reminder issue #$($issue.number) closed"
}
