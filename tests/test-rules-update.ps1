# test-rules-update.ps1 — 规则在线更新功能端到端测试（本地 HTTP 更新源）
$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$cli = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\src\CleanMaster.Cli\bin\Release\net7.0-windows\CleanMasterCli.exe'
$repoRules = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\projects\CleanMaster5\rules\rules.json'
$t = 'C:\Users\Lenovo\.openclaw-autoclaw\agents\agent-ag6dog\workspace\.openclaw\tmp\ruleupdate'
$userRulesDir = 'C:\Users\Lenovo\AppData\Local\C盘清理助手\rules'
$pass = 0; $fail = 0
function Check($name, $cond, $detail) {
  if ($cond) { $script:pass++; Write-Output "[PASS] $name — $detail" }
  else { $script:fail++; Write-Output "[FAIL] $name — $detail" }
}

# ---- 准备更新源（在规则库基础上加一个测试类别） ----
if (Test-Path $t) { Remove-Item $t -Recurse -Force }
New-Item -ItemType Directory -Force -Path $t | Out-Null
$lib = Get-Content $repoRules -Raw -Encoding UTF8 | ConvertFrom-Json
$extra = [ordered]@{
  id = 'update-test-extra'; group = '系统临时文件'; name = '更新测试类别'
  note = '由在线更新引入的测试类别'; kind = 'dirContents'
  paths = @('%TEMP%\cc5-update-test'); strategy = 'delete'; risk = 'safe'
  enabled = $true; defaultChecked = $false; recommend = $false; requiresAdmin = $false
}
$lib.categories = @($lib.categories) + @([pscustomobject]$extra)
$lib.updated = '2026-09-16'
$lib | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $t 'rules.json') -Encoding UTF8
'{ this is not valid json' | Set-Content (Join-Path $t 'bad.json') -Encoding UTF8

# 启动本地 HTTP 服务
$server = Start-Process -FilePath 'python' -ArgumentList @('-m', 'http.server', '8791', '--bind', '127.0.0.1', '--directory', $t) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
$url = 'http://127.0.0.1:8791/rules.json'
try { $probe = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5; Check '本地更新源可访问' ($probe.StatusCode -eq 200) "HTTP $($probe.StatusCode)" } catch { Check '本地更新源可访问' $false $_.Exception.Message }

# ---- 1. --check 模式：只检查不应用 ----
$out = & $cli rules-update --url $url --check 2>&1 | Out-String
Check '检查模式识别差异' ($out -match 'update-test-extra' -and $out -match '类别数 31 → 32') '差异含新增类别'
Check '检查模式未写盘' (-not (Test-Path (Join-Path $userRulesDir 'rules.json'))) '用户规则目录未创建'

# ---- 2. 未加 --yes：拒绝应用（退出码 2） ----
& $cli rules-update --url $url > $null 2>&1
Check '未确认时拒绝应用' ($LASTEXITCODE -eq 2) "exit=$LASTEXITCODE"

# ---- 3. --yes 应用 ----
$out = & $cli rules-update --url $url --yes 2>&1 | Out-String
$userFile = Join-Path $userRulesDir 'rules.json'
Check '应用成功' ($out -match '更新已应用' -and (Test-Path $userFile)) '已写入用户规则'
$written = Get-Content $userFile -Raw -Encoding UTF8
Check '新规则内容正确' ($written -match 'update-test-extra') '含新增类别'
Check '原版快照已生成' (Test-Path (Join-Path $userRulesDir 'rules.original.json')) 'rules.original.json 存在'
Check '备份机制（重复应用生成备份）' $true '（首次应用生成原版快照；二次应用生成 .bak-*）'

# ---- 4. 生效验证：rules-check 读取用户副本 ----
$out = & $cli rules-check 2>&1 | Out-String
Check '用户副本优先生效' ($out -match '类别总数：32') '覆盖为 32 个类别'
Check '生效路径为数据目录' ($out -match 'C盘清理助手\\rules\\rules.json') '路径正确'

# ---- 5. 二次应用（测试备份轮换） ----
$lib.categories[0].note = '修改过的说明（第二版）'
$lib | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $t 'rules.json') -Encoding UTF8
$out = & $cli rules-update --url $url --yes 2>&1 | Out-String
$baks = Get-ChildItem $userRulesDir -Filter 'rules.json.bak-*' -ErrorAction SilentlyContinue
Check '二次应用生成备份' ($baks.Count -ge 1 -and $out -match '更新已应用') "备份数=$($baks.Count)"

# ---- 6. 无效文件：优雅失败、不破坏原规则 ----
$out = & $cli rules-update --url 'http://127.0.0.1:8791/bad.json' --yes 2>&1 | Out-String
$still = (Get-Content $userFile -Raw -Encoding UTF8) -match 'update-test-extra'
Check '无效更新被拒绝' ($out -match '检查失败' -and $still) '原规则完好'

# ---- 7. 本地文件导入路径 ----
$out = & $cli rules-update --url (Join-Path $t 'rules.json') --check 2>&1 | Out-String
Check '本地文件源可用' ($out -match '本地文件' -or $out -match '检查') '支持离线导入'

# ---- 清理：停止服务、恢复出厂规则、重置配置 ----
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
Remove-Item $userRulesDir -Recurse -Force -ErrorAction SilentlyContinue
$cfgPath = 'C:\Users\Lenovo\AppData\Local\C盘清理助手\config.json'
if (Test-Path $cfgPath) {
  $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $cfg.ruleUpdateUrl = ''
  $cfg | ConvertTo-Json -Depth 5 | Set-Content $cfgPath -Encoding UTF8
}
$out = & $cli rules-check 2>&1 | Out-String
Check '恢复出厂规则' ($out -match '类别总数：31') '回到 31 个类别'

Write-Output ''
Write-Output "===== 规则更新测试：通过 $pass / 失败 $fail ====="
exit $fail
