#requires -Version 7.0
# 生成便携发布包：release\FastFind-1.0.0-win64.zip
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.2.2'
$release = Join-Path $root 'release'
$stage = Join-Path $release ("FastFind-{0}-win64" -f $version)

& pwsh -NoProfile -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'dist\FastFind.exe') $stage
Copy-Item (Join-Path $root 'README.md') $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'docs\screenshots') | Out-Null
Copy-Item (Join-Path $root 'docs\screenshots\*.png') (Join-Path $stage 'docs\screenshots')
New-Item -ItemType Directory -Force (Join-Path $stage 'data\index') | Out-Null
@'
FastFind 便携版
================
1. 双击 FastFind.exe 即可运行（无需安装、无需 .NET SDK）。
2. 首次运行会自动索引本机 NTFS 固定磁盘，完成后在 data\index 生成缓存，之后启动秒开。
3. 全局热键 Ctrl+Alt+Space 唤出/隐藏；Ctrl+T 切换主题。
4. 设置与缓存均在 data\ 目录，整个文件夹可拷贝到任意位置使用。
5. 搜索语法与快捷键见 README.md。
'@ | Set-Content (Join-Path $stage '使用说明.txt') -Encoding UTF8

$zip = Join-Path $release ("FastFind-{0}-win64.zip" -f $version)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$size = (Get-Item $zip).Length
"PACKAGE: {0}" -f $zip
"SIZE   : {0:N1} KB" -f ($size / 1KB)
"SHA256 : {0}" -f $hash
$hash | Set-Content (Join-Path $release 'SHA256.txt') -Encoding ASCII





