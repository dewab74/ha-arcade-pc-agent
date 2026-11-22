# Builds artifacts\installer\ArcadePcAgent-Setup-<Version>.exe with Inno Setup 6
# from the published exe (dotnet publish src/ArcadePcAgent -c Release -o artifacts/publish).
#   pwsh scripts/build-installer.ps1 -Version 0.5.0
# Windows only. Installs Inno Setup with Chocolatey when it is missing and
# CI is set (GitHub's Windows runners have Chocolatey).
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
if (-not (Test-Path (Join-Path $publish 'ArcadePcAgent.exe'))) { throw "No ArcadePcAgent.exe in $publish; run dotnet publish first" }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    if (-not $env:CI) { throw 'Inno Setup 6 (ISCC.exe) not found; install it from https://jrsoftware.org/isinfo.php' }
    choco install innosetup -y --no-progress | Out-Null
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { throw "Inno Setup install did not produce $iscc" }
}

& $iscc /Q "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root 'installer\ArcadePcAgent.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC exited $LASTEXITCODE" }
$out = Join-Path $root "artifacts\installer\ArcadePcAgent-Setup-$Version.exe"
if (-not (Test-Path $out)) { throw "Installer not found at $out" }
Write-Output "Built $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
