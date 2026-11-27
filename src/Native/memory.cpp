#include "native.h"
#include <algorithm>
#include <chrono>
#include <cstring>
#include <cwchar>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#else
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#endif

namespace {
constexpr std::size_t mib = 1024 * 1024;
constexpr char marker[] = "\"LastInventorySync\"";
constexpr std::size_t marker_size = sizeof(marker) - 1;
// Volatile stores prevent the compiler from eliminating cleanup of raw game bytes.
struct SensitiveBuffer : std::vector<std::uint8_t> {
    explicit SensitiveBuffer(std::size_t size) : std::vector<std::uint8_t>(size) {}
    ~SensitiveBuffer() { volatile std::uint8_t* p = data(); for (std::size_t i = 0; i < size(); ++i) p[i] = 0; }
};
struct Region { std::uint64_t start, size; };

// Owns only a read-only process handle; never changes process protections or data.
class ProcessReader {
#ifdef _WIN32
    HANDLE handle;
#else
    int handle;
    int directory;
#endif
#ifdef _WIN32
    static bool isGame(HANDLE process) {
        wchar_t image[32768]{};
        DWORD length = static_cast<DWORD>(std::size(image));
        if (!QueryFullProcessImageNameW(process, 0, image, &length)) return false;
        const auto separator = std::wcsrchr(image, L'\\');
        return _wcsicmp(separator ? separator + 1 : image, L"Warframe.x64.exe") == 0;
    }
#endif
public:
    explicit ProcessReader(int id) {
#ifdef _WIN32
        const auto pid = static_cast<DWORD>(id);
        handle = nullptr;
        // Check the image before requesting memory access. Verify the second
        // handle too, in case the target disappeared between the two opens.
        if (pid != GetCurrentProcessId()) {
            const auto query = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (!query) return;
            const auto expected = isGame(query);
            CloseHandle(query);
            if (!expected) return;
        }
        handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
        if (handle && pid != GetCurrentProcessId() && !isGame(handle)) {
            CloseHandle(handle);
            handle = nullptr;
        }
#else
        directory = open(("/proc/" + std::to_string(id)).c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        handle = -1;
        if (directory < 0) return;
        if (id != getpid()) {
            const auto comm = openat(directory, "comm", O_RDONLY | O_CLOEXEC);
            char buffer[64]{};
            const auto count = comm >= 0 ? ::read(comm, buffer, sizeof(buffer) - 1) : -1;
            if (comm >= 0) close(comm);
            if (count > 0 && buffer[count - 1] == '\n') buffer[count - 1] = 0;
            if (std::strcmp(buffer, "Warframe.x64") != 0 && std::strcmp(buffer, "Warframe.x64.exe") != 0 && std::strcmp(buffer, "Warframe.x64.ex") != 0) return;
        }
        handle = openat(directory, "mem", O_RDONLY | O_CLOEXEC);
#endif
    }
    ~ProcessReader() {
#ifdef _WIN32
        if (handle) CloseHandle(handle);
#else
        if (handle >= 0) close(handle);
        if (directory >= 0) close(directory);
#endif
    }
    ProcessReader(const ProcessReader&) = delete;
    ProcessReader& operator=(const ProcessReader&) = delete;
    bool valid() const {
#ifdef _WIN32
        return handle != nullptr;
#else
        return handle >= 0;
#endif
    }
    std::size_t read(std::uint64_t address, std::uint8_t* buffer, std::size_t size) const {
#ifdef _WIN32
        SIZE_T actual = 0;
        ReadProcessMemory(handle, reinterpret_cast<const void*>(address), buffer, size, &actual);
        return actual;
#else
        const auto actual = pread(handle, buffer, size, static_cast<off_t>(address));
        return actual > 0 ? static_cast<std::size_t>(actual) : 0;
#endif
    }
    template<typename Continue> std::vector<Region> regions(Continue keepGoing) const {
        std::vector<Region> result;
        auto append = [&result](std::uint64_t start, std::uint64_t size) {
            if (!size || start + size < start) return;
            if (!result.empty() && result.back().start + result.back().size == start) result.back().size += size;
            else result.push_back({start, size});
        };
#ifdef _WIN32
        MEMORY_BASIC_INFORMATION info{};
        std::uint64_t address = 0;
        while (keepGoing() && VirtualQueryEx(handle, reinterpret_cast<const void*>(address), &info, sizeof(info))) {
            const auto start = reinterpret_cast<std::uint64_t>(info.BaseAddress);
            const auto size = static_cast<std::uint64_t>(info.RegionSize);
            if (!size || start + size <= address) break;
            if (info.State == MEM_COMMIT && info.Type == MEM_PRIVATE && info.Protect == PAGE_READWRITE) append(start, size);
            address = start + size;
        }
#else
        std::ifstream maps("/proc/self/fd/" + std::to_string(directory) + "/maps");
        std::string line;
        while (keepGoing() && std::getline(maps, line)) {
            std::istringstream fields(line);
            std::string range, permissions;
            if (!(fields >> range >> permissions) || permissions != "rw-p") continue;
            if (line.find("/dev/") != std::string::npos || line.find(".dll") != std::string::npos || line.find(".so") != std::string::npos || line.find(".exe") != std::string::npos) continue;
            const auto dash = range.find('-');
            if (dash == std::string::npos) continue;
            const auto start = std::stoull(range.substr(0, dash), nullptr, 16);
            const auto end = std::stoull(range.substr(dash + 1), nullptr, 16);
            if (end > start) append(start, end - start);
        }
#endif
        return result;
    }
};
}

int wfc_open_file_identity(std::intptr_t handle, std::uint64_t* device, std::uint64_t* file) noexcept {
    if (!device || !file) return 0;
#ifdef _WIN32
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(reinterpret_cast<HANDLE>(handle), &info)) return 0;
    *device = info.dwVolumeSerialNumber;
    *file = (static_cast<std::uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
#else
    struct stat info{};
    if (fstat(static_cast<int>(handle), &info) != 0) return 0;
    *device = static_cast<std::uint64_t>(info.st_dev);
    *file = static_cast<std::uint64_t>(info.st_ino);
#endif
    return 1;
}

int wfc_file_identity(const char* path, std::uint64_t* device, std::uint64_t* file) noexcept {
    if (!path || !device || !file) return 0;
    try {
#ifdef _WIN32
        const auto size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, nullptr, 0);
        if (!size) return 0;
        std::vector<wchar_t> wide(static_cast<std::size_t>(size));
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide.data(), size)) return 0;
        const auto handle = CreateFileW(wide.data(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle == INVALID_HANDLE_VALUE) return 0;
        const auto ok = wfc_open_file_identity(reinterpret_cast<std::intptr_t>(handle), device, file);
        CloseHandle(handle);
        return ok;
#else
        struct stat info{};
        if (stat(path, &info) != 0) return 0;
        *device = static_cast<std::uint64_t>(info.st_dev);
        *file = static_cast<std::uint64_t>(info.st_ino);
        return 1;
#endif
    } catch (...) { return 0; }
}

int wfc_scan_memory(int pid, wfc_memory_candidate candidate, wfc_memory_progress progress, void* context) noexcept {
    if (pid <= 0 || !candidate || !progress) return -2;
    try {
        if (!progress(0, context)) return 1;
        ProcessReader process(pid);
        if (!process.valid()) return -1;
        const auto started = std::chrono::steady_clock::now();
        constexpr std::uint64_t readBudget = 32ULL * 1024 * mib;
        std::uint64_t scanned = 0, requested = 0;
        auto status = [&]() {
            if (!progress(scanned, context)) return 1;
            return requested >= readBudget || std::chrono::steady_clock::now() - started >= std::chrono::seconds(45) ? 2 : 0;
        };
        auto read = [&](std::uint64_t address, std::uint8_t* into, std::uint64_t size) {
            const auto attempt = static_cast<std::size_t>(std::min(size, readBudget - requested));
            requested += attempt;
            const auto actual = process.read(address, into, attempt);
            scanned += actual;
            return actual;
        };
        if (const int state = status()) return state;
        SensitiveBuffer buffer(4 * mib + marker_size), blob(32 * mib);
        for (const auto& region : process.regions([&]() { return status() == 0; })) {
            const auto end = region.start + region.size;
            for (auto cursor = region.start; cursor < end;) {
                if (const int state = status()) return state;
                const auto size = read(cursor, buffer.data(), std::min<std::uint64_t>(buffer.size(), end - cursor));
                if (!size) { cursor += std::min<std::uint64_t>(4096, end - cursor); continue; }
                std::size_t offset = 0;
                while (offset < size) {
                    const auto hit = wfc_find(buffer.data() + offset, size - offset, reinterpret_cast<const std::uint8_t*>(marker), marker_size);
                    if (hit < 0) break;
                    if (const int state = status()) return state;
                    const auto address = cursor + offset + static_cast<std::size_t>(hit);
                    const auto begin = address - std::min<std::uint64_t>(address - region.start, 8 * mib);
                    const auto got = read(begin, blob.data(), std::min<std::uint64_t>(blob.size(), end - begin));
                    if (const int state = status()) return state;
                    const auto at = static_cast<std::size_t>(address - begin);
                    if (got >= at + marker_size && std::equal(marker, marker + marker_size, blob.data() + at)) {
                        auto first = at;
                        while (first > 0 && blob[first - 1] != 0) --first;
                        auto last = at;
                        while (last < got && blob[last] != 0) ++last;
                        if (!candidate(blob.data() + first, last - first, at - first, context)) return 1;
                    }
                    offset += static_cast<std::size_t>(hit) + marker_size;
                }
                const auto step = size > marker_size ? size - marker_size : size;
                cursor += std::min<std::uint64_t>(step, end - cursor);
            }
        }
        return 0;
    } catch (...) { return -2; }
}
