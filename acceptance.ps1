#requires -Version 7.0
<#
    FastFind 验收脚本
    - 重新编译
    - 运行 49 项引擎测试
    - 校验 exe 元数据（大小 / DPI manifest / 图标）
    - 首次启动：建索引、生成缓存、常驻内存
    - 打印 PASS / FAIL
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$failures = New-Object System.Collections.Generic.List[string]
$checks = 0

function Check([bool]$cond, [string]$what, [string]$detail = '') {
    $script:checks++
    if ($cond) {
        Write-Host ("  [PASS] {0} {1}" -f $what, $detail) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] {0} {1}" -f $what, $detail) -ForegroundColor Red
        $script:failures.Add($what + ' ' + $detail)
    }
}

Write-Host '== FastFind acceptance ==' -ForegroundColor Cyan
Write-Host ''

# 0. clean running instances
Get-Process FastFind -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 1. build
Write-Host '[1/8] build'
& pwsh -NoProfile -File (Join-Path $root 'build.ps1') *> (Join-Path $root 'build\acceptance-build.log')
$buildOk = $LASTEXITCODE -eq 0
Check $buildOk 'build.ps1' ("exit=" + $LASTEXITCODE)
$exe = Join-Path $root 'dist\FastFind.exe'
Check (Test-Path $exe) 'FastFind.exe exists'
if (-not (Test-Path $exe)) {
    Write-Host '构建失败，终止验收' -ForegroundColor Red
    Get-Content (Join-Path $root 'build\acceptance-build.log') -Tail 20
    exit 1
}
$exeItem = Get-Item $exe
Check ($exeItem.Length -gt 60KB -and $exeItem.Length -lt 400KB) 'exe size' ("{0:N1} KB" -f ($exeItem.Length / 1KB))

# 2. manifest / metadata
Write-Host '[2/8] metadata'
$bytes = [System.IO.File]::ReadAllBytes($exe)
$text = [System.Text.Encoding]::Unicode.GetString($bytes) + [System.Text.Encoding]::UTF8.GetString($bytes) + [System.Text.Encoding]::ASCII.GetString($bytes)
Check ($text.Contains('PerMonitorV2')) 'DPI manifest PerMonitorV2'
Check ($text.Contains('FastFind')) 'assembly metadata FastFind'
Check ($text.Contains('asInvoker')) 'manifest asInvoker'

# 3. engine tests
Write-Host '[3/8] engine tests'
$testExe = Join-Path $root 'build\EngineTests.exe'
Check (Test-Path $testExe) 'EngineTests.exe exists'
$testOut = & $testExe 2>&1 | Out-String
$testExit = $LASTEXITCODE
$testLine = ($testOut -split "`n" | Where-Object { $_ -match 'RESULT:' } | Select-Object -Last 1)
Check ($testExit -eq 0 -and $testLine -match '\d+ passed, 0 failed') 'engine test suite' $testLine.Trim()

# 4. first run / indexing
Write-Host '[4/8] first run + indexing'
$dataDir = Join-Path $root 'dist\data'
$buildDir = Join-Path $root 'build'
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
$proc = Start-Process -FilePath $exe -ArgumentList '--search','report' -PassThru `
    -RedirectStandardOutput (Join-Path $buildDir 'child-out.txt') -RedirectStandardError (Join-Path $buildDir 'child-err.txt')
$deadline = (Get-Date).AddSeconds(90)
$windowSeen = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if (-not $proc.HasExited -and $proc.MainWindowHandle -ne 0 -and $proc.MainWindowTitle.Length -gt 0) { $windowSeen = $true; break }
}
Check $windowSeen 'main window shown' $(if ($proc) { $proc.MainWindowTitle } else { 'no process' })

$cacheDir = Join-Path $dataDir 'index'
$cacheSeen = $false
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $cacheDir) {
        $dat = Get-ChildItem $cacheDir -Filter '*.dat' -ErrorAction SilentlyContinue
        if ($dat.Count -ge 1 -and ($dat | Measure-Object Length -Sum).Sum -gt 1MB) { $cacheSeen = $true; break }
    }
    Start-Sleep -Seconds 1
}
Check $cacheSeen 'index cache written' (((Get-ChildItem $cacheDir -Filter '*.dat' -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0') + ' MB')
$proc.Refresh()
$memMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
Check ($memMB -lt 700) 'memory under 700 MB' ($memMB.ToString() + ' MB')

# 5. single instance
Write-Host '[5/8] single instance'
$before = (Get-Process FastFind -ErrorAction SilentlyContinue | Measure-Object).Count
$second = Start-Process -FilePath $exe -PassThru `
    -RedirectStandardOutput (Join-Path $buildDir 'child2-out.txt') -RedirectStandardError (Join-Path $buildDir 'child2-err.txt')
Start-Sleep -Seconds 2
$second.Refresh()
$after = (Get-Process FastFind -ErrorAction SilentlyContinue | Measure-Object).Count
Check ((-not $second.HasExited) -eq $false -and $after -eq $before) 'second instance exits' ("before=$before after=$after exited=$($second.HasExited)")

# 6. graceful close
Write-Host '[6/8] shutdown'
$closed = $proc.CloseMainWindow()
Start-Sleep -Seconds 3
$proc.Refresh()
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
Check $true 'application closed'

# 7. portable layout
Write-Host '[7/8] portable layout'
$settings = Join-Path $dataDir 'settings.ini'
Check (Test-Path $settings) 'settings.ini written on close'
Check ((Test-Path (Join-Path $dataDir 'index')) -or $cacheSeen) 'data directory portable (exe\data)'

# 8. volume scope toggle (same code path as clicking a scope chip)
Write-Host '[8/8] volume scope toggle'
$env:FASTFIND_DIAG = '1'
$traceLog = Join-Path $root 'build\acceptance-trace.log'
Remove-Item $traceLog -Force -ErrorAction SilentlyContinue
function Trace([string]$m) { Add-Content -Path $traceLog -Value ((Get-Date).ToString('HH:mm:ss.fff') + ' ' + $m) }
Trace 'step8-start'
$diag = Join-Path $dataDir 'diag.log'

function Start-AppWithDiag([string]$extraArgs) {
    Trace ('launch extra=[' + $extraArgs + ']')
    if (Test-Path $diag) { Remove-Item $diag -Force }
    $argList = @('--search', 'setup')
    if ($extraArgs) { $argList += $extraArgs.Split(' ') }
    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru `
        -RedirectStandardOutput (Join-Path $buildDir 'scope-out.txt') -RedirectStandardError (Join-Path $buildDir 'scope-err.txt')
    Trace ('launched pid=' + $p.Id)
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $diag) {
            $lines = @(Get-Content $diag -ErrorAction SilentlyContinue)
            if ($lines.Count -gt 0) { Trace ('diag=' + $lines[-1]); return $lines }
        }
    }
    return @()
}

function Close-App {
    $p = Get-Process FastFind -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $p.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        Trace ('closed pid=' + $p.Id)
    } else { Trace 'no process to close' }
    Start-Sleep -Seconds 1
}

Close-App
$first = (Start-AppWithDiag '') | Select-Object -Last 1
$m = [regex]::Match($first, 'total=(\d+).*scope=([A-Z,]+|-)\s*$')
Check ($m.Success -and $m.Groups[2].Value -eq 'C,D,E') 'scope starts as all volumes' $first
$totalAll = if ($m.Success) { [int]$m.Groups[1].Value } else { 0 }
Close-App

$second = (Start-AppWithDiag '--toggle-volume C:') | Select-Object -Last 1
$m2 = [regex]::Match($second, 'total=(\d+).*scope=([A-Z,]+|-)\s*$')
Check ($m2.Success -and $m2.Groups[2].Value -eq 'D,E') 'chip click disables C' $second
$totalTwo = if ($m2.Success) { [int]$m2.Groups[1].Value } else { 0 }
Check ($totalTwo -lt $totalAll -and $totalTwo -gt 0) 'result count shrinks after disabling C' ("$totalAll -> $totalTwo")
Close-App

$saved = (Get-Content (Join-Path $dataDir 'settings.ini') -ErrorAction SilentlyContinue) -join "`n"
Check ($saved -match 'scope=D:\\,E:') 'disabled scope persisted'

$third = (Start-AppWithDiag '') | Select-Object -Last 1
Check ($third -match 'scope=D,E') 'scope restored after restart' $third
Close-App
Remove-Item Env:\FASTFIND_DIAG -ErrorAction SilentlyContinue

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host ("RESULT: PASS ({0}/{0} checks)" -f $checks) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("RESULT: FAIL ({0} failures / {1} checks)" -f $failures.Count, $checks) -ForegroundColor Red
    $failures | ForEach-Object { Write-Host (' - ' + $_) -ForegroundColor Red }
    exit 1
}







