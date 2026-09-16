# C盘清理助手 v5.0.0 — 构建安装包脚本
#
# 用法（在项目根目录或本目录执行均可）:
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
# 可选参数:
#   -ProjectRoot <路径>   项目根目录（默认自动推断）
#   -DocsSrc   <路径>     文档来源目录（默认 <项目根>\docs）
#   -SkipPublish          跳过 dotnet publish（复用现有 publish\app / publish\cli）
#
# 前置条件:
#   - .NET SDK 7.0+（需要重新发布时）
#   - Inno Setup 6（默认查找路径可被下面的 $IsccCandidates 覆盖）

param(
    [string]$ProjectRoot,
    [string]$DocsSrc,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $ProjectRoot) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }

$IsccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) { throw '未找到 Inno Setup 6 的 ISCC.exe，请先安装 Inno Setup 6。' }

Write-Output "== [1/5] 检查发布产物 =="
$appPub = Join-Path $ProjectRoot 'publish\app'
$cliPub = Join-Path $ProjectRoot 'publish\cli'
if (-not (Test-Path (Join-Path $appPub 'C盘清理助手.exe'))) {
    if ($SkipPublish) { throw "publish\app 缺失且指定了 -SkipPublish。" }
    Write-Output '  publish 目录缺失，执行 dotnet publish ...'
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
    $env:DOTNET_ROOT = 'C:\Program Files\dotnet'
    & $dotnet publish (Join-Path $ProjectRoot 'src\CleanMaster.App') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -o $appPub
    if ($LASTEXITCODE -ne 0) { throw 'App 发布失败' }
    & $dotnet publish (Join-Path $ProjectRoot 'src\CleanMaster.Cli') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -o $cliPub
    if ($LASTEXITCODE -ne 0) { throw 'CLI 发布失败' }
} else {
    Write-Output '  使用现有 publish 产物。'
}

Write-Output "== [2/5] 准备文档负载 =="
if (-not $DocsSrc) { $DocsSrc = Join-Path $ProjectRoot 'docs' }
$payloadDocs = Join-Path $PSScriptRoot 'payload\docs'
New-Item -ItemType Directory -Force -Path $payloadDocs | Out-Null
# 复制文档（排除原始数据与部署说明，保持面向用户的整洁）
robocopy $DocsSrc $payloadDocs /E /XD data /XF README-deploy.txt /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
Write-Output ("  已暂存文档: {0} 个文件" -f (Get-ChildItem $payloadDocs -Recurse -File).Count)

Write-Output "== [3/5] 规范化文件编码（UTF-8 BOM）=="
function Ensure-Bom([string]$p) {
    if (-not (Test-Path $p)) { return }
    $bytes = [IO.File]::ReadAllBytes($p)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
        [IO.File]::WriteAllBytes($p, ([byte[]](0xEF, 0xBB, 0xBF) + $bytes))
        Write-Output "  已补写 BOM: $(Split-Path -Leaf $p)"
    }
}
Ensure-Bom (Join-Path $PSScriptRoot 'setup.iss')
Ensure-Bom (Join-Path $PSScriptRoot '安装前须知.txt')
Ensure-Bom (Join-Path $PSScriptRoot 'lang\ChineseSimplified.isl')

Write-Output "== [4/5] 编译安装包（Inno Setup）=="
& $Iscc (Join-Path $PSScriptRoot 'setup.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败，退出码 $LASTEXITCODE" }

Write-Output "== [5/5] 产物清单 =="
$outDir = Join-Path $PSScriptRoot 'out'
Get-ChildItem "$outDir\*.exe" | Sort-Object LastWriteTime -Descending | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Output ("  {0}" -f $_.FullName)
    Write-Output ("  大小: {0:N1} MB   SHA256: {1}" -f ($_.Length / 1MB), $hash)
}
