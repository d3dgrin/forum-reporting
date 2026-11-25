# Repeatable end-to-end demo of the reporting feature.
# Prerequisites: docker compose stack up, and the three services running (scripts/start-services.ps1).
#   -SkipReset   keep existing data (default: reset demo data first)
param([switch]$SkipReset)

$ErrorActionPreference = 'Stop'
$reporting = 'http://localhost:5103'
$account   = 'http://localhost:5102'

function Write-Step($text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }

# Calls an endpoint and returns @{ Status; Body } without throwing on 4xx/5xx (works on PowerShell 5.1 and 7).
function Invoke-Api([string]$Method, [string]$Url, [string]$UserId, $Body) {
    $headers = @{}
    if ($UserId) { $headers['X-Demo-UserId'] = $UserId }
    $json = if ($null -ne $Body) { $Body | ConvertTo-Json -Compress } else { $null }
    try {
        $r = Invoke-WebRequest -Method $Method -Uri $Url -Headers $headers -Body $json -ContentType 'application/json' -UseBasicParsing
        return @{ Status = [int]$r.StatusCode; Body = $r.Content }
    }
    catch {
        $err = $_
        $response = $err.Exception.Response
        if ($null -eq $response) { throw }
        # PS7: body is in ErrorDetails (Response is an HttpResponseMessage). PS 5.1: usually ErrorDetails too, else read the stream.
        $content = $err.ErrorDetails.Message
        if (-not $content -and $response -is [System.Net.WebResponse]) {
            try { $content = (New-Object System.IO.StreamReader($response.GetResponseStream())).ReadToEnd() } catch { }
        }
        return @{ Status = [int]$response.StatusCode; Body = $content }
    }
}

function Submit-Report([string]$Reporter, [string]$PostId, [string]$Reason = 'Harassment') {
    $r = Invoke-Api POST "$reporting/api/reports" $Reporter @{ postId = $PostId; reason = $Reason }
    $color = if ($r.Status -lt 300) { 'Green' } else { 'Yellow' }
    Write-Host ("{0,-11} reports {1,-9} -> {2}  {3}" -f ($(if ($Reporter) { $Reporter } else { '(anon)' })), $PostId, $r.Status, $r.Body) -ForegroundColor $color
    return $r
}

function Show-State {
    $summary = (Invoke-Api GET "$reporting/api/reports/users/target-1/summary" 'support-demo').Body
    $status  = (Invoke-Api GET "$account/api/accounts/target-1/status" $null).Body
    Write-Host "Reporting summary : $summary"
    Write-Host "Account status    : $status"
}

if (-not $SkipReset) {
    Write-Step 'Reset demo data (reporting_db, account_db, queue)'
    docker exec forum-reporting-postgres-1 psql -q -U forum -d reporting_db -c "TRUNCATE reports, reported_user_counters, outbox_messages;" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Reset of reporting_db failed (is the docker compose stack up?)' }
    docker exec forum-reporting-postgres-1 psql -q -U forum -d account_db -c "TRUNCATE processed_messages; UPDATE accounts SET status='Active', blocked_at=NULL, block_reason=NULL;" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Reset of account_db failed (has Account.Api run its migrations?)' }
    foreach ($queue in 'account.user-block-requested', 'account.user-block-requested.dlq') {
        docker exec forum-reporting-rabbitmq-1 rabbitmqctl -q purge_queue $queue | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Purging $queue failed" }
    }
    Write-Host 'Done.'
}

Write-Step 'Initial state of target-1 (author of post-1, post-2, post-3)'
Show-State

Write-Step 'Rejected requests'
Submit-Report $null        'post-1' | Out-Null   # 401: not logged in
Submit-Report 'reporter-1' 'post-11' | Out-Null  # 422: post-11 is reporter-1's own post
Submit-Report 'reporter-1' 'post-999' | Out-Null # 404: unknown post
Submit-Report 'reporter-1' 'post-1' '' | Out-Null # 400: reason required

Write-Step 'Four distinct reporters'
1..4 | ForEach-Object { Submit-Report "reporter-$_" "post-$((($_ - 1) % 3) + 1)" | Out-Null }

Write-Step 'Duplicate: reporter-1 reports a DIFFERENT post by the same author'
Submit-Report 'reporter-1' 'post-3' 'Spam, again' | Out-Null
Show-State

Write-Step 'Fifth distinct reporter crosses the threshold'
Submit-Report 'reporter-5' 'post-2' | Out-Null

Write-Step 'Waiting for the asynchronous block (outbox -> RabbitMQ -> Account.Api)'
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Milliseconds 250
    try { $status = (Invoke-Api GET "$account/api/accounts/target-1/status" $null).Body | ConvertFrom-Json }
    catch { $status = $null } # Account.Api not reachable: keep waiting, report below
} while ($status.status -ne 'Blocked' -and $sw.Elapsed.TotalSeconds -lt 30)

if ($status.status -eq 'Blocked') {
    Write-Host ("target-1 is BLOCKED after {0:N1} s (eventual consistency; 'BlockPending' until then)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
} else {
    Write-Host 'target-1 was not blocked within 30 s - is Account.Api running?' -ForegroundColor Red
}
Show-State
