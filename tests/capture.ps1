# capture.ps1 — capture a window screenshot (by process name/id/title/hwnd). PrintWindow, fallback CopyFromScreen
param(
  [string]$Process,
  [int]$ProcessId = 0,
  [int]$Hwnd = 0,
  [string]$TitleLike = '',
  [Parameter(Mandatory=$true)][string]$OutFile,
  [int]$TimeoutSec = 15,
  [switch]$FullScreen
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Save-Image($bmp, $path) {
  $dir = Split-Path -Parent $path
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

if ($FullScreen) {
  Add-Type -AssemblyName System.Windows.Forms
  $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $bmp.Size)
  $g.Dispose()
  Save-Image $bmp $OutFile
  $bmp.Dispose()
  Write-Output "OK fullscreen -> $OutFile"
  exit 0
}

$targetHwnd = [IntPtr]::Zero

if ($Hwnd -gt 0) {
  $targetHwnd = [IntPtr]$Hwnd
  Write-Output "window(hwnd): $Hwnd"
} else {
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  $proc = $null
  while ((Get-Date) -lt $deadline) {
    if ($ProcessId -gt 0) {
      $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    } elseif ($TitleLike) {
      $proc = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like $TitleLike } | Select-Object -First 1
    } elseif ($Process) {
      $proc = Get-Process -Name $Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if ($proc) { break }
    Start-Sleep -Milliseconds 400
  }
  if (-not $proc) { Write-Output "ERROR: no window found (Process='$Process' TitleLike='$TitleLike' pid=$ProcessId)"; exit 1 }
  Write-Output "window: pid=$($proc.Id) title='$($proc.MainWindowTitle)'"
  $targetHwnd = $proc.MainWindowHandle
}

$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($targetHwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { $w = 1280; $h = 800 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($targetHwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if ($ok) {
  $pts = @(@( [int]($w*0.5), [int]($h*0.08) ), @( [int]($w*0.5), [int]($h*0.5) ), @( [int]($w*0.2), [int]($h*0.9) ))
  $nonBlack = $false
  foreach ($pt in $pts) {
    try { $c = $bmp.GetPixel($pt[0], $pt[1]); if (($c.R + $c.G + $c.B) -gt 15) { $nonBlack = $true; break } } catch {}
  }
  if ($nonBlack) {
    Save-Image $bmp $OutFile; $bmp.Dispose()
    Write-Output "OK printwindow -> $OutFile ($w x $h)"
    exit 0
  }
}
$bmp.Dispose()

[WinCap]::SetForegroundWindow($targetHwnd) | Out-Null
Start-Sleep -Milliseconds 500
$r2 = New-Object WinCap+RECT
[WinCap]::GetWindowRect($targetHwnd, [ref]$r2) | Out-Null
$w2 = $r2.Right - $r2.Left; $h2 = $r2.Bottom - $r2.Top
$bmp2 = New-Object System.Drawing.Bitmap $w2, $h2
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.CopyFromScreen($r2.Left, $r2.Top, 0, 0, $bmp2.Size)
$g2.Dispose()
Save-Image $bmp2 $OutFile
$bmp2.Dispose()
Write-Output "OK screen-copy -> $OutFile ($w2 x $h2)"
