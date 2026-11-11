#include "native.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <vector>
#include <chrono>
#include <cstring>
#include <source_location>
#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif
static void check(bool ok, const std::source_location where = std::source_location::current()) {
    if (!ok) throw std::runtime_error("native regression failed at line " + std::to_string(where.line()));
}
static void memory_test() {
    // Heap allocation exercises real OS reads without inspecting another application's data.
    const char* fixture = "{\"LastInventorySync\":123,\"MiscItems\":[],\"nativeFixture\":\"wfc-regression-7d91\"}";
    std::vector<std::uint8_t> allocation(std::strlen(fixture) + 2, 0);
    std::memcpy(allocation.data() + 1, fixture, std::strlen(fixture));
#ifndef _WIN32
    // The OS reader observes this allocation without a C++ pointer argument. Make its
    // contents escape so optimizing compilers cannot elide or delay the fixture stores.
    asm volatile("" : : "r"(allocation.data()) : "memory");
#endif
    bool found = false;
    const auto candidate = [](const std::uint8_t* bytes, std::size_t size, std::size_t marker, void* context) -> int {
        const char expected[] = "wfc-regression-7d91";
        if (marker < size && wfc_find(bytes, size, reinterpret_cast<const std::uint8_t*>(expected), sizeof(expected) - 1) >= 0) {
            *static_cast<bool*>(context) = true;
            return 0;
        }
        return 1;
    };
    const auto progress = [](std::uint64_t, void*) -> int { return 1; };
#ifdef _WIN32
    const auto pid = static_cast<int>(GetCurrentProcessId());
#else
    const auto pid = static_cast<int>(getpid());
#endif
    const auto result = wfc_scan_memory(pid, candidate, progress, &found);
    std::cout << "Process reader fixture: status " << result << ", found " << found << std::endl;
    check(result == 1 && found);
    const auto cancel = [](std::uint64_t, void*) -> int { return 0; };
    check(wfc_scan_memory(pid, candidate, cancel, &found) == 1);
    check(wfc_scan_memory(-1, candidate, progress, &found) == -2);
}
int main() {
    const std::uint8_t color_rows[] = {1,2,3,255,4,5,6,255,99,99,99,99,7,8,9,255,10,11,12,255,99,99,99,99};
    std::uint8_t rgb[12]{};
    check(wfc_bgra_to_rgb(color_rows, 2, 2, 12, rgb, sizeof(rgb)) == 1);
    const std::uint8_t expected_rgb[] = {3,2,1,6,5,4,9,8,7,12,11,10};
    check(std::memcmp(rgb, expected_rgb, sizeof(rgb)) == 0);
    check(wfc_bgra_to_rgb(color_rows, 2, 2, 12, rgb, 11) == 0);
    check(wfc_bgra_to_rgb(color_rows, 2, 2, 7, rgb, sizeof(rgb)) == 0);
    check(wfc_bgra_to_rgb(nullptr, 2, 2, 12, rgb, sizeof(rgb)) == 0);
    memory_test();
    check(wfc_abi_version() == 1);
    const std::uint8_t data[]{1,2,1,2,3}; const std::uint8_t needle[]{1,2,3};
    check(wfc_find(data, 5, needle, 3) == 2); check(wfc_find(data, 2, needle, 3) == -1); check(wfc_find(nullptr, 0, needle, 3) == -1);
    check(std::abs(wfc_similarity("abc", 3, "axc", 3) - 2.0/3) < 1e-9); check(wfc_similarity("", 0, "", 0) == 1);
    std::vector<std::uint8_t> pixels(1920*1080*4, 0); double scale = 0;
    check(wfc_reward_layout(pixels.data(), 1920, 1080, 7680, 1, &scale) == 0);
    for (int cards=1; cards<=4; ++cards) {
        std::fill(pixels.begin(), pixels.end(), 0);
        int left = 960 - cards * 242 / 2;
        for (int k=0; k<cards; ++k) for (int y=476; y<=479; ++y) for (int x=left+k*242+5; x<left+(k+1)*242-5; ++x) {
            auto p = pixels.data() + (y*1920+x)*4; p[0]=p[1]=p[2]=110; p[3]=255;
        }
        check(wfc_reward_layout(pixels.data(), 1920, 1080, 7680, 1, &scale) == cards); check(scale == 1);
    }
    std::uint8_t gray[]{40,100,200,0, 20,30,40,0};
    check(wfc_prepare_bgra(gray, 2, 1, 8, 1)==1 && gray[0]==0 && gray[4]==255 && gray[3]==255);
    check(wfc_prepare_bgra(gray, 2, 1, 7, 0)==0);
    auto start=std::chrono::steady_clock::now();
    for (int i=0;i<100;++i) check(wfc_reward_layout(pixels.data(),1920,1080,7680,1,&scale)==4);
    std::cout << "Native regressions passed; 100 1080p layout reads: " << std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count() << " ms\n";
}
