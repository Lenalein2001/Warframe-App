#include "native.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <vector>
#include <unordered_map>
#include <cstdlib>
#include "vendor/lzma/LzmaDec.h"

int wfc_abi_version() noexcept { return 1; }
std::int64_t wfc_unlzma(const std::uint8_t* data, std::size_t size, std::uint8_t* output, std::size_t capacity) noexcept {
    if (!data || !output || size < 13 || capacity == 0 || capacity > 32 * 1024 * 1024 || size > 32 * 1024 * 1024) return -1;
    std::uint64_t declared = 0; for (int i=0; i<8; ++i) declared |= std::uint64_t(data[5+i]) << (i*8);
    const bool unknown = declared == std::numeric_limits<std::uint64_t>::max();
    if (!unknown && declared > capacity) return -1;
    SizeT dest = unknown ? capacity : static_cast<SizeT>(declared); SizeT source = size - 13; ELzmaStatus status;
    ISzAlloc allocator{ [](ISzAllocPtr, size_t n) -> void* { return n <= 32 * 1024 * 1024 ? std::malloc(n) : nullptr; }, [](ISzAllocPtr, void* p) { std::free(p); } };
    auto result = LzmaDecode(output, &dest, data+13, &source, data, 5, LZMA_FINISH_END, &status, &allocator);
    if (result != SZ_OK || (unknown ? status != LZMA_STATUS_FINISHED_WITH_MARK : dest != declared)) return -1;
    return static_cast<std::int64_t>(dest);
}
std::int64_t wfc_find(const std::uint8_t* data, std::size_t size, const std::uint8_t* needle, std::size_t count) noexcept {
    if (!data || !needle || count == 0 || count > size) return -1;
    const auto end = data + size - count + 1;
    for (auto p = data; p < end;) {
        p = static_cast<const std::uint8_t*>(std::memchr(p, needle[0], static_cast<std::size_t>(end - p)));
        if (!p) return -1;
        if (std::memcmp(p, needle, count) == 0) return p - data;
        ++p;
    }
    return -1;
}
static bool valid(const void* p, int w, int h, int stride) noexcept {
    return p && w > 0 && h > 0 && w <= 16384 && h <= 16384 && stride >= w * 4 && stride <= 262144 && std::int64_t(w) * h <= 40000000;
}
int wfc_prepare_bgra(std::uint8_t* pixels, int w, int h, int stride, int threshold) noexcept {
    if (!valid(pixels, w, h, stride) || threshold<0 || threshold>4) return 0;
    int low=255,high=0;
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        auto p = pixels + std::size_t(y) * stride + x * 4;
        int v = threshold==2 ? std::max(0,int(p[0])-p[2]) : threshold==3 ? std::max(0,int(p[2])-std::max(p[1],p[0])) : threshold==4 ? std::max(0,int(p[2])-p[0]) : (p[2]*19595+p[1]*38470+p[0]*7471+0x8000)>>16;
        low=std::min(low,v); high=std::max(high,v); p[0]=p[1]=p[2]=static_cast<std::uint8_t>(v); p[3]=255;
    }
    std::array<std::uint8_t,256> lut{}; const auto scale=high>low ? 255.0/(high-low) : 1.0;
    for (int i=0;i<256;++i) { auto v=high>low ? std::clamp(int(i*scale-low*scale),0,255) : i; if (threshold==1) v=v>110 ? 255 : 0; lut[std::size_t(i)]=static_cast<std::uint8_t>(255-v); }
    for (int y=0;y<h;++y) for (int x=0;x<w;++x) {
        auto p=pixels+std::size_t(y)*stride+x*4; p[0]=p[1]=p[2]=lut[p[0]];
    }
    return 1;
}
double wfc_similarity(const char* a, std::size_t na, const char* b, std::size_t nb) noexcept {
    if ((!a && na) || (!b && nb) || na > 4096 || nb > 4096) return -1;
    if (na + nb == 0) return 1;
    try {
        auto unicode = [](const char* text, std::size_t size) {
            std::vector<std::uint32_t> out;
            for (std::size_t p=0; p<size;) {
                auto first = static_cast<unsigned char>(text[p++]); std::uint32_t cp=first; int extra=0;
                if (first >= 0xf0) { cp=first&7; extra=3; } else if (first >= 0xe0) { cp=first&15; extra=2; } else if (first >= 0xc0) { cp=first&31; extra=1; }
                for (int j=0;j<extra;++j) { if (p>=size || (static_cast<unsigned char>(text[p])&0xc0)!=0x80) { cp=0xfffd; break; } cp=(cp<<6)|(static_cast<unsigned char>(text[p++])&63); }
                out.push_back(cp);
            }
            return out;
        };
        auto aa=unicode(a,na), bb=unicode(b,nb);
        std::unordered_map<std::uint32_t,std::vector<std::size_t>> index;
        for (std::size_t j=0;j<bb.size();++j) index[bb[j]].push_back(j);
        if (bb.size()>=200) for (auto it=index.begin();it!=index.end();) { if (it->second.size()>bb.size()/100+1) it=index.erase(it); else ++it; }
        std::vector<std::array<std::size_t,4>> queue{{0,aa.size(),0,bb.size()}}; std::size_t total=0;
        while (!queue.empty()) {
            auto range=queue.back(); queue.pop_back(); auto [alo,ahi,blo,bhi]=range;
            std::size_t best_i=alo,best_j=blo,best_size=0; std::vector<std::size_t> row(bb.size()+1),next(bb.size()+1);
            for (auto i=alo;i<ahi;++i) {
                std::fill(next.begin(),next.end(),0);
                auto found=index.find(aa[i]); if (found!=index.end()) for (auto j:found->second) {
                    if (j<blo) continue;
                    if (j>=bhi) break;
                    auto k=row[j]+1; next[j+1]=k; if (k>best_size) { best_i=i+1-k; best_j=j+1-k; best_size=k; }
                }
                row.swap(next);
            }
            while (best_i>alo && best_j>blo && aa[best_i-1]==bb[best_j-1]) { --best_i; --best_j; ++best_size; }
            while (best_i+best_size<ahi && best_j+best_size<bhi && aa[best_i+best_size]==bb[best_j+best_size]) ++best_size;
            if (best_size) { total+=best_size; if (alo<best_i && blo<best_j) queue.push_back({alo,best_i,blo,best_j}); if (best_i+best_size<ahi && best_j+best_size<bhi) queue.push_back({best_i+best_size,ahi,best_j+best_size,bhi}); }
        }
        return 2.0 * static_cast<double>(total) / static_cast<double>(aa.size()+bb.size());
    } catch (...) { return -1; }
}
namespace {
struct Frame {
    const std::uint8_t* data; int w, h, stride;
    int luma(int x, int y) const { auto p = data + std::size_t(y) * stride + x * 4; return (p[2] * 299 + p[1] * 587 + p[0] * 114) / 1000; }
    std::pair<double, int> bar(double u, double x0, double x1) const {
        auto inset = (x1 - x0) * .1; auto a = std::max(0, int(x0 + inset)); auto b = std::min(w, int(x1 - inset));
        if (b <= a) return {0, -1};
        auto edge = std::max(2, int(std::round(3.2 * u))); auto centre = h / 2.0 - 62.5 * u;
        std::pair<double, int> best{0, -1}; int previous = -1;
        for (int s = -8; s <= 8; ++s) {
            auto y = int(std::round(centre + s * .5 * u)); if (y == previous || y < edge || y + edge >= h) continue; previous = y;
            int lit = 0; for (int x = a; x < b; ++x) if (luma(x, y) - std::max(luma(x, y - edge), luma(x, y + edge)) >= 25) ++lit;
            auto coverage = double(lit) / (b - a); if (coverage > best.first) best = {coverage, y};
        }
        return best;
    }
    bool edges(int y, double left, double c, int cards) const {
        auto reach = std::max(4.0, c * .05); int judged = 0, breaks = 0, inner_judged = 0, inner_breaks = 0;
        for (int j = 0; j <= cards; ++j) {
            auto edge = left + j * c; std::vector<int> beside;
            auto append = [&](double a, double b) { for (int x = std::max(0, int(a)); x < std::min(w, int(b)); ++x) beside.push_back(luma(x, y)); };
            if (j > 0) append(edge - 3 * reach, edge - reach);
            if (j < cards) append(edge + reach, edge + 3 * reach);
            if (beside.empty()) continue;
            std::sort(beside.begin(), beside.end()); auto level = beside[beside.size() / 2];
            auto even = std::count_if(beside.begin(), beside.end(), [level](int v) { return v * 4 >= level * 3; });
            if (level < 50 || std::size_t(even) * 5 < beside.size() * 4) continue;
            int dark = 0; for (int x = std::max(0, int(edge - reach)); x < std::min(w, int(edge + reach)); ++x) if (luma(x, y) * 20 < level * 13) ++dark;
            ++judged; if (dark >= 2) ++breaks;
            if (j > 0 && j < cards) { ++inner_judged; if (dark >= 2) ++inner_breaks; }
        }
        return judged >= 2 && breaks * 5 >= judged * 3 && (inner_judged == 0 || inner_breaks > 0);
    }
};
}
int wfc_reward_layout(const std::uint8_t* pixels, int w, int h, int stride, double configured, double* scale) noexcept {
    if (!valid(pixels, w, h, stride) || !scale || !std::isfinite(configured)) return -1;
    try {
        Frame frame{pixels, w, h, stride}; std::vector<double> scales{1, .95, 1.05, .9, 1.1, .85, 1.15, .8, 1.2, .75, 1.25, .7, .65, .6, .55, .5};
        if (configured > 0) { if (configured < .3 || configured > 2) return -1; std::erase(scales, configured); scales.insert(scales.begin(), configured); }
        for (auto s : scales) {
            auto u = std::min(w / 1920.0, h / 1080.0) * s; auto c = 242 * u; auto mid = w / 2.0;
            auto seen = [&](double from) { return frame.bar(u, mid + from * c, mid + (from + 1) * c).first >= .6; };
            int cards = seen(-2) || seen(1) ? 4 : seen(-1.5) || seen(.5) ? 3 : seen(-1) || seen(0) ? 2 : seen(-.5) ? 1 : 0;
            if (!cards) continue;
            auto left = mid - cards * c / 2; auto row = frame.bar(u, left, left + cards * c).second;
            if (row >= 0 && frame.edges(row, left, c, cards)) { *scale = s; return cards; }
        }
        return 0;
    } catch (...) { return -1; }
}
