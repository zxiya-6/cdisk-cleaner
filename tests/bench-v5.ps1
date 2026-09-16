# bench-v5.ps1 — C盘清理助手 v5 性能基准：扫描耗时/内存峰值（CLI）×3 + GUI 冷启动×3
$ErrorActionPreference = 'Continue'
if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') {
  $env:DOTNET_ROOT = 'C:\Program Files\dotnet'
  $env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
}
$benchDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\bench'
New-Item -ItemType Directory -Force -Path $benchDir | Out-Null
$cli = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.Cli\bin\Release\net7.0-windows\CleanMasterCli.exe'
$app = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe'
$results = [ordered]@{ cliScan = @(); guiStart = @() }

# ---- CLI 扫描 ×3（真实全量） ----
for ($i = 1; $i -le 3; $i++) {
  $out = Join-Path $benchDir "bench-scan-out$i.txt"
  $err = Join-Path $benchDir "bench-scan-err$i.txt"
  $json = Join-Path $benchDir "bench-scan-$i.json"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $cli -ArgumentList @('scan', '--json', $json) -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $peak = 0L
  while (-not $p.HasExited) {
    try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
    Start-Sleep -Milliseconds 80
  }
  $sw.Stop()
  $text = Get-Content $out -Encoding UTF8 | Out-String
  $selfTime = ''
  if ($text -match '耗时：([\d\.]+)s') { $selfTime = $matches[1] }
  $files = ''
  if ($text -match '· ([\d,]+) 个文件 ·') { $files = $matches[1] }
  $results.cliScan += [ordered]@{
    run = $i
    wallClockSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    selfReportedSec = $selfTime
    peakMemoryMB = [math]::Round($peak / 1MB, 1)
    files = $files
  }
  Write-Output "scan${i}: wall=$([math]::Round($sw.Elapsed.TotalSeconds,2))s peak=$([math]::Round($peak/1MB,1))MB files=$files"
  Start-Sleep -Seconds 1
}

# ---- GUI 冷启动 ×3（以日志“主窗口已打开”为新基准） ----
$logFile = 'C:\Users\Lenovo\AppData\Local\C盘清理助手\logs\app-20260916.log'
for ($i = 1; $i -le 3; $i++) {
  $beforeLen = 0
  if (Test-Path $logFile) { $beforeLen = ([IO.File]::ReadAllText($logFile, [Text.Encoding]::UTF8)).Length }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $app -PassThru
  $readyMs = -1
  $t = [Diagnostics.Stopwatch]::StartNew()
  while ($t.Elapsed.TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 150
    try {
      $content = [IO.File]::ReadAllText($logFile, [Text.Encoding]::UTF8)
      if ($content.Length -gt $beforeLen) {
        $tail = $content.Substring($beforeLen)
        if ($tail -match '主窗口已打开') { $readyMs = $t.ElapsedMilliseconds; break }
      }
    } catch { }
  }
  $sw.Stop()
  $p.Refresh()
  $ws = if (-not $p.HasExited) { [math]::Round($p.WorkingSet64 / 1MB, 1) } else { -1 }
  $results.guiStart += [ordered]@{
    run = $i
    readyMs = $readyMs
    processAliveMs = $sw.ElapsedMilliseconds
    workingSetMBatReady = $ws
  }
  Write-Output "gui${i}: ready=${readyMs}ms ws=${ws}MB"
  $p.CloseMainWindow() | Out-Null
  Start-Sleep 2
  $p.Refresh()
  if (-not $p.HasExited) { $p.Kill() }
  Start-Sleep 1
}

$results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $benchDir 'bench-v5.json') -Encoding UTF8
Write-Output '==== saved bench-v5.json ===='
$results | ConvertTo-Json -Depth 6
