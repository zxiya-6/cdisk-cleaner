# ui-probe3.ps1 — UIA 诊断：统计元素、枚举按钮、多方式查找
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$exe = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe'

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
if (-not $win) { Write-Output 'FAIL no window'; exit 1 }
Write-Output "window found at $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Start-Sleep -Seconds 3

$all = $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output "elements in window subtree: $($all.Count)"

$byType = @{}
$buttons = @()
$tabs = @()
foreach ($e in $all) {
  try {
    $ct = $e.Current.ControlType.ProgrammaticName
    if ($byType.ContainsKey($ct)) { $byType[$ct]++ } else { $byType[$ct] = 1 }
    if ($ct -eq 'ControlType.Button') { $buttons += ("id='{0}' name='{1}' en={2} offscreen={3}" -f $e.Current.AutomationId, $e.Current.Name, $e.Current.IsEnabled, $e.Current.IsOffscreen) }
    if ($ct -eq 'ControlType.TabItem') { $tabs += $e.Current.Name }
  } catch { }
}
Write-Output '--- by type ---'
$byType.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Output ("  {0}: {1}" -f $_.Key, $_.Value) }
Write-Output "--- buttons ($($buttons.Count)) ---"
$buttons | ForEach-Object { Write-Output "  $_" }
Write-Output "--- tabs: $($tabs -join ' | ')"

# 直接找 BtnScan
$c1 = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'BtnScan')
$b1 = $win.FindFirst($TS::Descendants, $c1)
Write-Output "find BtnScan by id: $($null -ne $b1)"
$c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '扫描可清理项')
$b2 = $win.FindFirst($TS::Descendants, $c2)
Write-Output "find by name 扫描可清理项: $($null -ne $b2)"

# 若找到则点击并观察
if ($b1) {
  $b1.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output 'scan invoked'
  Start-Sleep -Seconds 8
  $lvCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'LvCategories')
  $lv = $win.FindFirst($TS::Descendants, $lvCond)
  if ($lv) {
    $rowCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $cnt = $lv.FindAll($TS::Descendants, $rowCond).Count
    Write-Output "rows after scan = $cnt"
  } else { Write-Output 'LvCategories not found' }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\tools\capture.ps1' -ProcessId $p.Id -OutFile 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\shots\probe3.png'
$p.CloseMainWindow() | Out-Null
Start-Sleep 2
$p.Refresh()
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
