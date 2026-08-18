# Rebuilds both trimmed publishes and stamps the Inno Setup installers into artifacts/.
# Idempotent, re-runnable; stops a running CodexQuota first (the instance locks publish files).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/build-installers.ps1 [-DotNet <path>] [-Version <semver>] [-SkipPublish]
#
# Defaults match this dev machine; CI (release.yml) does not use this script.

param(
    [string]$DotNet = "C:\Dev\.dotnet\dotnet.exe",
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Version = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$Iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
$Artifacts = Join-Path $Repo 'artifacts'
$Project = Join-Path $Repo 'src\CodexQuota.App\CodexQuota.App.csproj'
$Targets = @(
    @{ Arch = 'x64';   Rid = 'win-x64';   Profile = 'win-x64' },
    @{ Arch = 'arm64'; Rid = 'win-arm64'; Profile = 'win-arm64' }
)

if (-not (Test-Path $DotNet)) { throw "dotnet not found at $DotNet" }
if (-not (Test-Path $Iscc))   { throw "Inno Setup not found at $Iscc" }

# A running instance locks publish output; the app is single-instance, so stop it before rebuild.
Get-Process CodexQuota -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

if (-not $SkipPublish) {
    foreach ($t in $Targets) {
        Write-Host "=== Publishing $($t.Arch) ==="
        & $DotNet publish $Project -c Release -p:PublishProfile=$($t.Profile) --nologo
        if ($LASTEXITCODE -ne 0) { throw "publish $($t.Arch) failed" }
    }
}

New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
foreach ($t in $Targets) {
    $publishDir = Join-Path $Repo "src\CodexQuota.App\bin\Release\net9.0-windows10.0.19041.0\$($t.Rid)\publish"
    if (-not (Test-Path (Join-Path $publishDir 'CodexQuota.exe'))) {
        throw "publish output missing for $($t.Arch): $publishDir"
    }
    $args = @(
        (Join-Path $Repo 'installer\CodexQuota.iss'),
        "/DPublishDir=$publishDir",
        "/DOutputDir=$Artifacts",
        "/DTargetArch=$($t.Arch)"
    )
    if ($Version) { $args += "/DMyAppVersion=$Version" }
    Write-Host "=== Stamping $($t.Arch) installer ==="
    & $Iscc @args
    if ($LASTEXITCODE -ne 0) { throw "ISCC $($t.Arch) failed" }
}

Write-Host ''
Write-Host '=== Installers ==='
Get-ChildItem $Artifacts -Filter 'CodexQuotaSetup-*.exe' | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1} bytes  sha256:{2}' -f $_.Name, $_.Length, $hash
}