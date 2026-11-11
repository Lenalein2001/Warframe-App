#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <string>

// Quote each argument using the Windows command-line escaping rules, including
// trailing backslashes in paths and literal quotes inside arguments.
static std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (const wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(slashes * (c == L'"' ? 2 : 1), L'\\');
        slashes = 0;
        if (c == L'"') result += L'\\';
        result += c;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return ERROR_BAD_PATHNAME;
    path.resize(length);
    const auto root = std::filesystem::path(path).parent_path();
    const auto executable = root / L"app" / L"WarframeCompanion.Desktop.exe";
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args) return static_cast<int>(GetLastError());
    bool explicitData = false;
    std::wstring command = quote(executable.wstring());
    for (int i = 1; i < count; ++i) {
        const std::wstring arg(args[i]);
        if (arg == L"--portable") {
            explicitData = true;
            command += L" --data-dir " + quote((root / L"data").wstring());
        } else {
            if (arg == L"--data-dir") explicitData = true;
            command += L" " + quote(arg);
        }
    }
    LocalFree(args);
    // Never merge or silently overwrite profiles. Existing portable profiles
    // keep their location; a fresh extracted copy shares the installed profile.
    if (!explicitData && GetFileAttributesW((root / L"data" / L"state.sqlite").c_str()) != INVALID_FILE_ATTRIBUTES)
        command += L" --data-dir " + quote((root / L"data").wstring());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                        root.c_str(), &startup, &process)) return static_cast<int>(GetLastError());
    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hProcess);
    return static_cast<int>(code);
}
