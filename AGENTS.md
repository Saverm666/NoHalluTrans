# Local development workflow

- Use `D:\HJFY` as the source tree. `NoHalluTrans.slnx`, `src`, `tests`, `scripts`, `tools`, and `samples` are at the root. The reference archive is `reference` (formerly `tex-pipeline`).
- Keep only the two latest edition builds in `artifacts`: `artifacts/NoHalluTrans-Portable` and `artifacts/NoHalluTrans-Full`. During builds, MSBuild/dev intermediates may temporarily use `artifacts/bin/<project>`, `artifacts/obj/<project>`, and `artifacts/dev`, and must be cleaned up afterward.
- Store external build tools and verification artifacts outside `artifacts`: keep local .NET SDK/installers in `%LOCALAPPDATA%\HjfyBuild` (e.g., `%LOCALAPPDATA%\HjfyBuild\dotnet`) and verification evidence/reports/helper scripts in `%LOCALAPPDATA%\HjfyBuild\verification`.
- After code changes run `scripts/Start-Dev.ps1 -NoLaunch`. For an existing development build use `-SkipBuild`. All generated development launch files belong in `artifacts/dev`.
- Launch the retained portable app from `D:\HJFY\artifacts\NoHalluTrans-Portable\NoHalluTrans.exe` or `D:\HJFY\artifacts\NoHalluTrans-Full\NoHalluTrans.exe`. Do not recreate root `NoHalluTrans.exe`, `app`, `runtime`, or CMD shortcuts. Development links in `artifacts/dev` reuse development output and local TeX.
- `scripts/Build.ps1` builds and validates both latest editions by default without ZIP files. `-Edition Portable` builds without TeX; `-Edition Full` (or `-IncludeTex`) copies an existing TeX runtime. Successful replacement removes the previous application build while preserving data/work/output. The application downloads/repairs its portable TeX on user request. It is separate from normal local compilation.
- Only run `scripts/Package.ps1` to compress an existing portable folder when the user explicitly requests packaging. Do not compress the TeX runtime during folder builds.

- Cleanup preference: remove obsolete archives, previous builds, stale verification work directories, and `bin`/`obj`/`dev` intermediates after validation so `artifacts` contains only `NoHalluTrans-Portable` and `NoHalluTrans-Full`. Keep concise current verification evidence in `%LOCALAPPDATA%\HjfyBuild\verification`.

- Portable user data belongs beside the portable launcher: data/ for settings/history/cache/logs, work/ for task files, output/ for default PDFs. Never default to AppData or system temp for application-owned data. Preserve these folders on portable rebuilds.
