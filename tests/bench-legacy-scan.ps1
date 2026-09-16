# bench-legacy-scan.ps1 — 测量 CleanSuite v4.3 的真实扫描耗时与内存峰值（无头模式）
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$benchDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\bench'
New-Item -ItemType Directory -Force -Path $benchDir | Out-Null

$script = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理工具_v4.3\C盘清理工具\CleanSuite.ps1'
$outFile = Join-Path $benchDir 'legacy-scan-stdout.txt'
$errFile = Join-Path $benchDir 'legacy-scan-stderr.txt'

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath 'powershell.exe' `
  -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-ScanOnly') `
  -PassThru -WindowStyle Hidden -RedirectStandardOutput $outFile -RedirectStandardError $errFile

$peak = 0L
while (-not $p.HasExited) {
  try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
  Start-Sleep -Milliseconds 150
}
$p.WaitForExit()
$sw.Stop()

$result = [ordered]@{
  tool = 'CleanSuite v4.3 (PowerShell 实现)'
  elapsedMs = $sw.ElapsedMilliseconds
  elapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
  peakMemoryMB = [math]::Round($peak / 1MB, 1)
  exitCode = $p.ExitCode
}
$result | ConvertTo-Json | Set-Content (Join-Path $benchDir 'legacy-scan-result.json') -Encoding UTF8

Write-Output "elapsed=$($result.elapsedSec)s peak=$($result.peakMemoryMB)MB exit=$($p.ExitCode)"
Write-Output '=== stdout ==='
Get-Content $outFile -ErrorAction SilentlyContinue | Select-Object -First 60
Write-Output '=== stderr (first 10) ==='
Get-Content $errFile -ErrorAction SilentlyContinue | Select-Object -First 10
