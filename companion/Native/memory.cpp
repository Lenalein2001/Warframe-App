#include "native.h"
#include <algorithm>
#include <chrono>
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
#endif

namespace {
constexpr std::size_t mib = 1024 * 1024;
constexpr char marker[] = "\"LastInventorySync\"";
constexpr std::size_t marker_size = sizeof(marker) - 1;
struct Region { std::uint64_t start, size; };

// Owns only a read-only process handle; never changes process protections or data.
class ProcessReader {
#ifdef _WIN32
    HANDLE handle;
#else
    int handle;
#endif
    int pid;
public:
    explicit ProcessReader(int id) : pid(id) {
#ifdef _WIN32
        handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, static_cast<DWORD>(id));
#else
        handle = open(("/proc/" + std::to_string(id) + "/mem").c_str(), O_RDONLY | O_CLOEXEC);
#endif
    }
    ~ProcessReader() {
#ifdef _WIN32
        if (handle) CloseHandle(handle);
#else
        if (handle >= 0) close(handle);
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
    std::vector<Region> regions() const {
        std::vector<Region> result;
        auto append = [&result](std::uint64_t start, std::uint64_t size) {
            if (!size || start + size < start) return;
            if (!result.empty() && result.back().start + result.back().size == start) result.back().size += size;
            else result.push_back({start, size});
        };
#ifdef _WIN32
        MEMORY_BASIC_INFORMATION info{};
        std::uint64_t address = 0;
        while (VirtualQueryEx(handle, reinterpret_cast<const void*>(address), &info, sizeof(info))) {
            const auto start = reinterpret_cast<std::uint64_t>(info.BaseAddress);
            const auto size = static_cast<std::uint64_t>(info.RegionSize);
            if (!size || start + size <= address) break;
            if (info.State == MEM_COMMIT && info.Type == MEM_PRIVATE && (info.Protect & (PAGE_GUARD | PAGE_READWRITE)) == PAGE_READWRITE) append(start, size);
            address = start + size;
        }
#else
        std::ifstream maps("/proc/" + std::to_string(pid) + "/maps");
        std::string line;
        while (std::getline(maps, line)) {
            std::istringstream fields(line);
            std::string range, permissions;
            if (!(fields >> range >> permissions) || permissions.substr(0, 2) != "rw") continue;
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

int wfc_scan_memory(int pid, wfc_memory_candidate candidate, wfc_memory_progress progress, void* context) noexcept {
    if (pid <= 0 || !candidate || !progress) return -2;
    try {
        ProcessReader process(pid);
        if (!process.valid()) return -1;
        const auto started = std::chrono::steady_clock::now();
        std::uint64_t scanned = 0;
        auto status = [&]() {
            if (!progress(scanned, context)) return 1;
            return scanned >= 32ULL * 1024 * mib || std::chrono::steady_clock::now() - started >= std::chrono::seconds(45) ? 2 : 0;
        };
        if (const int state = status()) return state;
        std::vector<std::uint8_t> buffer(4 * mib + marker_size), blob(32 * mib);
        for (const auto& region : process.regions()) {
            const auto end = region.start + region.size;
            for (auto cursor = region.start; cursor < end;) {
                if (const int state = status()) return state;
                const auto size = process.read(cursor, buffer.data(), static_cast<std::size_t>(std::min<std::uint64_t>(buffer.size(), end - cursor)));
                scanned += size;
                if (!size) { cursor += std::min<std::uint64_t>(4096, end - cursor); continue; }
                std::size_t offset = 0;
                while (offset < size) {
                    const auto hit = wfc_find(buffer.data() + offset, size - offset, reinterpret_cast<const std::uint8_t*>(marker), marker_size);
                    if (hit < 0) break;
                    if (const int state = status()) return state;
                    const auto address = cursor + offset + static_cast<std::size_t>(hit);
                    const auto begin = address - std::min<std::uint64_t>(address - region.start, 8 * mib);
                    const auto got = process.read(begin, blob.data(), static_cast<std::size_t>(std::min<std::uint64_t>(blob.size(), end - begin)));
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
