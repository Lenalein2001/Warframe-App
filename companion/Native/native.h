#pragma once
#include <cstdint>
#include <cstddef>
#ifdef _WIN32
#define WFC_API extern "C" __declspec(dllexport)
#else
#define WFC_API extern "C" __attribute__((visibility("default")))
#endif
// All buffers are caller-owned. Functions never retain pointers or throw across the ABI.
WFC_API int wfc_abi_version() noexcept;
WFC_API std::int64_t wfc_unlzma(const std::uint8_t* data, std::size_t size, std::uint8_t* output, std::size_t capacity) noexcept;
WFC_API std::int64_t wfc_find(const std::uint8_t* data, std::size_t size, const std::uint8_t* needle, std::size_t count) noexcept;
WFC_API int wfc_prepare_bgra(std::uint8_t* pixels, int width, int height, int stride, int threshold) noexcept;
WFC_API double wfc_similarity(const char* a, std::size_t na, const char* b, std::size_t nb) noexcept;
WFC_API int wfc_reward_layout(const std::uint8_t* pixels, int width, int height, int stride, double configured_scale, double* found_scale) noexcept;
