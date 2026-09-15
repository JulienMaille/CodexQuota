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
$running = Get-Process CodexQuota -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process CodexQuota -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $SkipPublish) {
    foreach ($t in $Targets) {
        Write-Host "=== Publishing $($t.Arch) ==="
        $publishArgs = @($Project, '-c', 'Release', "-p:PublishProfile=$($t.Profile)", '--nologo')
        if ($Version) { $publishArgs += "-p:Version=$Version" }
        & $DotNet publish @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "publish $($t.Arch) failed" }
    }
} else {
    foreach ($t in $Targets) {
        $exePath = Join-Path $Repo "src\CodexQuota.App\bin\Release\net9.0-windows10.0.19041.0\$($t.Rid)\publish\CodexQuota.exe"
        if ((Test-Path $exePath) -and (Test-Path $Project) -and ((Get-Item $Project).LastWriteTime -gt (Get-Item $exePath).LastWriteTime)) {
            Write-Warning "Skipping publish but exe is older than source for $($t.Arch): $exePath"
        }
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
        "/DPublishDir=`"$publishDir`"",
        "/DOutputDir=`"$Artifacts`"",
        "/DTargetArch=$($t.Arch)"
    )
    if ($Version) { $args += "/DMyAppVersion=$Version" }
    Write-Host "=== Stamping $($t.Arch) installer ==="
    & $Iscc @args
    if ($LASTEXITCODE -ne 0) { throw "ISCC $($t.Arch) failed" }
}

Write-Host ''
Write-Host '=== Installers ==='
$installers = Get-ChildItem $Artifacts -Filter 'CodexQuotaSetup-*.exe' | Sort-Object Name
if (-not $installers) { throw "no installers produced in $Artifacts" }
$installers | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1} bytes  sha256:{2}' -f $_.Name, $_.Length, $hash
}