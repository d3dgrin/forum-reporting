# Starts Posts.Api (5101), Account.Api (5102) and Reporting.Api (5103), each in its own window.
# Prerequisite: docker compose -f deploy/docker-compose.yml up -d
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'ForumReporting.slnx') -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

foreach ($service in 'Posts.Api', 'Account.Api', 'Reporting.Api') {
    $project = Join-Path $root "src/Services/$service"
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = '$service'; dotnet run --no-build --project '$project'"
}

Write-Host 'Waiting for health endpoints...'
foreach ($port in 5101, 5102, 5103) {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try { Invoke-RestMethod "http://localhost:$port/health" | Out-Null; Write-Host "  :$port healthy"; break }
        catch { if ((Get-Date) -gt $deadline) { throw "Service on :$port did not become healthy" }; Start-Sleep -Milliseconds 500 }
    }
}
Write-Host 'All services are up. Run scripts/demo.ps1'
