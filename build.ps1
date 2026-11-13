# Builds bin\Orbit.exe with the C# compiler that ships with Windows (.NET Framework 4.8); no SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'Orbit.exe'
$ico = Join-Path $root 'assets\orbit.ico'

New-Item -ItemType Directory -Force $bin, (Split-Path $ico) | Out-Null
if (-not (Test-Path $ico)) {
    python -I (Join-Path $root 'tools\make_icon.py') $ico
    if ($LASTEXITCODE) { throw 'Icon generation failed' }
}

# A running Orbit locks the exe: ask it to quit first.
$running = Get-Process Orbit -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    & $exe --quit
    $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /optimize+ /warn:4 `
    "/out:$exe" "/win32icon:$ico" `
    "/resource:$(Join-Path $root 'src\Theme.xaml'),Orbit.Theme.xaml" `
    "/resource:$ico,Orbit.orbit.ico" `
    "/r:$(Join-Path $wpf 'PresentationFramework.dll')" `
    "/r:$(Join-Path $wpf 'PresentationCore.dll')" `
    "/r:$(Join-Path $wpf 'WindowsBase.dll')" `
    "/r:$(Join-Path $fw 'System.Xaml.dll')" `
    /r:System.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Xml.dll `
    /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll `
    $sources
if ($LASTEXITCODE) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $exe"
