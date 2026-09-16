# uitest.ps1 v3 — 多路径兜底的界面交互测试（只读与取消操作，不执行清理）
param(
  [string]$ExePath = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.App\bin\Release\net7.0-windows\C盘清理助手.exe',
  [string]$ShotDir  = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\shots'
)
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
public class WinEnum3 {
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
        res.Add(h.ToInt64().ToString() + "|" + c.ToString() + "|" + t.ToString() + "|" + vis);
      }
      return true;
    }, IntPtr.Zero);
    return res;
  }
}
"@

$capture = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\tools\capture.ps1'
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
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
# 从根元素按 pid+AutomationId 找（兜底路径）
function Find-Anywhere([int]$procId, [string]$id, $timeoutSec = 20) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    try {
      $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
      $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
      $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
      $el = $AE::RootElement.FindFirst($TS::Descendants, $and)
      if ($el) { return $el }
    } catch { }
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
    try {
      $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      return $true
    } catch { Start-Sleep -Milliseconds 600 }
  }
  return $false
}
function Shot($procId, $name) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $capture -ProcessId $procId -OutFile (Join-Path $ShotDir $name) | Out-Null
}
function Shot-Hwnd($hwnd, $name) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $capture -Hwnd $hwnd -OutFile (Join-Path $ShotDir $name) | Out-Null
}
function Count-Rows($lv) {
  if (-not $lv) { return 0 }
  try {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    $n = $lv.FindAll($TS::Descendants, $c).Count
    if ($n -gt 0) { return $n }
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    return $lv.FindAll($TS::Descendants, $c2).Count
  } catch { return -1 }
}

$log = @()
$p = Start-Process -FilePath $ExePath -PassThru
$log += "launched pid=$($p.Id)"
$win = Get-MainWindow $p.Id 60
if (-not $win) { Write-Output 'FAIL: no window'; $log; exit 1 }
Start-Sleep -Seconds 2
Shot $p.Id 'ui-01-main.png'

# 等待 UIA 就绪：BtnScan 可达（窗口范围或根范围任一）
$btnScan = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 40 -and -not $btnScan) {
  $btnScan = Find-In $win 'BtnScan' 2
  if (-not $btnScan) { $btnScan = Find-Anywhere $p.Id 'BtnScan' 2 }
}
$log += "btnScan found=$($null -ne $btnScan) in $([math]::Round($sw.Elapsed.TotalSeconds,1))s"

# 1) 扫描
if ($btnScan) {
  $okInv = Invoke-Retry $btnScan
  $log += "scan invoke=$okInv"
} else {
  $log += 'FAIL: BtnScan unreachable'
}
# 轮询行出现
$rowsFound = 0
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 70) {
  Start-Sleep -Seconds 1
  $lv = Find-In $win 'LvCategories' 2
  if (-not $lv) { $lv = Find-Anywhere $p.Id 'LvCategories' 2 }
  $rowsFound = Count-Rows $lv
  if ($rowsFound -gt 0) { break }
}
$log += "scan rows=$rowsFound (waited $([math]::Round($sw.Elapsed.TotalSeconds,1))s)"
Start-Sleep -Milliseconds 800
Shot $p.Id 'ui-02-after-scan.png'

# 2) 选中第一行 → 详情
$lv = Find-In $win 'LvCategories' 5
if (-not $lv) { $lv = Find-Anywhere $p.Id 'LvCategories' 5 }
if ($lv -and $rowsFound -gt 0) {
  try {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    $rows = $lv.FindAll($TS::Descendants, $c)
    if ($rows.Count -gt 0) {
      $rows.Item(0).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      $log += 'selected row0'
    }
  } catch { $log += "select failed: $_" }
}
Start-Sleep -Seconds 1
Shot $p.Id 'ui-03-detail.png'

# 3) 清理确认对话框（打开→截图→取消）
$cleanClicked = $false
for ($attempt = 1; $attempt -le 3 -and -not $cleanClicked; $attempt++) {
  $btnClean = $null
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt 15) {
    $btnClean = Find-In $win 'BtnClean' 2
    if (-not $btnClean) { $btnClean = Find-Anywhere $p.Id 'BtnClean' 2 }
    if ($btnClean -and $btnClean.Current.IsEnabled) { break }
    Start-Sleep -Milliseconds 400
  }
  if ($btnClean -and $btnClean.Current.IsEnabled) {
    $ok = Invoke-Retry $btnClean
    $log += "clean invoke attempt$attempt = $ok"
    # 用 Win32 枚举等待“确认清理”窗口出现
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $dlgHwnd = 0
    while ($sw.Elapsed.TotalSeconds -lt 12) {
      Start-Sleep -Milliseconds 500
      $wlist = [WinEnum3]::ListForPid($p.Id)
      foreach ($w in $wlist) {
        $parts = $w -split '\|'
        if ($parts.Count -ge 4 -and $parts[2] -eq '确认清理' -and $parts[3] -eq 'True') { $dlgHwnd = [int64]$parts[0]; break }
      }
      if ($dlgHwnd -gt 0) { break }
    }
    if ($dlgHwnd -gt 0) {
      $cleanClicked = $true
      $log += "dialog hwnd=$dlgHwnd"
      Start-Sleep -Milliseconds 700
      Shot-Hwnd $dlgHwnd 'ui-04-confirm-dialog.png'
      # 取消：优先 UIA 按钮，兜底 WM_CLOSE
      $cancelled = $false
      try {
        $dlgEl = $AE::FromHandle([IntPtr]$dlgHwnd)
        $cancel = Find-In $dlgEl 'BtnCancel' 6
        if ($cancel) { $cancelled = Invoke-Retry $cancel; $log += "dialog cancel via UIA=$cancelled" }
      } catch { }
      if (-not $cancelled) {
        Add-Type @"
using System;using System.Runtime.InteropServices;
public class W32Close { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }
"@
        [W32Close]::PostMessage([IntPtr]$dlgHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        $log += 'dialog closed via WM_CLOSE'
      }
    } else {
      $log += "attempt$attempt no dialog window"
    }
  } else {
    $log += "attempt${attempt}: clean button not enabled"
  }
}
Start-Sleep -Milliseconds 900
Shot $p.Id 'ui-05-after-cancel.png'

# 4) 页签遍历截图
function Select-Tab($win, $name) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
  $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
  $tab = $win.FindFirst($TS::Descendants, $and)
  if (-not $tab) { return 'not-found' }
  try { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return 'ok' } catch { return "err: $_" }
}
$tabs = @(
  @('大文件', 'ui-06-bigfiles.png'),
  @('备份与恢复', 'ui-07-backup.png'),
  @('历史台账', 'ui-08-history.png'),
  @('工具', 'ui-09-tools.png'),
  @('清理', 'ui-10-clean-back.png')
)
foreach ($t in $tabs) {
  $ok = Select-Tab $win $t[0]
  Start-Sleep -Milliseconds 900
  Shot $p.Id $t[1]
  $log += "tab '$($t[0])' -> $ok"
}

$log | Write-Output
$p.CloseMainWindow() | Out-Null
Start-Sleep 2
if (-not $p.HasExited) { $p.Kill() }
Write-Output 'DONE'
