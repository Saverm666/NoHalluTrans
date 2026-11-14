param(
    [string]$Source = '',
    [string]$Destination = '',
    [switch]$IncludeTex
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Source) { $Source = Join-Path $root $(if ($IncludeTex) { 'artifacts/NoHalluTrans-Full' } else { 'artifacts/NoHalluTrans-Portable' }) }
if (!$Destination) { $Destination = Join-Path $root 'artifacts/archives/HJFY-win-x64.zip' }
if (!(Test-Path (Join-Path $Source 'app/NoHalluTrans.exe'))) {
    throw 'A portable application directory is required. Run Build.ps1 first.'
}
if ($IncludeTex -and !(Test-Path (Join-Path $Source 'runtime/texlive/bin/windows/xelatex.exe'))) {
    throw 'An installed TeX runtime is required for -IncludeTex.'
}
if (Test-Path $Destination) { throw 'Archive already exists. Choose a new destination.' }
New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($Destination)) -Parent) | Out-Null
$exclude = @('data','work','output')
if (!$IncludeTex) { $exclude += 'runtime' }
& (Join-Path $PSScriptRoot 'New-PortableArchive.ps1') -Source $Source -Destination $Destination -Prefix 'HJFY' -ExcludeDirectory $exclude
"$((Get-FileHash $Destination -Algorithm SHA256).Hash.ToLower())  $([IO.Path]::GetFileName($Destination))" | Set-Content "$Destination.sha256" -Encoding ASCII
Write-Output "Archive: $Destination"
