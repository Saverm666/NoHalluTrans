# Portable launcher

The small native Windows x64 launcher starts `app/NoHalluTrans.exe`, forwards the original Unicode arguments, waits for it and returns its exit code. It uses Windows system DLLs and needs no .NET runtime of its own. The WinUI application remains self-contained in `app`.

The checked binary is built with MinGW-w64 GCC (`tools/w64devkit` or Linux `x86_64-w64-mingw32-gcc`):

```bash
gcc -municode -mwindows -Os -s -static -Wall -Wextra -Wl,--no-insert-timestamp launcher.c -o NoHalluTrans.exe -luser32
sha256sum NoHalluTrans.exe > NoHalluTrans.exe.sha256
```

`Build.ps1` verifies its checksum before copying it. MinGW-w64 runtime notices are included in `THIRD-PARTY-NOTICES.txt` and copied into the release. Compiler tools are not shipped to users.
