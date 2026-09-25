#include "Shared.h"
#include <d3d11.h>
#include <d3d9.h>
#include <dxgi1_2.h>
#include <MinHook.h>
#include <algorithm>

static sf::Shared* shared{};
static SRWLOCK pacingLock = SRWLOCK_INIT;
static HANDLE timer{};
static const int64_t frequency = sf::Frequency();
static void* activeStream{};
static int64_t lastSeen{}, lastFrame{}, deadline{};
static LONG previousCap{};
static thread_local int nesting{};

static void Pace(void* stream, LONG api) {
    if (!shared || !sf::Alive(shared) || !TryAcquireSRWLockExclusive(&pacingLock)) return;
    const auto before = sf::Now();
    // Ignore secondary swapchains instead of making their render threads wait for each other.
    if (activeStream != stream) {
        if (lastSeen && before - lastSeen < frequency) {
            ReleaseSRWLockExclusive(&pacingLock);
            return;
        }
        activeStream = stream;
        deadline = lastFrame = 0;
    }
    lastSeen = before;
    const LONG cap = sf::Read(&shared->targetMilliFps);
    if (cap != previousCap) { deadline = lastFrame = 0; previousCap = cap; }
    if (cap >= 15000 && cap <= 1000000) {
        const int64_t period = frequency * 1000 / cap;
        if (!deadline || before > deadline + period) deadline = before;
        int64_t remaining = deadline - before;
        // Sleep most of the interval; spin only the final 0.2 ms. No system-wide timer changes.
        if (remaining > frequency / 5000 && timer) {
            LARGE_INTEGER due{};
            due.QuadPart = -((remaining - frequency / 5000) * 10000000 / frequency);
            if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 100);
        } else if (remaining > frequency / 1000) {
            Sleep(static_cast<DWORD>(remaining * 1000 / frequency - 1));
        }
        while (sf::Now() < deadline) YieldProcessor();
        deadline += period;
    } else deadline = 0;
    const auto now = sf::Now();
    if (lastFrame && now > lastFrame) {
        const auto micros = std::min<int64_t>((now - lastFrame) * 1000000 / frequency, MAXLONG);
        const auto seq = static_cast<uint32_t>(sf::Read(&shared->sequence));
        InterlockedExchange(&shared->micros[seq % sf::Capacity], static_cast<LONG>(micros));
        InterlockedExchange(&shared->api, api);
        InterlockedExchange(&shared->sequence, static_cast<LONG>(seq + 1));
    }
    lastFrame = now;
    ReleaseSRWLockExclusive(&pacingLock);
}
struct Scope { bool outer = (++nesting == 1); ~Scope() { --nesting; } };
using Present = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1 = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using Present9 = HRESULT (STDMETHODCALLTYPE*)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
using Present9Ex = HRESULT (STDMETHODCALLTYPE*)(IDirect3DDevice9Ex*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
using Swap = BOOL (WINAPI*)(HDC);
static Present originalPresent{};
static Present1 originalPresent1{};
static Present9 original9{};
static Present9Ex original9Ex{};
static Swap originalSwap{};
static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* chain, UINT sync, UINT flags) {
    Scope scope;
    if (scope.outer && !(flags & (DXGI_PRESENT_TEST | DXGI_PRESENT_DO_NOT_WAIT))) Pace(chain, 1);
    return originalPresent(chain, sync, flags);
}
static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* chain, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params) {
    Scope scope;
    if (scope.outer && !(flags & (DXGI_PRESENT_TEST | DXGI_PRESENT_DO_NOT_WAIT))) Pace(chain, 1);
    return originalPresent1(chain, sync, flags, params);
}
static HRESULT STDMETHODCALLTYPE Hook9(IDirect3DDevice9* device, const RECT* a, const RECT* b, HWND c, const RGNDATA* d) {
    Scope scope;
    if (scope.outer) Pace(device, 2);
    return original9(device, a, b, c, d);
}
static HRESULT STDMETHODCALLTYPE Hook9Ex(IDirect3DDevice9Ex* device, const RECT* a, const RECT* b, HWND c, const RGNDATA* d, DWORD flags) {
    Scope scope;
    if (scope.outer && !(flags & D3DPRESENT_DONOTWAIT)) Pace(device, 2);
    return original9Ex(device, a, b, c, d, flags);
}
static BOOL WINAPI HookSwap(HDC dc) {
    Scope scope;
    if (scope.outer) Pace(dc, 3);
    return originalSwap(dc);
}
template<typename T> static bool Hook(void* target, T replacement, T* original) {
    return MH_CreateHook(target, reinterpret_cast<void*>(replacement), reinterpret_cast<void**>(original)) == MH_OK;
}
static DWORD WINAPI Initialize(void*) {
    const auto name = sf::MappingName(GetCurrentProcessId());
    const HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
    if (!mapping) return 0;
    shared = static_cast<sf::Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(sf::Shared)));
    if (!shared || shared->magic != sf::Magic || shared->version != sf::Version) return 0;
    // Hooks remain resident until game exit; disabling only removes the cap, never unloads executing code.
    timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002, TIMER_ALL_ACCESS);
    if (!timer) timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    if (MH_Initialize() != MH_OK) { InterlockedExchange(&shared->status, -1); return 0; }
    const HWND window = CreateWindowExW(0, L"STATIC", L"SmoothFrames probe", WS_POPUP, 0, 0, 16, 16, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    int hooks = 0;
    if (window) {
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = desc.BufferDesc.Height = 16;
        desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 1;
        desc.OutputWindow = window;
        desc.Windowed = TRUE;
        IDXGISwapChain* chain{};
        ID3D11Device* device{};
        auto hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, nullptr);
        if (FAILED(hr)) hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, nullptr);
        if (SUCCEEDED(hr)) {
            auto table = *reinterpret_cast<void***>(chain);
            hooks += Hook(table[8], HookPresent, &originalPresent);
            IDXGISwapChain1* chain1{};
            if (SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&chain1)))) {
                hooks += Hook((*reinterpret_cast<void***>(chain1))[22], HookPresent1, &originalPresent1);
                chain1->Release();
            }
            chain->Release();
            device->Release();
        }
        IDirect3D9Ex* d3d{};
        if (SUCCEEDED(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d))) {
            D3DPRESENT_PARAMETERS params{};
            params.Windowed = TRUE;
            params.SwapEffect = D3DSWAPEFFECT_DISCARD;
            params.hDeviceWindow = window;
            IDirect3DDevice9Ex* device9{};
            if (SUCCEEDED(d3d->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_NOWINDOWCHANGES, &params, nullptr, &device9))) {
                auto table = *reinterpret_cast<void***>(device9);
                hooks += Hook(table[17], Hook9, &original9);
                hooks += Hook(table[121], Hook9Ex, &original9Ex);
                device9->Release();
            }
            d3d->Release();
        }
        DestroyWindow(window);
    }
    if (auto gdi = GetModuleHandleW(L"gdi32.dll"))
        hooks += Hook(reinterpret_cast<void*>(GetProcAddress(gdi, "SwapBuffers")), HookSwap, &originalSwap);
    const bool enabled = hooks > 0 && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
    InterlockedExchange(&shared->status, enabled ? 1 : -1);
    return 0;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        if (HANDLE thread = CreateThread(nullptr, 0, Initialize, nullptr, 0, nullptr)) CloseHandle(thread);
    }
    return TRUE;
}
