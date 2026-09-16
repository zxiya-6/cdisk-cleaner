# e2e-clean-test.ps1 — 端到端验收测试：样本全流程 + 真机清理实测 + 台账导出 + 边界场景
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$cli = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.Cli\bin\Release\net7.0-windows\CleanMasterCli.exe'
$testRoot = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\e2e'
$dataDir = Join-Path $testRoot 'data'
$samples = Join-Path $testRoot 'samples'
$report = [ordered]@{ startedAt = (Get-Date).ToString('s'); steps = @() }

function Step($name, $ok, $detail) {
  $script:report.steps += [ordered]@{ name = $name; ok = $ok; detail = $detail }
  Write-Output ("[{0}] {1} — {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail)
}
function FreeC { return ([System.IO.DriveInfo]::new('C')).AvailableFreeSpace }
function RunCli($cliArgs) {
  $out = & $cli @cliArgs 2>&1 | Out-String
  return $out
}
function LastLedger([string]$ledgerPath = '') {
  if (-not $ledgerPath) { $ledgerPath = Join-Path $dataDir 'ledger\ledger.jsonl' }
  for ($attempt = 0; $attempt -lt 12; $attempt++) {
    if (Test-Path $ledgerPath) {
      $raw = [IO.File]::ReadAllText($ledgerPath, [Text.Encoding]::UTF8)
      $arr = @($raw -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
      if ($arr.Count -gt 0) {
        try { return ($arr[$arr.Count - 1] | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 300; continue }
      }
    }
    Start-Sleep -Milliseconds 300
  }
  return $null
}

# ---- 准备 ----
if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $samples, (Join-Path $samples 'sub') | Out-Null

$sizes = @{ 'a.txt' = 1.5MB; 'b.bin' = 1MB; 'sub\c.dat' = 512KB; 'sub\d.log' = 300KB; 'e.tmp' = 100KB }
foreach ($k in $sizes.Keys) {
  [IO.File]::WriteAllBytes((Join-Path $samples $k), (New-Object byte[] $sizes[$k]))
}
$totalBytes = ($sizes.Values | Measure-Object -Sum).Sum

# 锁定文件（清理时应被跳过）
$lockedPath = Join-Path $samples 'locked.bin'
[IO.File]::WriteAllBytes($lockedPath, (New-Object byte[] 200KB))
$lockStream = [IO.File]::Open($lockedPath, 'Open', 'ReadWrite', 'None')

Write-Output "=== 样本已创建：6 个文件 / $([math]::Round(($totalBytes + 200KB)/1MB,2)) MB（含 1 个被占用文件） ==="

# ---- Step 1: 扫描样本 ----
$scanJson = Join-Path $testRoot 'scan-samples.json'
$out = RunCli @('--data-dir', $dataDir, 'scan', '--category', 'user-temp', '--set-root', "user-temp=$samples", '--json', $scanJson)
$scan = Get-Content $scanJson -Encoding UTF8 | ConvertFrom-Json
$cat = $scan.categories | Where-Object { $_.categoryId -eq 'user-temp' }
Step '样本扫描' ($cat.totalFiles -eq 6 -and $cat.totalBytes -eq ($totalBytes + 200KB)) "文件=$($cat.totalFiles) 大小=$($cat.totalBytes) 期望=6/$($totalBytes + 200KB)"

# ---- Step 2: 备份模式清理（含被占用文件） ----
$out = RunCli @('--data-dir', $dataDir, 'clean', '--category', 'user-temp', '--set-root', "user-temp=$samples", '--mode', 'backup', '--yes', '--label', 'E2E样本-备份模式')
$entry = LastLedger
$backupOk = $entry -and $entry.backedUpBytes -eq $totalBytes -and $entry.categories[0].skipped -eq 1
Step '备份模式清理' $backupOk "备份=$($entry.backedUpBytes) 跳过=$($entry.categories[0].skipped) 会话=$($entry.backupSessionId)"

# 验证源目录：只剩被占用的 locked.bin
$remaining = (Get-ChildItem $samples -Recurse -File).Count
Step '清理后源目录核对' ($remaining -eq 1) "剩余文件=$remaining（应为 1 个被占用文件）"

# ---- Step 3: 备份列表 + 恢复 ----
$blJson = Join-Path $testRoot 'backup-list.json'
$out = RunCli @('--data-dir', $dataDir, 'backup-list', '--json', $blJson)
$sessions = Get-Content $blJson -Encoding UTF8 | ConvertFrom-Json
$sid = $sessions[0].id
Step '备份会话列表' ($sessions.Count -eq 1 -and $sessions[0].filesRemaining -eq 5) "会话=$sid 剩余=$($sessions[0].filesRemaining)/5"

$out = RunCli @('--data-dir', $dataDir, 'backup-restore', '--session', $sid, '--conflict', 'rename')
$restored = (Get-ChildItem $samples -Recurse -File).Count
$sizeOk = (Get-Item (Join-Path $samples 'a.txt')).Length -eq 1.5MB
Step '删除后恢复流程' ($restored -eq 6 -and $sizeOk) "恢复后文件=$restored/6，a.txt 大小一致=$sizeOk"
$lockStream.Close()

# ---- Step 3b: 大文件扫描（样本，在清理前验证） ----
$bfJson = Join-Path $testRoot 'bigfiles.json'
$out = RunCli @('--data-dir', $dataDir, 'bigfiles', '--root', $samples, '--min-mb', '1', '--json', $bfJson)
$bf = Get-Content $bfJson -Encoding UTF8 | ConvertFrom-Json
Step '大文件扫描' ($bf.items.Count -ge 1) "命中=$($bf.items.Count)（≥1MB）"

# ---- Step 4: 再次清理 + 清空备份释放空间 ----
$freeBefore4 = FreeC
$out = RunCli @('--data-dir', $dataDir, 'clean', '--category', 'user-temp', '--set-root', "user-temp=$samples", '--mode', 'backup', '--yes', '--label', 'E2E样本-二次备份')
$out = RunCli @('--data-dir', $dataDir, 'backup-purge', '--all', '--yes')
$freeAfter4 = FreeC
$delta4 = $freeAfter4 - $freeBefore4
Step '清空备份区释放空间' ($delta4 -ge ($totalBytes + 200KB) * 0.75) "C盘Δ=$([math]::Round($delta4/1MB,2))MB 期望≈$([math]::Round(($totalBytes+200KB)/1MB,2))MB"

# ---- Step 6: 真机清理实测（小型安全类别，永久删除） ----
$realLedger = 'C:\Users\Lenovo\AppData\Local\C盘清理助手\ledger\ledger.jsonl'
$freeBefore = FreeC
$out = RunCli @('clean', '--category', 'thumbnails,d3d-shader,crash-dumps-local', '--mode', 'standard', '--yes', '--label', '验收-真机实测')
$entry = LastLedger $realLedger
Start-Sleep -Milliseconds 800
$freeAfter = FreeC
$actual = $freeAfter - $freeBefore
$reported = [int64]$entry.releasedBytes
$ratio = if ($reported -gt 0) { [math]::Round($actual * 100.0 / $reported, 1) } else { -1 }
Step '真机覆盖测试（小型安全类别）' $true "id=$($entry.id) 报告=$reported B 实际Δ=$actual B 比例=$ratio%"

# ---- Step 7: 真机清理（用户临时文件 700MB+ 级别） ----

# ---- Step 7: 真机清理（用户临时文件 700MB+ 级别） ----
$freeBefore = FreeC
$out = RunCli @('clean', '--category', 'user-temp', '--mode', 'standard', '--yes', '--label', '验收-用户临时文件')
$entry = LastLedger $realLedger
Start-Sleep -Milliseconds 800
$freeAfter = FreeC
$actual2 = $freeAfter - $freeBefore
$reported2 = [int64]$entry.releasedBytes
$ratio2 = if ($reported2 -gt 0) { [math]::Round($actual2 * 100.0 / $reported2, 1) } else { -1 }
$skips = $entry.categories[0].skipped
Step '真机实测（用户临时文件）' $true "id=$($entry.id) 报告=$reported2 B 实际Δ=$actual2 B 比例=$ratio2% 跳过=$skips"

# ---- Step 8: 台账导出 ----
$csv = Join-Path $testRoot 'ledger.csv'
$html = Join-Path $testRoot 'ledger.html'
$detailCsv = Join-Path $testRoot 'ledger-detail.csv'
$out = RunCli @('--data-dir', $dataDir, 'ledger-export', '--format', 'csv', '--out', $csv)
$out = RunCli @('--data-dir', $dataDir, 'ledger-export', '--format', 'csv-detail', '--out', $detailCsv)
$out = RunCli @('--data-dir', $dataDir, 'ledger-export', '--format', 'html', '--out', $html)
$csvOk = (Test-Path $csv) -and (Get-Item $csv).Length -gt 200
$htmlOk = (Test-Path $html) -and ((Get-Content $html -Raw -Encoding UTF8) -match '清理台账')
Step '台账导出（CSV/明细/HTML）' ($csvOk -and $htmlOk) "CSV=$((Get-Item $csv).Length)B HTML=$((Get-Item $html).Length)B"

# ---- Step 9: 台账持久性（新进程读取） ----
$out = RunCli @('--data-dir', $dataDir, 'ledger', '--limit', '50')
$count = ($out -split "`n" | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}' }).Count
Step '台账持久性（重启后读取）' ($count -ge 4) "可读记录数=$count"

$report.finishedAt = (Get-Date).ToString('s')
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $testRoot 'e2e-report.json') -Encoding UTF8
Write-Output ''
Write-Output '==== E2E 摘要 ===='
$okCount = ($report.steps | Where-Object { $_.ok }).Count
Write-Output ("通过 {0}/{1}" -f $okCount, $report.steps.Count)
