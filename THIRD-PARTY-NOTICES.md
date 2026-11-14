# Third-party components

This software's release bundles separately licensed third-party components. Their original license and copyright notices remain applicable.

| Component | Purpose | License / official source |
|---|---|---|
| .NET | Self-contained Windows runtime | MIT and bundled third-party notices; https://github.com/dotnet/runtime |
| Windows App SDK / WinUI 3 | Native Windows desktop UI, system backdrop and controls | Microsoft package license and third-party notices; https://github.com/microsoft/WindowsAppSDK and https://github.com/microsoft/microsoft-ui-xaml |
| Windows SDK Build Tools / CsWinRT / WebView2 SDK (transitive) | Build-time tooling and Windows projections | Microsoft package licenses and notices included in licenses/; no WebView2 control is used by the app |
| Microsoft.Data.Sqlite | SQLite cache | MIT; https://github.com/dotnet/efcore |
| Windows SQLite / SQLitePCLRaw | SQLite engine (Windows built-in winsqlite3.dll) and bindings | Windows system component; SQLite public domain; SQLitePCLRaw Apache-2.0; https://sqlite.org/copyright.html and https://github.com/ericsink/SQLitePCL.raw |
| SharpCompress | Archive extraction | MIT; https://github.com/adamhathcock/sharpcompress |
| System.Security.Cryptography.ProtectedData | Windows DPAPI integration | MIT; https://github.com/dotnet/runtime |
| TeX Live | TeX engines, packages, package manager and utilities | Per-component licenses; https://tug.org/texlive/copying.html |
| Fandol fonts | Chinese typesetting | GPL with font embedding exception; https://ctan.org/pkg/fandol |
| Biber / BibTeX | Bibliography processing | Per-component licenses in TeX Live; https://ctan.org/pkg/biber and https://ctan.org/pkg/bibtex |

The release includes the TeX Live runtime's license files and package metadata, and .NET's THIRD-PARTY-NOTICES.TXT and LICENSE.txt where supplied by the runtime distribution. All NuGet dependency versions are recorded in dependency-assets.json. The build also collects NuGet package licenses into licenses/.

The original reverse-engineering archive and reference Python implementation are not included in the desktop release. No ownership or license of the original proprietary application is implied by these notices.

## Portable launcher

The native launcher is compiled with MinGW-w64; its runtime notices are in `app/licenses/launcher/THIRD-PARTY-NOTICES.txt`. Compiler tools are build dependencies and are not included in the portable release.

- PDFsharp 6.2.4：MIT License，用于保留矢量与文字的 PDF 页面合并及并排排版。https://github.com/empira/PDFsharp
