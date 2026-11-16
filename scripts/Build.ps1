param(
    [string]$Dotnet = '',
    [string]$TexRoot = '',
    [string]$OutputDirectory = '',
    [ValidateSet("Both", "Portable", "Full")]
    [string]$Edition = "Both",
    [switch]$IncludeTex,
    [switch]$SkipCoreTests,
    [switch]$SkipTests,
    [string]$PrebuiltApp = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PATHEXT = '.COM;.EXE;.BAT;.CMD'
$root = Split-Path $PSScriptRoot -Parent
$toolsRoot = Join-Path $env:LOCALAPPDATA 'HjfyBuild'
if (!$Dotnet) {
    $localDotnet = Join-Path $toolsRoot 'dotnet/dotnet.exe'
    if (Test-Path $localDotnet) { $Dotnet = $localDotnet } else { $Dotnet = 'dotnet' }
}
if (!$env:NUGET_PACKAGES -and (Test-Path (Join-Path $toolsRoot 'nuget'))) {
    $env:NUGET_PACKAGES = Join-Path $toolsRoot 'nuget'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$rulesA = Join-Path $root 'reference/03-rules/ported-rules.json'
$rulesB = Join-Path $root 'reference/01-implementation/docs/ported-rules.json'
if ((Get-FileHash $rulesA).Hash -ne (Get-FileHash $rulesB).Hash) { throw 'Rule reference copies disagree; reconcile them before publishing.' }
if (!$TexRoot) { $TexRoot = Join-Path $env:LOCALAPPDATA 'HjfyBuild/texlive' }
if ($IncludeTex -and !(Test-Path (Join-Path $TexRoot 'hjfy-runtime.json'))) {
    throw 'TeX runtime is not finalized. Run Prepare-TeX.ps1 first, or omit -IncludeTex for the lightweight portable app.'
}
if ($IncludeTex) { $Edition = 'Full' }
if ($Edition -eq 'Both') {
    if (!(Test-Path (Join-Path $TexRoot 'hjfy-runtime.json'))) { throw 'Both editions require a prepared local TeX runtime. Use -Edition Portable to build only the lightweight edition.' }
    foreach ($variant in @('Portable', 'Full')) {
        $arguments = @{ Dotnet=$Dotnet; TexRoot=$TexRoot; OutputDirectory=$OutputDirectory; Edition=$variant; SkipCoreTests=[bool]$SkipCoreTests; SkipTests=[bool]$SkipTests }
        if ($variant -eq 'Full') {
            $arguments.SkipCoreTests = $true
            $portableApp = Join-Path $OutputDirectory 'NoHalluTrans-Portable/app'
            if (Test-Path $portableApp) { $arguments.PrebuiltApp = $portableApp }
        }
        & $PSCommandPath @arguments
    }
    return
}
$IncludeTex = $Edition -eq 'Full'
if ($IncludeTex -and !(Test-Path (Join-Path $TexRoot 'hjfy-runtime.json'))) { throw 'A prepared TeX runtime is required for the full edition.' }
$editionName = if ($IncludeTex) { 'NoHalluTrans-Full' } else { 'NoHalluTrans-Portable' }
$destination = Join-Path $OutputDirectory $editionName
$verification = Join-Path $OutputDirectory 'verification'
New-Item -ItemType Directory -Force $verification | Out-Null
$release = Join-Path $OutputDirectory ($editionName + '-building-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path $release) { throw "Release directory already exists: $release. Choose a fresh OutputDirectory." }
if (!$SkipTests -and !$SkipCoreTests) {
    $env:HJFY_TEST_REPORT = Join-Path $verification 'core-tests.json'
    & $Dotnet run --project (Join-Path $root 'tests/NoHalluTrans.Tests/NoHalluTrans.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Behavioral tests failed.' }
}
$app = Join-Path $release 'app'
$reusedApp = $PrebuiltApp -and (Test-Path $PrebuiltApp)
if ($reusedApp) {
    & robocopy.exe $PrebuiltApp $app /E /COPY:DAT /DCOPY:DAT /MT:16 /R:1 /W:1 /XJ /XF release-manifest.json /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) { throw 'Copying prebuilt app directory failed.' }
    $global:LASTEXITCODE = 0
} else {
    & $Dotnet publish (Join-Path $root 'src/NoHalluTrans.Desktop/NoHalluTrans.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Platform=x64 -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publishing failed.' }
    # WinUI's native MUI resources are not filtered by SatelliteResourceLanguages.
    Get-ChildItem $app -Directory | Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)+$' -and $_.Name -notin @('en-us','zh-CN','zh-TW') } | Remove-Item -Recurse -Force
    Get-ChildItem $app -File -Filter '*.pdb' | Remove-Item -Force
}
$launcher = Join-Path $root 'tools/launcher/NoHalluTrans.exe'
$expectedLauncherHash = (Get-Content "$launcher.sha256" -Raw).Split(' ')[0].Trim()
if ((Get-FileHash $launcher -Algorithm SHA256).Hash -ine $expectedLauncherHash) { throw 'Launcher checksum mismatch.' }
Copy-Item $launcher (Join-Path $release 'NoHalluTrans.exe')
if (!$reusedApp) {
    New-Item -ItemType Directory -Force (Join-Path $app 'licenses/launcher') | Out-Null
    Copy-Item (Join-Path $root 'tools/launcher/THIRD-PARTY-NOTICES.txt') (Join-Path $app 'licenses/launcher')
}
if ($IncludeTex) {
    $runtimeTarget = Join-Path $release 'runtime/texlive'
    & robocopy.exe $TexRoot $runtimeTarget /E /COPY:DAT /DCOPY:DAT /MT:16 /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) { throw 'Copying the portable TeX runtime failed.' }
    $global:LASTEXITCODE = 0
}
$guide = Get-Content (Join-Path $root 'docs/使用说明.md') -Raw -Encoding UTF8
$guide = $guide -replace '(?m)^\[工程首页\].*\r?\n', ''
$guide = $guide.Replace('(VERIFICATION.md)', '(app/VERIFICATION.md)')
$guide | Set-Content (Join-Path $release '使用说明.md') -Encoding UTF8
if (!$reusedApp) {
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $app
    Copy-Item (Join-Path $root 'docs/VERIFICATION.md') $app
    Copy-Item (Join-Path $root 'samples') $app -Recurse
    Copy-Item (Join-Path $PSScriptRoot 'Verify-Portable.ps1') $app
}
if (!$SkipTests) {
    $desktopReport = Join-Path $verification ('build-' + $Edition.ToLowerInvariant())
    if (Test-Path $desktopReport) { Remove-Item $desktopReport -Recurse -Force }
    $desktopArguments = @((Join-Path $release 'NoHalluTrans.exe'), '--bundled-runtime', (Join-Path $app 'samples/translation'), $desktopReport)
    if (!$IncludeTex) { $desktopArguments += '--core-only' }
    & $Dotnet run --project (Join-Path $root 'tests/NoHalluTrans.Desktop.Tests/NoHalluTrans.Desktop.Tests.csproj') -c Release -- @desktopArguments
    if ($LASTEXITCODE -ne 0) { throw 'WinUI desktop checks failed.' }
    if ($IncludeTex) {
        & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Root $release -WorkDirectory (Join-Path $verification 'full-compilation')
    } else {
        & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Root $release -CoreOnly

    }
}
$packageDirectory = Join-Path $root 'artifacts/obj/NoHalluTrans.Desktop'
$dependencyManifest = Get-ChildItem $packageDirectory -Filter 'project.assets.json' -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$reusedApp -and $dependencyManifest) {
    Copy-Item $dependencyManifest.FullName (Join-Path $app 'dependency-assets.json')
    $assets = Get-Content $dependencyManifest.FullName -Raw | ConvertFrom-Json
    $packageFolders = $assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name }
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        foreach ($packageFolder in $packageFolders) {
            $packagePath = Join-Path $packageFolder $library.Name.ToLowerInvariant()
            if (!(Test-Path $packagePath)) { continue }
            $licenseDirectory = Join-Path $app ('licenses/' + $library.Name.Replace('/','-'))
            New-Item -ItemType Directory -Force $licenseDirectory | Out-Null
            Get-ChildItem $packagePath -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING|THIRD.PARTY)' -or $_.Extension -eq '.nuspec' } | ForEach-Object {
                $relative = $_.FullName.Substring($packagePath.Length).TrimStart('\','/')
                $target = Join-Path $licenseDirectory $relative
                New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
                Copy-Item $_.FullName $target
            }
        }
    }
}
# TeX Live's license files are included in the runtime; .NET's notices are copied by self-contained publish.
$files = Get-ChildItem $release -File -Recurse
$texManifest = if ($IncludeTex) { Get-Content (Join-Path $TexRoot 'hjfy-runtime.json') -Raw | ConvertFrom-Json } else { $null }
$manifest = @{ version='0.3.1'; layout='launcher-app'; includes_tex=[bool]$IncludeTex; tex_preparation=$(if ($IncludeTex) {'ready-to-use'} else {'download-on-demand'}); target='win-x64'; self_contained=$true; python_required=$false; unpacked_bytes=($files | Measure-Object Length -Sum).Sum; file_count=$files.Count; tex_runtime=$texManifest }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $app 'release-manifest.json') -Encoding UTF8
# Keep all portable user data when replacing the application folder.
foreach ($name in @('data', 'work', 'output')) {
    New-Item -ItemType Directory -Force (Join-Path $release $name) | Out-Null
}
# Swap only after the complete folder has passed validation. Preserve the previous result.
if (Test-Path $destination) {
    $active = @(Get-Process HJFY -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) })
    if ($active.Count) { throw "Close the portable app before replacing it. New build retained at $release" }
    foreach ($name in @('data', 'work', 'output')) {
        $existingData = Join-Path $destination $name
        if (Test-Path $existingData) {
            & robocopy.exe $existingData (Join-Path $release $name) /E /COPY:DAT /DCOPY:DAT /MT:16 /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP
            if ($LASTEXITCODE -gt 7) { throw "Preserving portable user data failed: $name" }
            $global:LASTEXITCODE = 0
        }
    }
    $previous = Join-Path $OutputDirectory ($editionName + '-previous-' + [Guid]::NewGuid().ToString('N'))
    Move-Item $destination $previous
    try { Move-Item $release $destination }
    catch { Move-Item $previous $destination; throw }
    Remove-Item $previous -Recurse -Force
} else {
    Move-Item $release $destination
}
Write-Output "Portable directory: $destination"
Write-Output "Unpacked bytes: $($manifest.unpacked_bytes)"
Write-Output 'No archives were created. Run Package.ps1 separately when distribution is needed.'
