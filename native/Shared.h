#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <string>

namespace sf {
constexpr LONG Magic = 0x53465032;
constexpr LONG Version = 2;
constexpr int Capacity = 512;
// Fixed-width, naturally aligned IPC; mirrored by NativeSession.cs (both bitnesses).
struct Shared {
    LONG magic;
    LONG version;
    volatile LONG targetMilliFps;
    volatile LONG heartbeat;
    volatile LONG status; // 0 loading, 1 hooks ready, -1 initialization failed
    volatile LONG api;    // 1 DXGI, 2 D3D9, 3 OpenGL
    volatile LONG sequence;
    LONG reserved;
    volatile LONG micros[Capacity];
};
static_assert(offsetof(Shared, micros) == 32 && sizeof(Shared) == 2080);
inline std::wstring MappingName(DWORD pid) {
    return L"Local\\SmoothFrames.v2." + std::to_wstring(pid);
}
inline LONG Read(volatile LONG* value) { return InterlockedCompareExchange(value, 0, 0); }
inline bool Alive(Shared* shared) {
    return GetTickCount() - static_cast<DWORD>(Read(&shared->heartbeat)) < 2500;
}
inline int64_t Now() { LARGE_INTEGER value; QueryPerformanceCounter(&value); return value.QuadPart; }
inline int64_t Frequency() { LARGE_INTEGER value; QueryPerformanceFrequency(&value); return value.QuadPart; }
}
