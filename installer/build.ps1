# Publishes Rosetta and compiles installer\Rosetta-Setup.exe (requires Inno Setup 6).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }

dotnet publish "$root\src\Rosetta" -c Release -r win-x64 --self-contained -o "$PSScriptRoot\publish"
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

& $iscc "$PSScriptRoot\Rosetta.iss"
if ($LASTEXITCODE) { throw 'ISCC failed' }
Write-Host "Built $PSScriptRoot\Output\Rosetta-Setup.exe"
