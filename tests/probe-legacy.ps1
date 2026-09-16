# probe-legacy.ps1 — measure cold-start of legacy tools and capture screenshots (baseline evidence)
param(
  [string]$OutDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\bench',
  [string]$ShotDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\shots'
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $OutDir, $ShotDir | Out-Null
$capture = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\tools\capture.ps1'
$results = [ordered]@{}

function Wait-Window([int]$pid_, [int]$timeoutMs) {
  # returns ms until a visible main window appears, or -1
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    $p = Get-Process -Id $pid_ -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne 0) { return $sw.ElapsedMilliseconds }
    Start-Sleep -Milliseconds 50
  }
  return -1
}

# ---------- A. 磁盘清理助手 v1 (WPF self-contained) ----------
Write-Output '=== A: 磁盘清理助手 v1 ==='
$exeA = 'C:\Users\Lenovo\Desktop\C盘清理工具\磁盘清理助手 v1\磁盘清理助手_文件夹版\磁盘清理助手.exe'
try {
  $swA = [System.Diagnostics.Stopwatch]::StartNew()
  $pA = Start-Process -FilePath $exeA -PassThru
  $winMs = Wait-Window $pA.Id 30000
  $swA.Stop()
  Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("[{0}] A launch->window: {1} ms" -f (Get-Date -Format 'HH:mm:ss'), $winMs)
  Start-Sleep -Seconds 6
  $pA.Refresh()
  if (-not $pA.HasExited) {
    $wsMB = [math]::Round($pA.WorkingSet64 / 1MB, 1)
    $title = $pA.MainWindowTitle
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("A title='$title' ws={0} MB" -f $wsMB)
    $results['wpfv1'] = [ordered]@{ windowMs = $winMs; workingSetMB = $wsMB; title = $title; alive = $true }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -ProcessId $pA.Id -OutFile (Join-Path $ShotDir 'wpfv1-main.png') -TimeoutSec 12 | Write-Output
    # graceful close
    if ($pA.MainWindowHandle -ne 0) { $pA.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3 }
    $pA.Refresh(); if (-not $pA.HasExited) { $pA.Kill() }
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value 'A closed'
  } else {
    $results['wpfv1'] = [ordered]@{ windowMs = $winMs; alive = $false; exitCode = $pA.ExitCode }
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("A EXITED early code={0}" -f $pA.ExitCode)
  }
} catch {
  $results['wpfv1'] = [ordered]@{ error = $_.Exception.Message }
  Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("A ERROR: {0}" -f $_.Exception.Message)
}

# ---------- B. CleanSuite v4.3 (PowerShell + WPF) ----------
Write-Output '=== B: CleanSuite v4.3 ==='
$ps1 = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理工具_v4.3\C盘清理工具\CleanSuite.ps1'
try {
  $swB = [System.Diagnostics.Stopwatch]::StartNew()
  $pB = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$ps1) -PassThru
  $winMsB = Wait-Window $pB.Id 40000
  $swB.Stop()
  Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("[{0}] B launch->window: {1} ms" -f (Get-Date -Format 'HH:mm:ss'), $winMsB)
  Start-Sleep -Seconds 8
  $pB.Refresh()
  if (-not $pB.HasExited) {
    $wsMB = [math]::Round($pB.WorkingSet64 / 1MB, 1)
    $title = $pB.MainWindowTitle
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("B title='$title' ws={0} MB" -f $wsMB)
    $results['v43'] = [ordered]@{ windowMs = $winMsB; workingSetMB = $wsMB; title = $title; alive = $true }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $capture -ProcessId $pB.Id -OutFile (Join-Path $ShotDir 'v43-main.png') -TimeoutSec 12 | Write-Output
    $pB.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 3
    $pB.Refresh(); if (-not $pB.HasExited) { $pB.Kill() }
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value 'B closed'
  } else {
    $results['v43'] = [ordered]@{ windowMs = $winMsB; alive = $false; exitCode = $pB.ExitCode }
    Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("B EXITED early code={0}" -f $pB.ExitCode)
  }
} catch {
  $results['v43'] = [ordered]@{ error = $_.Exception.Message }
  Add-Content -Path (Join-Path $OutDir 'probe-legacy.log') -Value ("B ERROR: {0}" -f $_.Exception.Message)
}

$results | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $OutDir 'probe-legacy.json') -Encoding UTF8
Write-Output '=== RESULTS ==='
$results | ConvertTo-Json -Depth 5
