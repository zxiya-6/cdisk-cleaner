# bench-final.ps1 — 终版口径统一的性能基准
#  A. v5 发布版 GUI 冷启动 ×3（窗口标题出现口径，清空 DOTNET_ROOT 证明零依赖）
#  B. v5 发布版 CLI 全量扫描 ×3（耗时 + 内存峰值）
#  C. v4.3 GUI 冷启动 ×3（同一窗口标题口径）
#  D. v4.3 无头扫描 ×2（耗时 + 内存峰值）
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$benchDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\bench'
New-Item -ItemType Directory -Force -Path $benchDir | Out-Null

$appV5 = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理助手 v5.0\C盘清理助手.exe'
$cliV5 = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理助手 v5.0\tools\CleanMasterCli.exe'
$legacyScript = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理工具_v4.3\C盘清理工具\CleanSuite.ps1'

function Wait-TitleMs([int]$procId, [string]$pattern, [int]$timeoutMs = 60000) {
  $t = [Diagnostics.Stopwatch]::StartNew()
  while ($t.ElapsedMilliseconds -lt $timeoutMs) {
    try {
      $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
      if ($p -and $p.MainWindowHandle -ne 0 -and $p.MainWindowTitle -match $pattern) { return $t.ElapsedMilliseconds }
    } catch { }
    Start-Sleep -Milliseconds 60
  }
  return -1
}

$results = [ordered]@{ v5Gui = @(); v5Cli = @(); legacyGui = @(); legacyScan = @() }

# ---- A. v5 发布版 GUI 冷启动（清空运行时环境变量，证明零依赖运行） ----
$savedRoot = $env:DOTNET_ROOT; $savedRootX = $env:DOTNET_ROOT_X64
$env:DOTNET_ROOT = $null; $env:DOTNET_ROOT_X64 = $null
for ($i = 1; $i -le 3; $i++) {
  $p = Start-Process -FilePath $appV5 -PassThru
  $ms = Wait-TitleMs $p.Id 'C盘清理助手 v5' 60000
  $p.Refresh()
  $ws = if (-not $p.HasExited) { [math]::Round($p.WorkingSet64 / 1MB, 1) } else { -1 }
  $results.v5Gui += [ordered]@{ run = $i; readyMs = $ms; workingSetMB = $ws }
  Write-Output "v5Gui${i}: ready=${ms}ms ws=${ws}MB"
  $p.CloseMainWindow() | Out-Null
  Start-Sleep 2
  $p.Refresh()
  if (-not $p.HasExited) { $p.Kill() }
  Start-Sleep 1
}
$env:DOTNET_ROOT = $savedRoot; $env:DOTNET_ROOT_X64 = $savedRootX

# ---- B. v5 发布版 CLI 扫描 ×3 ----
for ($i = 1; $i -le 3; $i++) {
  $out = Join-Path $benchDir "final-scan-out$i.txt"
  $err = Join-Path $benchDir "final-scan-err$i.txt"
  $json = Join-Path $benchDir "final-scan-$i.json"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $cliV5 -ArgumentList @('scan', '--json', $json) -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $peak = 0L
  while (-not $p.HasExited) {
    try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
    Start-Sleep -Milliseconds 70
  }
  $sw.Stop()
  $text = Get-Content $out -Encoding UTF8 | Out-String
  $selfTime = ''
  if ($text -match '耗时：([\d\.]+)s') { $selfTime = $matches[1] }
  $files = ''
  if ($text -match '· ([\d,]+) 个文件 ·') { $files = $matches[1] }
  $results.v5Cli += [ordered]@{
    run = $i
    wallClockSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    selfReportedSec = $selfTime
    peakMemoryMB = [math]::Round($peak / 1MB, 1)
    files = $files
  }
  Write-Output "v5Cli${i}: wall=$([math]::Round($sw.Elapsed.TotalSeconds,2))s peak=$([math]::Round($peak/1MB,1))MB files=$files"
  Start-Sleep -Milliseconds 800
}

# ---- C. v4.3 GUI 冷启动 ×3（同一窗口标题口径） ----
for ($i = 1; $i -le 3; $i++) {
  $p = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $legacyScript) -PassThru
  $ms = Wait-TitleMs $p.Id 'C 盘清理套件' 90000
  $p.Refresh()
  $ws = if (-not $p.HasExited) { [math]::Round($p.WorkingSet64 / 1MB, 1) } else { -1 }
  $results.legacyGui += [ordered]@{ run = $i; readyMs = $ms; workingSetMB = $ws }
  Write-Output "legacyGui${i}: ready=${ms}ms ws=${ws}MB"
  $p.CloseMainWindow() | Out-Null
  Start-Sleep 2
  $p.Refresh()
  if (-not $p.HasExited) { $p.Kill() }
  Start-Sleep 1
}

# ---- D. v4.3 无头扫描 ×2 ----
for ($i = 1; $i -le 2; $i++) {
  $out = Join-Path $benchDir "final-legacy-out$i.txt"
  $err = Join-Path $benchDir "final-legacy-err$i.txt"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $legacyScript, '-ScanOnly') `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
  $peak = 0L
  while (-not $p.HasExited) {
    try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
    Start-Sleep -Milliseconds 120
  }
  $p.WaitForExit()
  $sw.Stop()
  $results.legacyScan += [ordered]@{
    run = $i
    elapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    peakMemoryMB = [math]::Round($peak / 1MB, 1)
  }
  Write-Output "legacyScan${i}: elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,2))s peak=$([math]::Round($peak/1MB,1))MB"
  Start-Sleep 1
}

# ---- 汇总（中位数） ----
function Median($arr) {
  $sorted = @($arr | Sort-Object)
  $n = $sorted.Count
  if ($n -eq 0) { return -1 }
  if ($n % 2 -eq 1) { return $sorted[[int][math]::Floor($n / 2)] }
  return [math]::Round(($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2.0, 2)
}

$summary = [ordered]@{
  v5_gui_ready_ms_median = Median @($results.v5Gui | ForEach-Object { $_.readyMs })
  legacy_gui_ready_ms_median = Median @($results.legacyGui | ForEach-Object { $_.readyMs })
  v5_scan_wall_median = Median @($results.v5Cli | ForEach-Object { $_.wallClockSec })
  v5_scan_peak_mb_median = Median @($results.v5Cli | ForEach-Object { $_.peakMemoryMB })
  legacy_scan_median = Median @($results.legacyScan | ForEach-Object { $_.elapsedSec })
  legacy_scan_peak_mb_median = Median @($results.legacyScan | ForEach-Object { $_.peakMemoryMB })
}
$out = [ordered]@{ raw = $results; summary = $summary }
$out | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $benchDir 'bench-final.json') -Encoding UTF8

Write-Output ''
Write-Output '==== 汇总（中位数） ===='
$summary | ConvertTo-Json
