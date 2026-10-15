# Builds the Clicky installer: publishes the self-contained app (publish-dist\) and compiles
# installer\Clicky.iss into dist\Clicky-<version>-Setup.exe. Version comes from ClickyWindows.csproj.
# -Install also upgrades the copy installed on this PC in place (same AppId): settings, keys,
# desktop icon and autostart are kept (Inno reuses the previous tasks), and Clicky is restarted.
param([switch]$Install)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = ([xml](Get-Content "ClickyWindows.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in ClickyWindows.csproj" }

$iscc = @($env:ISCC, "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

if (Test-Path "publish-dist") { Remove-Item "publish-dist" -Recurse -Force }
dotnet publish -p:PublishProfile=Dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

& $iscc /Q "/DAppVersion=$version" "installer\Clicky.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-Item "dist\Clicky-$version-Setup.exe" | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }

if ($Install) {
    $setup = Resolve-Path "dist\Clicky-$version-Setup.exe"
    $p = Start-Process $setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Installer exit code $($p.ExitCode)" }
    Start-Process "$env:LOCALAPPDATA\Programs\Clicky\ClickyWindows.exe"
    "Installed $version and restarted Clicky"
}
