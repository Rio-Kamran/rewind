# Builds Rewind.exe and rewind-tests.exe with the C# compiler that ships inside Windows
# (no Visual Studio needed), then runs the tests. Usage:  powershell -File build.ps1
# -Version 2.2.0 stamps that version instead of the git tag (for trying the auto-updater on an old build).
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:Microsoft.VisualBasic.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:System.Web.Extensions.dll')
# System.Speech (the "clip that" voice trigger) lives in the GAC, not next to csc, so it needs its full path.
$speech = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Recurse -Filter 'System.Speech.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $speech) { throw "System.Speech.dll not found in the GAC (it ships with .NET Framework 4.x)" }
$refs += "/r:$($speech.FullName)"
$src = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

# The version the auto-updater compares against GitHub's latest release. It comes from the git tag:
# the tag being built in CI (GITHUB_REF_NAME), else `git describe --tags` here, else 0.0.0 (an
# unstamped build never updates itself). Written to obj\Version.cs, which isn't checked in.
function Get-BuildVersion {
    if ($Version) { return $Version }
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME) { return $env:GITHUB_REF_NAME }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue' # no tags yet is not an error, just an unstamped build
    try {
        $described = & git -C $root describe --tags 2>$null
        if ($LASTEXITCODE -eq 0 -and $described) { return "$described".Trim() }
    } catch {
        Write-Host "git describe failed: $($_.Exception.Message)"
    } finally {
        $ErrorActionPreference = $previous
    }
    return '0.0.0'
}
$label = (Get-BuildVersion) -replace '^[vV]', ''
if ($label -notmatch '^[0-9A-Za-z.+-]+$' -or $label -notmatch '^(\d+)\.(\d+)(\.(\d+))?') { throw "Version '$label' doesn't look like 2.2.4" }
$patch = if ($Matches[4]) { $Matches[4] } else { '0' }
$numeric = '{0}.{1}.{2}.0' -f $Matches[1], $Matches[2], $patch
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$versionFile = Join-Path $obj 'Version.cs'
@"
// Written by build.ps1 from the git tag; not checked in.
using System.Reflection;

[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$label")]
"@ | Set-Content -Encoding UTF8 $versionFile
$src += $versionFile
Write-Host "Version $label ($numeric)"

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
$tests = Get-ChildItem (Join-Path $root 'tests\*.cs') | ForEach-Object { $_.FullName }
& $csc /nologo /target:exe /optimize+ /warn:4 "/out:$root\rewind-tests.exe" $refs $testSrc $tests
if ($LASTEXITCODE -ne 0) { throw "tests build failed" }

Write-Host "Running tests"
& (Join-Path $root 'rewind-tests.exe')
exit $LASTEXITCODE
