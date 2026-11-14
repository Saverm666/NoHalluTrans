#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <wchar.h>

/* Keep the public folder clean; the child retains its own WinUI resource root. */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)instance; (void)previous; (void)show;
    wchar_t executable[32768], directory[32768], command[32768];
    DWORD length = GetModuleFileNameW(NULL, directory, 32768);
    if (!length || length >= 32768) return 1;
    wchar_t *separator = wcsrchr(directory, L'\\');
    if (!separator) return 1;
    *separator = L'\0';
    if (swprintf(executable, 32768, L"%ls\\app\\NoHalluTrans.exe", directory) < 0 ||
        swprintf(command, 32768, L"\"%ls\" %ls", executable, arguments) < 0 ||
        wcslen(directory) + 5 >= 32768) return 1;
    wcscat(directory, L"\\app");
    STARTUPINFOW startup = {0}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &process))
    {
        MessageBoxW(NULL, L"无法启动 NoHalluTrans。请完整解压软件，并保留 app 文件夹。", L"NoHalluTrans", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 1; GetExitCodeProcess(process.hProcess, &code); CloseHandle(process.hProcess);
    return (int)code;
}
