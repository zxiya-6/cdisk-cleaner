# verify-dism.ps1 — 通过 UI 读取“工具页 → 分析组件存储”的实际显示内容，验证 DISM 输出编码修复
param(
  [string]$ExePath = 'C:\Users\Lenovo\Desktop\C盘清理工具\C盘清理助手 v5.0\C盘清理助手.exe',
  [string]$ShotDir = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\installer\out\shots',
  [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-MainWindow([int]$procId, $timeoutSec = 60) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $wins = $AE::RootElement.FindAll($TS::Children, $cond)
    foreach ($w in $wins) { if ($w.Current.Name -like '*清理助手*') { return $w } }
    Start-Sleep -Milliseconds 350
  }
  return $null
}
function Find-In($root, [string]$id, $timeoutSec = 12) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    try {
      $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
      $el = $root.FindFirst($TS::Descendants, $c)
      if ($el) { return $el }
    } catch { }
    Start-Sleep -Milliseconds 350
  }
  return $null
}
function Invoke-Retry($el, [int]$attempts = 3) {
  for ($i = 1; $i -le $attempts; $i++) {
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { Start-Sleep -Milliseconds 600 }
  }
  return $false
}
function Get-Text($el) {
  if (-not $el) { return $null }
  try { $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern); return $tp.DocumentRange.GetText(-1) } catch { }
  try { $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); return $vp.Current.Value } catch { }
  return $null
}

# 关闭已有实例，确保测试对象是修复后的版本
Get-Process | Where-Object { $_.ProcessName -like '*清理助手*' -and $_.Path -ne $null } | ForEach-Object {
  Write-Output ("关闭旧实例: pid=" + $_.Id + " " + $_.Path)
  Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath) -PassThru
Write-Output ("launched pid=" + $p.Id)
$win = Get-MainWindow $p.Id 60
if (-not $win) { Write-Output 'FAIL: 主窗口未出现'; exit 1 }
Start-Sleep -Seconds 2

# 切到“工具”页签
$c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '工具')
$c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
$and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
$tab = $win.FindFirst($TS::Descendants, $and)
if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Output '已切换到工具页' } catch { Write-Output ('切页失败: ' + $_) } }
Start-Sleep -Milliseconds 900

$btn = Find-In $win 'BtnDismAnalyze' 15
if (-not $btn) { Write-Output 'FAIL: 未找到分析按钮'; $p.Kill(); exit 1 }
Write-Output ("按钮可达: '" + $btn.Current.Name + "'")
$ok = Invoke-Retry $btn
Write-Output ("invoke=" + $ok)

$sw = [Diagnostics.Stopwatch]::StartNew()
$text = ''
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
  Start-Sleep -Seconds 3
  $tb = Find-In $win 'TxtDismOutput' 3
  $text = Get-Text $tb
  if ($text -and $text -match '完成：退出码') { break }
  if ($sw.Elapsed.TotalSeconds -gt 300) { break }
}
$sw.Stop()
Write-Output ("轮询等待: " + [math]::Round($sw.Elapsed.TotalSeconds,1) + " s，输出长度: " + $(if ($text) { $text.Length } else { 0 }) + " 字符")

if ($text) {
  $hasFFFD = $text.Contains([char]0xFFFD)
  $hasDeploy = $text.Contains('部署映像服务和管理工具')
  $hasImgVer = $text.Contains('映像版本')
  $hasDone = $text -match '完成：退出码'
  Write-Output '—— 检查结果 ——'
  Write-Output ("包含『部署映像服务和管理工具』: " + $hasDeploy)
  Write-Output ("包含『映像版本』: " + $hasImgVer)
  Write-Output ("包含完成行: " + $hasDone)
  Write-Output ("包含 U+FFFD 替换符: " + $hasFFFD)

  $txtOut = Join-Path $ShotDir 'dism-output-text.txt'
  [IO.File]::WriteAllText($txtOut, $text, [Text.Encoding]::UTF8)
  Write-Output ("全文已保存: " + $txtOut)

  Write-Output '—— 输出头部 10 行 ——'
  ($text -split "`r?`n" | Select-Object -First 10) | ForEach-Object { Write-Output $_ }
} else {
  Write-Output 'WARN: 未读取到输出文本'
}

# 截图工具页
$capture = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\tools\capture.ps1'
if (Test-Path $capture) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $capture -ProcessId $p.Id -OutFile (Join-Path $ShotDir 'dism-verify.png') | Out-Null
  Write-Output '截图: dism-verify.png'
}

$p.CloseMainWindow() | Out-Null
Start-Sleep 2
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
