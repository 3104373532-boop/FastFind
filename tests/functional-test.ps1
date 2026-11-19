$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:FASTFIND_DIAG = '1'
$exe = Join-Path $root 'dist\FastFind.exe'
$data = Join-Path $root 'dist\data'
$diag = Join-Path $data 'diag.log'
$buildDir = Join-Path $root 'build\uitest'
$driver = Join-Path $root 'build\UiDriver.exe'
if (-not (Test-Path $driver)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /platform:x64 ('/out:' + $driver) /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'tools\UiDriver.cs') | Out-Null
}
New-Item -ItemType Directory -Force $buildDir | Out-Null
$out = Join-Path $buildDir 'child-out.txt'
$err = Join-Path $buildDir 'child-err.txt'
$script:fail = 0
$script:pass = 0

Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class DlgClean {
  public delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static int CountErrors() {
    int n = 0;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256); string t = sb.ToString();
      if (t.IndexOf("Application Error", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("应用程序错误", StringComparison.OrdinalIgnoreCase) >= 0) n++;
      return true;
    }, IntPtr.Zero);
    return n;
  }
  public static int CloseAll() {
    int n = 0;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256); string t = sb.ToString();
      if (t.IndexOf("Application Error", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("应用程序错误", StringComparison.OrdinalIgnoreCase) >= 0 || t == "FastFind") {
        PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); n++;
      }
      return true;
    }, IntPtr.Zero);
    return n;
  }
}
"@ -ErrorAction SilentlyContinue

function Check([bool]$cond, [string]$name, [string]$detail) {
    if ($cond) { $script:pass++; Write-Output ("  [PASS] {0} {1}" -f $name, $detail) }
    else { $script:fail++; Write-Output ("  [FAIL] {0} {1}" -f $name, $detail) }
}

function Close-App {
    $p = Get-Process FastFind -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $p.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 2
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
    Start-Sleep -Milliseconds 500
}

function Launch([string]$query) {
    if (Test-Path $diag) { Remove-Item $diag -Force }
    return Start-Process -FilePath $exe -ArgumentList ('--search "' + $query + '"') -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
}

function Last-Diag { return (Get-Content $diag -ErrorAction SilentlyContinue | Select-Object -Last 1) }

function Query-Total([string]$query) {
    Close-App
    $p = Launch $query
    $line = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $line = Last-Diag
        if ($line) { break }
    }
    Close-App
    if (-not $line) { return -1 }
    $m = [regex]::Match($line, 'total=(\d+)')
    if ($m.Success) { [int]$m.Groups[1].Value } else { -1 }
}

[void][DlgClean]::CloseAll()
Get-Process FastFind -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$ini = Join-Path $data 'settings.ini'
if (Test-Path $ini) { (Get-Content $ini) | Where-Object { $_ -notmatch '^scope=' } | Set-Content $ini -Encoding UTF8 }

Write-Output '== 1. Query syntax =='
$setup = Query-Total 'setup'
Check ($setup -gt 1000) 'plain term' ("setup=$setup")
$wild = Query-Total '*.log -tmp'
Check ($wild -gt 0) 'wildcard + exclusion' ("*.log -tmp=$wild")
$rx = Query-Total '/^setup/'
Check ($rx -gt 0) 'regex' ("/^setup/=$rx")
$pathExt = Query-Total 'path:python ext:py'
Check ($pathExt -gt 0) 'path + ext' ("path:python ext:py=$pathExt")
$today = Query-Total 'dm:2026-11'
Check ($today -gt 0) 'date filter' ("dm:2026-11=$today")
$dirs = Query-Total 'type:dir setup'
Check ($dirs -gt 0 -and $dirs -lt $setup) 'type:dir narrows' ("$dirs < $setup")
$big = Query-Total 'size:>500mb'
Check ($big -ge 0) 'size filter' ("size:>500mb=$big")
$and = Query-Total 'setup ext:md'
Check ($and -gt 0 -and $and -lt $setup) 'AND semantics' ("setup ext:md=$and < $setup")

Write-Output ''
Write-Output '== 2. Auto refresh + keyboard actions (no foreground needed) =='
$stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
$copyTarget = Join-Path $buildDir ("ffprobe-copy-$stamp.txt")
$delTarget = Join-Path $buildDir ("ffprobe-del-$stamp.txt")
Set-Content $copyTarget 'copy probe' -Encoding UTF8
Set-Content $delTarget 'delete probe' -Encoding UTF8

$p = Launch ('ffprobe-copy-' + $stamp)
$hitLine = $null
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $line = Last-Diag
    if ($line -match 'total=[1-9]') { $hitLine = $line; break }
    if ($line -and $line -match "ffprobe-copy-$stamp") { $hitLine = $line }
}
Check ($hitLine -match 'total=[1-9]') 'watcher auto-refresh indexes new file' ([string]$hitLine)

& $driver $p.Id copy | Out-Null
Start-Sleep -Milliseconds 500
$clip = Get-Clipboard -ErrorAction SilentlyContinue
Check ($clip -and $clip.ToString().IndexOf([System.IO.Path]::GetFileName($copyTarget), [StringComparison]::OrdinalIgnoreCase) -ge 0) 'Ctrl+C copies full path' ([string]$clip)
Close-App

$p = Launch ('ffprobe-del-' + $stamp)
Start-Sleep -Seconds 4
& $driver $p.Id delete | Out-Null
Start-Sleep -Milliseconds 800
Check (-not (Test-Path $delTarget)) 'Del sends file to recycle bin' $delTarget
Close-App

Write-Output ''
Write-Output '== 3. Open action never fails silently =='
$p = Launch ('ffprobe-copy-' + $stamp)
Start-Sleep -Seconds 4
$openOut = & $driver $p.Id open
$errWindows = [DlgClean]::CountErrors()
$dialog = [DlgClean]::CloseAll()
Check ($errWindows -eq 0) 'no crash/error dialog after open' ("errorWindows=$errWindows")
$notepad = (Get-Process Notepad -ErrorAction SilentlyContinue | Measure-Object).Count
$diagLine = Get-Content $diag -ErrorAction SilentlyContinue | Where-Object { $_ -match 'open-error' } | Select-Object -Last 1
$dialogVisible = ($openOut -join ' ') -match 'dialog=[1-9]'
Check ($dialogVisible -or $notepad -gt 0) 'open shows dialog or opens app' ("$($openOut -join '; ')")
if ($dialogVisible) { Check ($diagLine -ne $null) 'failure reason recorded' ([string]$diagLine) }
Close-App

[void][DlgClean]::CloseAll()
Remove-Item $copyTarget -Force -ErrorAction SilentlyContinue
Write-Output ''
Write-Output ("PASS={0} FAIL={1}" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0


