<#
.SYNOPSIS
  Smoke-test a published Inventory Tracker executable.

.DESCRIPTION
  Copies the executable ALONE into a fresh temp folder (proving it needs no other files), starts it on a free port
  with a temp database, checks /health, the embedded static assets and the first-run redirect, then stops it and
  deletes everything. Exit code 0 = pass.

.PARAMETER Exe
  Path to the published executable.

.PARAMETER TimeoutSeconds
  How long to wait for the app to become healthy (first start extracts native libraries, so allow a while).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path

$work = Join-Path ([IO.Path]::GetTempPath()) ("inventory-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()

$process = $null
$failures = New-Object System.Collections.Generic.List[string]

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name $Detail" -ForegroundColor Red; $failures.Add($Name) }
}

try {
    $copy = Join-Path $work (Split-Path -Leaf $Exe)
    Copy-Item -LiteralPath $Exe -Destination $copy
    if ($IsLinux -or $IsMacOS) { chmod +x $copy }

    $outLog = Join-Path $work 'stdout.log'
    $errLog = Join-Path $work 'stderr.log'
    $db = Join-Path $work 'smoke.db'

    Write-Host "Smoke test: $Exe (port $port)"
    $startArgs = @('--db', $db, '--port', "$port")
    $process = Start-Process -FilePath $copy -ArgumentList $startArgs -WorkingDirectory $work -PassThru `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog

    $base = "http://localhost:$port"
    $healthy = $false
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and -not $healthy) {
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 500
        try { $healthy = (Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
    }
    Check 'starts and /health returns 200' $healthy "(exited=$($process.HasExited))"

    if ($healthy) {
        $health = (Invoke-WebRequest "$base/health" -UseBasicParsing).Content | ConvertFrom-Json
        Check '/health reports db ok' ($health.db -eq 'ok') ($health | ConvertTo-Json -Compress)
        Check '/health reports a version' (-not [string]::IsNullOrWhiteSpace($health.version))

        foreach ($asset in '/lib/htmx.min.js', '/lib/pico.min.css', '/css/site.css', '/js/site.js') {
            $code = try { (Invoke-WebRequest "$base$asset" -UseBasicParsing -TimeoutSec 5).StatusCode } catch { 0 }
            Check "embedded asset $asset" ($code -eq 200) "(status $code)"
        }

        # The home page must render (200) or, once authentication exists, send a fresh install to first-run setup / login.
        # HttpClient (not Invoke-WebRequest): Windows PowerShell 5.1 cannot reliably report a 302 with -MaximumRedirection 0.
        $status = 0; $loc = ''
        Add-Type -AssemblyName System.Net.Http
        $handler = [System.Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
        $client = [System.Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds(10)
        try {
            $resp = $client.GetAsync("$base/").GetAwaiter().GetResult()
            $status = [int]$resp.StatusCode
            if ($resp.Headers.Location) { $loc = $resp.Headers.Location.ToString() }
        }
        catch { $loc = $_.Exception.Message }
        finally { $client.Dispose(); $handler.Dispose() }
        $homeOk = ($status -eq 200) -or ($status -in 301, 302, 303, 307, 308 -and $loc -match '/Account/(Setup|Login)')
        Check 'home page renders or redirects to setup/login' $homeOk "(status $status, Location: $loc)"

        Check 'database file created' (Test-Path -LiteralPath $db)
    }
    else {
        foreach ($log in $outLog, $errLog) {
            if (Test-Path $log) { Write-Host "--- $(Split-Path -Leaf $log)"; Get-Content $log -Tail 30 }
        }
    }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(5000) | Out-Null }
    Start-Sleep -Milliseconds 300
    # Best-effort cleanup (native library extraction may briefly hold files).
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Smoke test FAILED ($($failures.Count) check(s))" -ForegroundColor Red
    exit 1
}
Write-Host 'Smoke test passed' -ForegroundColor Green
exit 0
