# Builds the app and the installer.
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
# Output: dist\FormatWheel-Setup-<version>.exe
# ASCII only on purpose - Windows PowerShell 5.1 reads BOM-less files as ANSI.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = '.\src\FormatWheel\FormatWheel.csproj'
[xml]$proj = Get-Content $project -Encoding UTF8
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '0.0.0' }
Write-Host "==> Format Wheel $version" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install it: winget install Microsoft.DotNet.SDK.10 (then reopen the console)'
}

Remove-Item .\publish -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '==> dotnet publish' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:PublishReadyToRun=true -o .\publish
if ($LASTEXITCODE -ne 0) { throw 'Build failed (see errors above).' }

# the SDK must be available to extension authors - pack it
Write-Host '==> dotnet pack SDK' -ForegroundColor Cyan
dotnet pack .\src\FormatWheel.Sdk\FormatWheel.Sdk.csproj -c Release -o .\dist
if ($LASTEXITCODE -ne 0) { throw 'SDK pack failed.' }

function Find-Iscc {
    @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host '==> Inno Setup not found, installing via winget' -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
    $iscc = Find-Iscc
    if (-not $iscc) { throw 'Install Inno Setup 6 manually: https://jrsoftware.org/isdl.php' }
}

Write-Host '==> Building installer' -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "/DPublishDir=$PSScriptRoot\publish" .\installer\FormatWheel.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

Write-Host "==> Done: $(Join-Path $PSScriptRoot "dist\FormatWheel-Setup-$version.exe")" -ForegroundColor Green
