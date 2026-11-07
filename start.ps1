<#
.SYNOPSIS
  Start Inventory Tracker and open it in your browser.

.DESCRIPTION
  Builds (if needed) and runs the app, then opens http://localhost:5080. This is a thin front door over
  scripts\dev.ps1 (which has more tasks: test, build, migrate, reset-db ...). Press Ctrl+C to stop.

  Data is stored in .\.data\inventory.db unless you set INVENTORY_DB (environment or .env) or pass -DbPath.

.PARAMETER Lan
  Listen on every network interface so tablets / other PCs can connect (default: this machine only).

.PARAMETER Bind
  localhost | all | a specific IP address.

.PARAMETER Port
  TCP port (default 5080).

.PARAMETER DbPath
  Path to the SQLite database file.

.PARAMETER NoOpen
  Do not open the browser.

.PARAMETER Watch
  Restart automatically when source files change (dotnet watch).

.EXAMPLE
  .\start.ps1
.EXAMPLE
  .\start.ps1 -Lan
.EXAMPLE
  .\start.ps1 -Port 6000 -NoOpen
#>
[CmdletBinding()]
param(
    [switch]$Lan,
    [string]$Bind,
    [ValidateRange(1, 65535)]
    [int]$Port,
    [string]$DbPath,
    [switch]$NoOpen,
    [switch]$Watch
)

$ErrorActionPreference = 'Stop'
$dev = Join-Path $PSScriptRoot 'scripts\dev.ps1'

# Forward only what the caller actually specified (named-parameter splatting needs a hashtable).
$forward = @{}
foreach ($key in $PSBoundParameters.Keys) {
    if ($key -notin 'NoOpen', 'Watch') { $forward[$key] = $PSBoundParameters[$key] }
}
if (-not $NoOpen) { $forward['Open'] = $true }

$task = if ($Watch) { 'watch' } else { 'run' }
& $dev $task @forward
exit $LASTEXITCODE
