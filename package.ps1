#requires -Version 7.0
# 生成便携发布包：release\FastFind-<ver>-win64.zip 或 FastFind-<ver>-win32.zip
param(
    [ValidateSet('x64', 'x86')]
    [string]$Arch = 'x64'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.2.3'
$release = Join-Path $root 'release'
$suffix = if ($Arch -eq 'x86') { 'win32' } else { 'win64' }
$stage = Join-Path $release ("FastFind-{0}-{1}" -f $version, $suffix)

& pwsh -NoProfile -File (Join-Path $root 'build.ps1') -Arch $Arch
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = if ($Arch -eq 'x86') { Join-Path $root 'dist\x86\FastFind.exe' } else { Join-Path $root 'dist\FastFind.exe' }
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先执行 build.ps1 -Arch $Arch" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $root 'README.md') $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'docs\screenshots') | Out-Null
Copy-Item (Join-Path $root 'docs\screenshots\*.png') (Join-Path $stage 'docs\screenshots')
New-Item -ItemType Directory -Force (Join-Path $stage 'data\index') | Out-Null
$archText = if ($Arch -eq 'x86') { '32 位（x86，适用于 32 位 Windows）' } else { '64 位（x64，适用于 64 位 Windows 10/11）' }
@"
FastFind 便携版
================
1. 双击 FastFind.exe 即可运行（无需安装、无需 .NET SDK）。
2. 本包架构：$archText。
3. 首次运行会自动索引本机 NTFS 固定磁盘，完成后在 data\index 生成缓存，之后启动秒开。
4. 全局热键 Ctrl+Alt+Space 唤出/隐藏；Ctrl+T 切换主题。
5. 设置与缓存均在 data\ 目录，整个文件夹可拷贝到任意位置使用。
6. 搜索语法、快捷键与系统要求见 README.md。
"@ | Set-Content (Join-Path $stage '使用说明.txt') -Encoding UTF8

$zip = Join-Path $release ("FastFind-{0}-{1}.zip" -f $version, $suffix)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$size = (Get-Item $zip).Length
"PACKAGE: {0}" -f $zip
"SIZE   : {0:N1} KB" -f ($size / 1KB)
"SHA256 : {0}" -f $hash
"$suffix $hash" | Add-Content (Join-Path $release 'SHA256.txt') -Encoding ASCII
