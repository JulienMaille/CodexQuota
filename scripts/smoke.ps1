# Boot smoke test for a published CodexQuota: relaunches the exe, waits, then reports the
# process state, the last log lines, and any ERROR/WARN/Exception. Non-zero exit on error log.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1 [-Arch x64|arm64] [-WaitSeconds 45]

param(
    [string]$Arch = 'x64',
    [int]$WaitSeconds = 45,
    [string]$Log = "$env:LOCALAPPDATA\Temp\CodexQuota.log"
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot "..\src\CodexQuota.App\bin\Release\net9.0-windows10.0.19041.0\win-$Arch\publish\CodexQuota.exe"
if (-not (Test-Path $exe)) { throw "exe not found: $exe" }

Get-Process CodexQuota -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$before = if (Test-Path $Log) { (Get-Item $Log).Length } else { 0 }
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $WaitSeconds

if ($proc.HasExited) {
    'DEAD - process exited'
    exit 1
} else {
    "ALIVE pid=$($proc.Id) started=$($proc.StartTime)"
}

if (Test-Path $Log) {
    Write-Host '--- NEW LOG LINES ---'
    $fs = [System.IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
    try {
        $fs.Seek([Math]::Min($before, $fs.Length), 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($fs)
        $lines = $reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ }
        $reader.Close()
    } finally {
        $fs.Close()
    }
    $lines | Select-Object -Last 15 | ForEach-Object { $_ }

    $errors = $lines | Select-String -Pattern 'ERROR|WARN|Exception' | Select-Object -Last 5
    if ($errors) {
        Write-Host '--- ERRORS ---'
        $errors | ForEach-Object { $_.Line }
        exit 1
    }
}
exit 0