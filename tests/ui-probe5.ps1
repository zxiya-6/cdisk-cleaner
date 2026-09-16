# ui-probe5.ps1 — 点击清理按钮后用 Win32 枚举窗口 + 日志验证对话框是否真的打开
$ErrorActionPreference = 'Continue'
if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') {
  $env:DOTNET_ROOT = 'C:\Program Files\dotnet'
  $env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class WinEnum {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  public static List<string> ListForPid(uint target) {
    var res = new List<string>();
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target) {
        var t = new StringBuilder(256); GetWindowText(h, t, 256);
        var c = new StringBuilder(256); GetClassName(h, c, 256);
        bool vis = IsWindowVisible(h);
        res.Add(h.ToInt64().ToString() + "|class=" + c.ToString() + "|title=" + t.ToString() + "|vis=" + vis);
      }
      return true;
    }, IntPtr.Zero);
    return res;
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$exe = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe'
$logFile = 'C:\Users\Lenovo\AppData\Local\C盘清理助手\logs\app-20260916.log'

function Wait-Id($root, $id, $timeoutSec = 30) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $el = $root.FindFirst($TS::Descendants, $c)
    if ($el) { return $el }
    Start-Sleep -Milliseconds 350
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
  Start-Sleep -Milliseconds 350
}
Write-Output "window at $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Start-Sleep -Seconds 2

$btn = Wait-Id $win 'BtnScan' 30
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 6
$lv = Wait-Id $win 'LvCategories' 10
Write-Output "rows=$($lv.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))).Count)"

$clean = Wait-Id $win 'BtnClean' 10
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 15 -and -not $clean.Current.IsEnabled) { Start-Sleep -Milliseconds 300 }
Write-Output "btnClean enabled=$($clean.Current.IsEnabled)"
Write-Output 'invoking clean click...'
$clean.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'invoke returned'

for ($i = 1; $i -le 12; $i++) {
  Start-Sleep -Seconds 1
  $wlist = [WinEnum]::ListForPid($p.Id)
  Write-Output "t+${i}s windows=$($wlist.Count)"
  if ($wlist.Count -gt 0) { $wlist | ForEach-Object { Write-Output "    $_" } }
  if ($wlist.Count -gt 1) { break }
}

# 查 UIA 里能否找到该窗口
$cond2 = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$uiaWins = $AE::RootElement.FindAll($TS::Children, $cond2)
Write-Output "UIA windows: $($uiaWins.Count)"
foreach ($w in $uiaWins) { Write-Output "  uia-win: '$($w.Current.Name)' class=$($w.Current.ClassName)" }

Write-Output '=== app log tail ==='
Get-Content $logFile -Tail 6 -Encoding UTF8

$p.CloseMainWindow() | Out-Null
Start-Sleep 2
$p.Refresh()
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
