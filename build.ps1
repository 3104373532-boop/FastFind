$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 4 编译器 csc.exe" }

$dist = Join-Path $root 'dist'
$build = Join-Path $root 'build'
New-Item -ItemType Directory -Force $dist | Out-Null
New-Item -ItemType Directory -Force $build | Out-Null

$version = '1.2.2'
$coreSrc = @(Get-ChildItem (Join-Path $root 'src\Core\*.cs') | ForEach-Object { $_.FullName })
$uiSrc = @(Get-ChildItem (Join-Path $root 'src\UI\*.cs') | ForEach-Object { $_.FullName })

$assemblyInfo = Join-Path $build 'AssemblyInfo.cs'
$ai = @"
using System.Reflection;
[assembly: AssemblyTitle("FastFind")]
[assembly: AssemblyDescription("Windows 极速文件查找器")]
[assembly: AssemblyProduct("FastFind")]
[assembly: AssemblyCompany("FastFind")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@
[System.IO.File]::WriteAllText($assemblyInfo, $ai, (New-Object System.Text.UTF8Encoding($false)))

$icon = Join-Path $build 'fastfind.ico'
if (-not (Test-Path $icon)) {
    try { & (Join-Path $root 'tools\make-icon.ps1') -IconPath $icon } catch { Write-Warning ("图标生成失败: " + $_) }
}

$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
$manifest = Join-Path $root 'src\app.manifest'
$common = @('/nologo', '/platform:x64', '/unsafe', '/optimize+')

$exe = Join-Path $dist 'FastFind.exe'
Write-Host "编译 FastFind..." -ForegroundColor Cyan
$outArg = ('/out:' + $exe)
$manifestArg = ('/win32manifest:' + $manifest)
$exeArgs = @() + $common + @('/target:winexe', $outArg, $manifestArg)
if (Test-Path $icon) { $exeArgs += ('/win32icon:' + $icon) }
$exeArgs += $refs
$exeArgs += $coreSrc
$exeArgs += $uiSrc
$exeArgs += $assemblyInfo

& $csc @exeArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }
$exeItem = Get-Item $exe
Write-Host ("完成: {0} ({1:N1} KB)" -f $exeItem.FullName, ($exeItem.Length / 1KB)) -ForegroundColor Green

$testExe = Join-Path $build 'EngineTests.exe'
Write-Host "编译引擎测试..." -ForegroundColor Cyan
$testArgs = @() + $common + @('/target:exe', ('/out:' + $testExe), '/r:System.dll', '/r:System.Core.dll')
$testArgs += $coreSrc
$testArgs += (Join-Path $root 'tests\EngineTests.cs')
& $csc @testArgs
if ($LASTEXITCODE -ne 0) { throw "测试编译失败 (exit $LASTEXITCODE)" }
Write-Host ("完成: {0}" -f $testExe) -ForegroundColor Green

$benchExe = Join-Path $build 'IndexBench.exe'
Write-Host "编译索引基准..." -ForegroundColor Cyan
$benchArgs = @() + $common + @('/target:exe', ('/out:' + $benchExe), '/r:System.dll', '/r:System.Core.dll')
$benchArgs += $coreSrc
$benchArgs += (Join-Path $root 'tools\IndexBench.cs')
& $csc @benchArgs
if ($LASTEXITCODE -ne 0) { throw "基准编译失败 (exit $LASTEXITCODE)" }
Write-Host ("完成: {0}" -f $benchExe) -ForegroundColor Green



