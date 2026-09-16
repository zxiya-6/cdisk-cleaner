# ui-probe2.ps1 — 稳健版 UIA 探针：等待、点击扫描、转储列表 UIA 子树
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$exe = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe'

function Dump($el, $indent, $depth) {
  if ($depth -le 0) { return }
  $pad = ' ' * $indent
  Write-Output ("$pad- type=$($el.Current.ControlType.ProgrammaticName) id='$($el.Current.AutomationId)' name='$([string]$el.Current.Name).Substring(0,[Math]::Min(40,[string]$el.Current.Name.Length))'")
  $kids = $el.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
  $n = 0
  foreach ($k in $kids) { Dump $k ($indent + 2) ($depth - 1); $n++; if ($n -ge 8) { Write-Output "$pad  ...($($kids.Count) children total)"; break } }
}

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

# 等待窗口（按 pid，名称含“清理助手”）
$win = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 40) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
  $wins = $AE::RootElement.FindAll($TS::Children, $cond)
  foreach ($w in $wins) { if ($w.Current.Name -like '*清理助手*') { $win = $w; break } }
  if ($win) { break }
  Start-Sleep -Milliseconds 400
}
if (-not $win) { Write-Output 'FAIL: no window'; $p.Kill(); exit 1 }
Write-Output "window ready at $([math]::Round($sw.Elapsed.TotalSeconds,1))s: '$($win.Current.Name)'"

function Wait-Id($root, $id, $timeoutSec) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $el = $root.FindFirst($TS::Descendants, $c)
    if ($el) { return $el }
    Start-Sleep -Milliseconds 400
  }
  return $null
}

$btn = Wait-Id $win 'BtnScan' 30
Write-Output "BtnScan found=$($null -ne $btn) (after wait)"
if (-not $btn) { Dump $win 0 4; $p.Kill(); exit 1 }

$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'scan clicked'
$sw = [Diagnostics.Stopwatch]::StartNew()
$found = 0
while ($sw.Elapsed.TotalSeconds -lt 40) {
  Start-Sleep -Seconds 1
  $lv = Wait-Id $win 'LvCategories' 3
  if (-not $lv) { continue }
  $rowCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $desc = $lv.FindAll($TS::Descendants, $rowCond)
  $found = $desc.Count
  if ($found -gt 0) { break }
}
Write-Output "rows found=$found (waited $([math]::Round($sw.Elapsed.TotalSeconds,1))s)"

if ($found -eq 0) {
  Write-Output '=== dump LvCategories subtree ==='
  $lv = Wait-Id $win 'LvCategories' 5
  if ($lv) { Dump $lv 0 5 } else { Write-Output 'LvCategories not found at all'; Dump $win 0 4 }
} else {
  $lv = Wait-Id $win 'LvCategories' 5
  $rowCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $desc = $lv.FindAll($TS::Descendants, $rowCond)
  Write-Output 'first 5 rows:'
  for ($i = 0; $i -lt [Math]::Min(5, $desc.Count); $i++) {
    $r = $desc.Item($i)
    Write-Output ("  row$i name='$($r.Current.Name)'")
  }
  # 选中第一行
  try {
    $desc.Item(0).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 1
    $files = 0
    $dv = Wait-Id $win 'LvDetailFiles' 6
    if ($dv) {
      $rowCond2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
      $files = $dv.FindAll($TS::Descendants, $rowCond2).Count
    }
    Write-Output "selected row0; detail file rows=$files"
  } catch { Write-Output "select/row detail failed: $_" }
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\tools\capture.ps1' -ProcessId $p.Id -OutFile 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\shots\probe2-final.png'
$p.CloseMainWindow() | Out-Null
Start-Sleep 2
$p.Refresh()
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
