# Builds AnyNES with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
param([switch]$Tools)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$core = Get-ChildItem "$root\src\Core" -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$refs = @("/r:$fw\System.IO.Compression.dll", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
$common = @('/nologo', '/optimize+', '/warn:4', '/nowarn:1591', '/langversion:5', '/codepage:65001') + $refs

function Invoke-Csc([string[]]$cscArgs) {
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
}

# 1. Player stub: the generic runtime every generated game exe is made from.
$player = Get-ChildItem "$root\src\Player" -Filter *.cs | ForEach-Object { $_.FullName }
Invoke-Csc ($common + @('/target:winexe', "/out:$out\AnyNesPlayer.exe", "/win32manifest:$root\src\app.manifest") + $core + $player)

# 2. Builder UI, with the player stub embedded as a resource.
$builder = Get-ChildItem "$root\src\Builder" -Filter *.cs | ForEach-Object { $_.FullName }
Invoke-Csc ($common + @('/target:winexe', "/out:$out\AnyNesBuilder.exe", "/win32manifest:$root\src\app.manifest",
    "/win32icon:$root\src\Builder\builder.ico", "/resource:$out\AnyNesPlayer.exe,AnyNes.PlayerStub.exe") + $core + $builder)

if ($Tools) {
    Invoke-Csc ($common + @('/target:exe', "/out:$out\RomTest.exe", "$root\tools\RomTest.cs") + $core)
}
Write-Host "Built to $out"
