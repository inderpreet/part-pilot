<#
.SYNOPSIS
  Publish Inventory Tracker as a single self-contained executable.

.DESCRIPTION
  Produces publish\<rid>\PartPilot[.exe]: one file, no .NET install needed, static assets embedded.
  Supported runtime identifiers: win-x64, linux-x64, linux-arm64, osx-arm64.

.PARAMETER Runtime
  Target runtime identifier. Defaults to the machine you are on.

.PARAMETER All
  Publish every supported runtime identifier.

.PARAMETER Smoke
  After publishing, run scripts\smoke.ps1 against the result. Only possible when the target matches this machine;
  other targets are published but reported as "not smoke-tested here".

.PARAMETER Configuration
  Build configuration (default Release).

.EXAMPLE
  .\scripts\publish.ps1 -Smoke
.EXAMPLE
  .\scripts\publish.ps1 -Runtime linux-x64
.EXAMPLE
  .\scripts\publish.ps1 -All
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string]$Runtime,
    [switch]$All,
    [switch]$Smoke,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\InventoryTracker\InventoryTracker.csproj'
$AllRuntimes = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')

function Get-HostRuntime {
    $arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $rt = [Runtime.InteropServices.RuntimeInformation]
    $os = [Runtime.InteropServices.OSPlatform]
    if ($rt::IsOSPlatform($os::Windows) -and $arch -eq 'x64') { return 'win-x64' }
    if ($rt::IsOSPlatform($os::Linux) -and $arch -eq 'x64') { return 'linux-x64' }
    if ($rt::IsOSPlatform($os::Linux) -and $arch -eq 'arm64') { return 'linux-arm64' }
    if ($rt::IsOSPlatform($os::OSX) -and $arch -eq 'arm64') { return 'osx-arm64' }
    return $null
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}

$hostRuntime = Get-HostRuntime
$targets = if ($All) { $AllRuntimes } elseif ($Runtime) { @($Runtime) } elseif ($hostRuntime) { @($hostRuntime) } else { throw 'Could not detect this platform; pass -Runtime.' }

$results = @()
foreach ($rid in $targets) {
    $out = Join-Path $Root "publish\$rid"
    $exeName = if ($rid -like 'win-*') { 'PartPilot.exe' } else { 'PartPilot' }
    $exe = Join-Path $out $exeName

    Write-Host "==> Publishing $rid" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

    & dotnet publish $Project -c $Configuration -r $rid -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }

    # One file only: no wwwroot, .dll, native sqlite, .pdb or config files left beside the executable.
    $files = @(Get-ChildItem -LiteralPath $out -Recurse -File)
    $extra = @($files | Where-Object { $_.Name -ne $exeName })
    if (-not (Test-Path -LiteralPath $exe)) { throw "Expected $exe was not produced." }
    if ($extra.Count -gt 0) {
        Write-Warning "Unexpected extra files next to the executable:"
        $extra | ForEach-Object { Write-Warning "  $($_.FullName.Substring($out.Length + 1))" }
        throw "publish\$rid must contain exactly one file."
    }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
    Write-Host "    $exe  ($sizeMb MB)" -ForegroundColor Green

    $smokeResult = 'skipped'
    if ($Smoke) {
        if ($rid -eq $hostRuntime) {
            & (Join-Path $PSScriptRoot 'smoke.ps1') -Exe $exe
            if ($LASTEXITCODE -ne 0) { throw "Smoke test failed for $rid" }
            $smokeResult = 'passed'
        }
        else {
            $smokeResult = "not smoke-tested here (host is $hostRuntime)"
            Write-Host "    $smokeResult" -ForegroundColor DarkYellow
        }
    }
    $results += [pscustomobject]@{ Runtime = $rid; 'Size (MB)' = $sizeMb; Smoke = $smokeResult }
}

Write-Host ''
$results | Format-Table -AutoSize
