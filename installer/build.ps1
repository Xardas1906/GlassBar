# Baut ein Release: veröffentlicht GlassBar, packt das Zip (für den Update-Knopf) und den Installer.
# Aufruf aus dem Repo-Ordner:  powershell -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content "GlassBar\GlassBar.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "Keine <Version> in GlassBar.csproj" }

$out = "publish\GlassBar-$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish GlassBar\GlassBar.csproj -c Release -r win-x64 --self-contained false -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }
Get-ChildItem $out -Filter *.pdb | Remove-Item

$zip = "publish\GlassBar-v$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 nicht gefunden (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$version" "installer\GlassBar.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC fehlgeschlagen" }

Get-Item $zip, "publish\GlassBar-Setup-$version.exe" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
