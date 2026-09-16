# ui-probe4.ps1 — 确认扫描后列表行的 UIA 控件类型
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$exe = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe'

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

$p = Start-Process -FilePath $exe -PassThru
$win = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 60) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
  $wins = $AE::RootElement.FindAll($TS::Children, $cond)
  foreach ($w in $wins) { if ($w.Current.Name -like '*清理助手*') { $win = $w; break } }
  if ($win) { break }
  Start-Sleep -Milliseconds 400
}
Write-Output "window at $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Start-Sleep -Seconds 2

$btn = Wait-Id $win 'BtnScan' 30
Write-Output "BtnScan found=$($null -ne $btn)"
if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'scan invoked' }
Start-Sleep -Seconds 8

$lv = Wait-Id $win 'LvCategories' 10
if (-not $lv) { Write-Output 'no LvCategories'; exit 1 }
$desc = $lv.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$types = @{}
foreach ($e in $desc) { try { $ct = $e.Current.ControlType.ProgrammaticName; if ($types.ContainsKey($ct)) { $types[$ct]++ } else { $types[$ct] = 1 } } catch {} }
Write-Output "LvCategories descendants: $($desc.Count)"
$types.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Output ("  {0}: {1}" -f $_.Key, $_.Value) }

$cData = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
$cList = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
Write-Output "DataItem count = $($lv.FindAll($TS::Descendants, $cData).Count)"
Write-Output "ListItem count = $($lv.FindAll($TS::Descendants, $cList).Count)"

$kids = $lv.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output "LvCategories direct children: $($kids.Count)"
$i = 0
foreach ($k in $kids) { Write-Output ("  child${i}: type=$($k.Current.ControlType.ProgrammaticName) name='$($k.Current.Name)'"); $i++; if ($i -ge 6) { break } }

# 实时状态确认：BtnClean 是否可用 + 状态文字
$clean = Wait-Id $win 'BtnClean' 5
Write-Output "BtnClean enabled=$($clean.Current.IsEnabled)"
$status = Wait-Id $win 'TxtStatus' 5
Write-Output "TxtStatus='$($status.Current.Name)'"

$p.CloseMainWindow() | Out-Null
Start-Sleep 2
$p.Refresh()
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
