# Builds Rewind.exe and rewind-tests.exe with the C# compiler that ships inside Windows
# (no Visual Studio needed), then runs the tests. Usage:  powershell -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:Microsoft.VisualBasic.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
# System.Speech (the "clip that" voice trigger) lives in the GAC, not next to csc, so it needs its full path.
$speech = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Recurse -Filter 'System.Speech.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $speech) { throw "System.Speech.dll not found in the GAC (it ships with .NET Framework 4.x)" }
$refs += "/r:$($speech.FullName)"
$src = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

# Tray icon file for the exe itself (Explorer, Start menu); the tray icon is drawn at runtime.
$ico = Join-Path $root 'rewind.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 40, 40))), 3, 3, 26, 26)
    $g.DrawArc((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.5), 9, 9, 14, 14, 20, 300)
    $g.Dispose()
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $stream = [System.IO.File]::Create($ico)
    $icon.Save($stream)
    $stream.Close()
}

Write-Host "Building Rewind.exe"
& $csc /nologo /target:winexe /optimize+ /warn:4 "/win32icon:$ico" "/out:$root\Rewind.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "Rewind.exe build failed" }

Write-Host "Building rewind-tests.exe"
$testSrc = $src | Where-Object { $_ -notmatch '\\(Program|TrayApp|HotkeyWindow|ClipsForm|ClipGrid|SettingsTab|TrimForm|Toast)\.cs$' }
& $csc /nologo /target:exe /optimize+ /warn:4 "/out:$root\rewind-tests.exe" $refs $testSrc (Join-Path $root 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw "tests build failed" }

Write-Host "Running tests"
& (Join-Path $root 'rewind-tests.exe')
exit $LASTEXITCODE
