param(
    [string]$Dotnet = '',
    [string]$TexRoot = '',
    [switch]$SkipBuild,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PATHEXT = '.COM;.EXE;.BAT;.CMD'
$root = Split-Path $PSScriptRoot -Parent
$workspace = Join-Path $root 'artifacts/dev'
$toolsRoot = Join-Path $env:LOCALAPPDATA 'HjfyBuild'
if (!$Dotnet) {
    $localDotnet = Join-Path $toolsRoot 'dotnet/dotnet.exe'
    if (Test-Path $localDotnet) { $Dotnet = $localDotnet } else { $Dotnet = 'dotnet' }
}
if (!$env:NUGET_PACKAGES -and (Test-Path (Join-Path $toolsRoot 'nuget'))) {
    $env:NUGET_PACKAGES = Join-Path $toolsRoot 'nuget'
}
$appDirectory = Join-Path $root 'artifacts/bin/NoHalluTrans.Desktop/x64/Release/net10.0-windows10.0.19041.0/win-x64'
$exe = Join-Path $appDirectory 'NoHalluTrans.exe'
$entry = Join-Path $workspace 'NoHalluTrans.exe'
$appLink = Join-Path $workspace 'app'
$linkedExe = Join-Path $appLink 'NoHalluTrans.exe'
$running = @(Get-Process HJFY -ErrorAction SilentlyContinue | Where-Object { $_.Path -in @($exe, $linkedExe, $entry) })
if (!$SkipBuild -and $running.Count -gt 0) {
    throw 'The local test app is already open. Close its window, then try again.'
}
if (!$SkipBuild) {
    Write-Host 'Building the local test app (no publish or ZIP)...'
    & $Dotnet build (Join-Path $root 'src/NoHalluTrans.Desktop/NoHalluTrans.Desktop.csproj') -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. The test app was not started.' }
}
if (!(Test-Path $exe)) { throw 'No test build found. Run again without -SkipBuild.' }
if (!$TexRoot) { $TexRoot = Join-Path $toolsRoot 'texlive' }
if (!(Test-Path (Join-Path $TexRoot 'hjfy-runtime.json')) -or !(Test-Path (Join-Path $TexRoot 'bin/windows/xelatex.exe'))) {
    throw 'Local TeX runtime is missing. Run Prepare-TeX.ps1 first, or pass -TexRoot with an existing runtime.'
}
# Temporary development entry only; all generated files stay under artifacts.
New-Item -ItemType Directory -Force $workspace | Out-Null
if (Test-Path $appLink) {
    $existingApp = Get-Item $appLink -Force
    if ($existingApp.LinkType -ne 'Junction' -or [string]$existingApp.Target -ine $appDirectory) {
        throw "The app entry already points elsewhere: $appLink. It was left untouched."
    }
} else {
    New-Item -ItemType Junction -Path $appLink -Target $appDirectory | Out-Null
}
$runtimeDirectory = Join-Path $workspace 'runtime'
New-Item -ItemType Directory -Force $runtimeDirectory | Out-Null
$runtimeLink = Join-Path $runtimeDirectory 'texlive'
$resolvedTexRoot = (Resolve-Path $TexRoot).Path
if (Test-Path $runtimeLink) {
    $existing = Get-Item $runtimeLink -Force
    if ($existing.LinkType -ne 'Junction' -or [string]$existing.Target -ine $resolvedTexRoot) {
        throw "The test runtime path already points elsewhere: $runtimeLink. It was left untouched."
    }
} else {
    New-Item -ItemType Junction -Path $runtimeLink -Target $resolvedTexRoot | Out-Null
}
Copy-Item (Join-Path $root 'samples') $appDirectory -Recurse -Force
$launcher = Join-Path $root 'tools/launcher/NoHalluTrans.exe'
$expectedHash = (Get-Content "$launcher.sha256" -Raw).Split(' ')[0].Trim()
if ((Get-FileHash $launcher -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Launcher checksum mismatch.' }
if (Test-Path $entry) {
    if ((Get-FileHash $entry -Algorithm SHA256).Hash -ine $expectedHash) {
        throw "An unrelated executable already exists: $entry. It was left untouched."
    }
} else {
    Copy-Item $launcher $entry
}
Write-Host "Open this anytime: $entry"
Write-Host "TeX runtime: $resolvedTexRoot"
Write-Host "Offline sample: $(Join-Path $appLink 'samples/translation')"
if (!$NoLaunch) {
    Start-Process -FilePath $entry -WorkingDirectory $workspace | Out-Null
}
