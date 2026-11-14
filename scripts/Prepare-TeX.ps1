param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$Repository = 'https://mirrors.tuna.tsinghua.edu.cn/CTAN/systems/texlive/tlnet',
    [string]$InstallerArchive = '',
    [int]$ExpectedYear = 2026
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# Native tools must remain executable even when the parent process has a restricted PATHEXT.
$env:PATHEXT = '.COM;.EXE;.BAT;.CMD'
$Destination = [IO.Path]::GetFullPath($Destination)
if ($Destination -match '[^\x00-\x7f]') { throw 'TeX destination must use an ASCII path.' }
$buildRoot = Join-Path $env:LOCALAPPDATA 'HjfyBuild'
New-Item -ItemType Directory -Force $buildRoot | Out-Null
if (!$InstallerArchive) {
    $InstallerArchive = Join-Path $buildRoot 'install-tl.zip'
    if (!(Test-Path $InstallerArchive)) { Invoke-WebRequest "$Repository/install-tl.zip" -OutFile $InstallerArchive -UseBasicParsing }
}
$installerRoot = Join-Path $buildRoot 'installer'
if (!(Test-Path $installerRoot)) { Expand-Archive $InstallerArchive $installerRoot }
$installer = Get-ChildItem $installerRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
$perl = Join-Path $installer.FullName 'tlpkg/tlperl/bin/perl.exe'
$installScript = Join-Path $installer.FullName 'install-tl'
$normalized = $Destination.Replace('\','/')
$profile = Join-Path $buildRoot 'texlive.profile'
@"
selected_scheme scheme-small
TEXDIR $normalized
TEXMFLOCAL $normalized/texmf-local
TEXMFSYSCONFIG $normalized/texmf-config
TEXMFSYSVAR $normalized/texmf-var
binary_windows 1
instopt_portable 1
instopt_adjustpath 0
tlpdbopt_install_docfiles 0
tlpdbopt_install_srcfiles 0
tlpdbopt_desktop_integration 0
tlpdbopt_file_assocs 0
collection-latexextra 0
collection-langchinese 0
collection-xetex 1
collection-luatex 1
collection-fontsrecommended 1
collection-bibtexextra 0
"@ | Set-Content $profile -Encoding ASCII
Push-Location $buildRoot
try {
    if (!(Test-Path (Join-Path $Destination 'bin/windows/xelatex.exe'))) {
        & $perl $installScript -no-gui -profile $profile -repository $Repository
        if ($LASTEXITCODE -ne 0) { throw 'TeX Live installation failed.' }
    }
    $tlmgr = Join-Path $Destination 'texmf-dist/scripts/texlive/tlmgr.pl'
    $runtimePerl = Join-Path $Destination 'tlpkg/tlperl/bin/perl.exe'
    $env:PATH = (Join-Path $Destination 'bin/windows') + ';' + $env:PATH
    $version = & $runtimePerl $tlmgr --version
    if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch "version $ExpectedYear") { throw 'TeX Live release year mismatch.' }
    & $runtimePerl $tlmgr option repository $Repository
    if ($LASTEXITCODE -ne 0) { throw 'Cannot configure TeX repository.' }
    # Several file names belong to larger packages: subcaption -> caption,
    # tabularx -> tools, balance -> preprint. "arxiv" is not a TeX Live package.
    & $runtimePerl $tlmgr install biber biblatex ctex xecjk fandol natbib booktabs microtype titlesec enumitem subfiles soul ulem cleveref algorithmicx algorithms algorithm2e algpseudocodex multirow adjustbox tcolorbox fontawesome fontawesome5 beamer listings mathtools caption wrapfig xpatch xstring environ trimspaces collectbox footmisc threeparttable makecell tools float abstract preprint etoolbox acmart doclicense ccicons ieeetran siunitx pgfplots thmtools libertine libertinus-otf libertinus-type1 newtx newpx inconsolata xurl orcidlink csquotes tabularray ninecolors arydshln diagbox colortbl placeins pdflscape changepage lineno todonotes pdfpages standalone svg transparent eso-pic overpic forest doublestroke bbm bbm-macros stmaryrd cancel units physics braket sttools cite bookmark tablefootnote koma-script paralist xifthen ifmtarg zref relsize anyfontsize silence mdframed framed fvextra upquote grffile epstopdf totpages ncctools comment draftwatermark hyperxmp cmap mmap refcount setspace
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install required runtime packages.' }
    $packages = & $runtimePerl $tlmgr info --only-installed --data name,localrev
    if ($LASTEXITCODE -ne 0) { throw 'Cannot generate installed package list.' }
    foreach ($tool in @('pdflatex','xelatex','lualatex','bibtex','biber','kpsewhich')) {
        $binary = Join-Path $Destination "bin/windows/$tool.exe"
        if (!(Test-Path $binary)) { throw "Missing runtime tool: $tool" }
        $toolVersion = & $binary --version
        if ($LASTEXITCODE -ne 0 -or !$toolVersion) { throw "Runtime tool does not run: $tool" }
    }
    foreach ($file in @('ctex.sty','xeCJK.sty','fontspec.sty','FandolSong-Regular.otf','plainnat.bst','biblatex.sty')) {
        $found = & (Join-Path $Destination 'bin/windows/kpsewhich.exe') $file
        if ($LASTEXITCODE -ne 0 -or !$found) { throw "Missing runtime component: $file" }
    }
    $packages | Set-Content (Join-Path $Destination 'hjfy-packages.txt') -Encoding UTF8
    $manifest = @{ year=$ExpectedYear; repository=$Repository; created_utc=[DateTime]::UtcNow.ToString('o'); installer_sha256=(Get-FileHash $InstallerArchive -Algorithm SHA256).Hash; packages_sha256=(Get-FileHash (Join-Path $Destination 'hjfy-packages.txt') -Algorithm SHA256).Hash }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $Destination 'hjfy-runtime.json') -Encoding UTF8
} finally { Pop-Location }
