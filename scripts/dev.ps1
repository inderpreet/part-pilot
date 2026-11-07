<#
.SYNOPSIS
  Day-to-day developer commands for Inventory Tracker.

.DESCRIPTION
  One entry point for running, testing and maintaining the project. Works in Windows PowerShell 5.1 and PowerShell 7+.

  Tasks:
    run       Run the app (default).              dotnet run
    watch     Run with hot reload / auto-restart. dotnet watch run
    test      Run all tests.                      dotnet test
    build     Build the solution (warnings are errors).
    migrate   Add an EF Core migration (needs -Name).
    reset-db  Delete the dev database (+ WAL/SHM files).
    help      Show this help.

  For run/watch the dev database lives in <repo>\.data\inventory.db (git-ignored), so it survives rebuilds and
  `dotnet clean`. It is only defaulted when you have not chosen a database yourself with -DbPath, the INVENTORY_DB
  environment variable, or INVENTORY_DB in a .env file.

.PARAMETER Task
  Which task to run (see above). Default: run.

.PARAMETER Lan
  Shortcut for -Bind all: listen on every network interface so tablets/other PCs can connect.

.PARAMETER Bind
  localhost (default) | all | a specific IP address. Maps to --bind / INVENTORY_BIND.

.PARAMETER Port
  TCP port. Maps to --port / INVENTORY_PORT (app default: 5080).

.PARAMETER DbPath
  Path to the SQLite file. Maps to --db / INVENTORY_DB.

.PARAMETER Open
  Open the default browser once the server has started (--open).

.PARAMETER Name
  Migration name for the migrate task, e.g. -Name AddProducts.

.PARAMETER Filter
  Test filter for the test task, e.g. -Filter "FullyQualifiedName~Settings".

.PARAMETER Force
  Skip the confirmation prompt for reset-db.

.EXAMPLE
  .\scripts\dev.ps1                          # run on http://localhost:5080
.EXAMPLE
  .\scripts\dev.ps1 run -Lan -Open           # reachable from the LAN, open the browser
.EXAMPLE
  .\scripts\dev.ps1 watch -Port 6000
.EXAMPLE
  .\scripts\dev.ps1 test -Filter "FullyQualifiedName~Settings"
.EXAMPLE
  .\scripts\dev.ps1 migrate -Name AddProducts
.EXAMPLE
  .\scripts\dev.ps1 reset-db -Force
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('run', 'watch', 'test', 'build', 'migrate', 'reset-db', 'help')]
    [string]$Task = 'run',

    [switch]$Lan,
    [string]$Bind,
    [ValidateRange(1, 65535)]
    [int]$Port,
    [string]$DbPath,
    [switch]$Open,

    [string]$Name,
    [string]$Filter,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\InventoryTracker'
$DataDir = Join-Path $Root '.data'
$DefaultDb = Join-Path $DataDir 'inventory.db'

$CallerDir = (Get-Location).Path

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# Relative paths are relative to where the script was invoked, not the repo root.
function Resolve-UserPath([string]$Path) {
    [IO.Path]::GetFullPath([IO.Path]::Combine($CallerDir, $Path))
}

function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$DotnetArgs)
    Write-Host "dotnet $($DotnetArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Test-DbConfigured {
    # Has the user already chosen a database somewhere other than this script?
    if ($DbPath -or $env:INVENTORY_DB -or $env:ConnectionStrings__Default) { return $true }
    foreach ($dir in @($CallerDir, $Root)) {
        $envFile = Join-Path $dir '.env'
        if ((Test-Path $envFile) -and (Select-String -Path $envFile -Pattern '^\s*(export\s+)?(INVENTORY_DB|ConnectionStrings__Default)\s*=' -Quiet)) {
            return $true
        }
    }
    return $false
}

function Get-AppArgs {
    $appArgs = @()
    $bindValue = if ($Lan) { 'all' } else { $Bind }
    if ($bindValue) { $appArgs += @('--bind', $bindValue) }
    if ($Port) { $appArgs += @('--port', "$Port") }
    if ($Open) { $appArgs += '--open' }

    if ($DbPath) {
        $appArgs += @('--db', (Resolve-UserPath $DbPath))
    }
    elseif (-not (Test-DbConfigured)) {
        New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
        $appArgs += @('--db', $DefaultDb)
    }
    return , $appArgs
}

function Show-LanHint([string[]]$AppArgs) {
    $i = [array]::IndexOf($AppArgs, '--bind')
    $bindValue = if ($i -ge 0) { $AppArgs[$i + 1] } elseif ($env:INVENTORY_BIND) { $env:INVENTORY_BIND } else { 'localhost' }
    if ($bindValue -notin @('all', 'lan', '0.0.0.0', '*')) { return }

    $p = [array]::IndexOf($AppArgs, '--port')
    $portValue = if ($p -ge 0) { $AppArgs[$p + 1] } elseif ($env:INVENTORY_PORT) { $env:INVENTORY_PORT } else { '5080' }

    Write-Host ''
    Write-Host 'Listening on all interfaces (plain HTTP). Other devices can try:' -ForegroundColor Yellow
    try {
        Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -notmatch '^127\.|^169\.254\.' } |
            ForEach-Object { Write-Host "  http://$($_.IPAddress):$portValue" -ForegroundColor Yellow }
    }
    catch { Write-Host "  http://<this-machine-ip>:$portValue" -ForegroundColor Yellow }
    Write-Host "If they cannot connect, allow the port in Windows Firewall (inbound TCP $portValue)." -ForegroundColor DarkYellow
    Write-Host ''
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install .NET 10 SDK from https://dotnet.microsoft.com/download'
}

Push-Location $Root
try {
    switch ($Task) {
        'help' {
            Get-Help $PSCommandPath -Detailed
        }

        'build' {
            Write-Step 'Building solution'
            Invoke-Dotnet build
        }

        'test' {
            Write-Step 'Running tests'
            $testArgs = @('test')
            if ($Filter) { $testArgs += @('--filter', $Filter) }
            Invoke-Dotnet @testArgs
        }

        'run' {
            $appArgs = Get-AppArgs
            Write-Step 'Running Inventory Tracker (Ctrl+C to stop)'
            Show-LanHint $appArgs
            Invoke-Dotnet run --project $Project -- @appArgs
        }

        'watch' {
            $appArgs = Get-AppArgs
            Write-Step 'Running with hot reload (Ctrl+C to stop)'
            Show-LanHint $appArgs
            Invoke-Dotnet watch --project $Project run -- @appArgs
        }

        'migrate' {
            if (-not $Name) { throw 'Specify a migration name: .\scripts\dev.ps1 migrate -Name AddProducts' }
            Write-Step "Adding migration '$Name'"
            Invoke-Dotnet tool restore
            Invoke-Dotnet ef migrations add $Name --project $Project --output-dir Data/Migrations
            Write-Host 'Remember: update the work item/docs, and verify the migration applies to an empty DB and the previous schema.' -ForegroundColor DarkYellow
        }

        'reset-db' {
            $target = if ($DbPath) { Resolve-UserPath $DbPath } else { $DefaultDb }
            $files = @($target, "$target-wal", "$target-shm") | Where-Object { Test-Path -LiteralPath $_ }
            if (-not $files) { Write-Host "Nothing to delete: no database at $target"; break }

            Write-Host "This will delete:" -ForegroundColor Yellow
            $files | ForEach-Object { Write-Host "  $_" }
            if (-not $Force) {
                $answer = Read-Host 'Type "yes" to continue'
                if ($answer -ne 'yes') { Write-Host 'Cancelled.'; break }
            }
            $files | ForEach-Object { Remove-Item -LiteralPath $_ -Force }
            Write-Host 'Database deleted. It is recreated (migrated + seeded) on next start.' -ForegroundColor Green
        }
    }
}
finally {
    Pop-Location
}
