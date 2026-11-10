#include "native.h"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <vector>
#include <chrono>
static void check(bool ok) { if (!ok) throw std::runtime_error("native regression failed"); }
int main() {
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
