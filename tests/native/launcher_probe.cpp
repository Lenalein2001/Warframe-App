#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <fstream>
#include <string>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    std::wstring output = GetConsoleWindow() ? L"console\n" : L"no-console\n";
    for (int i = 1; i < count; ++i) output += std::wstring(args[i]) + L"\n";
    LocalFree(args);
    std::ofstream file("probe.txt", std::ios::binary);
    file.write(reinterpret_cast<const char*>(output.data()), static_cast<std::streamsize>(output.size() * sizeof(wchar_t)));
    return 23;
}
