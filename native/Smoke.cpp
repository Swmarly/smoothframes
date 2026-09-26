#include "Shared.h"
#include <d3d11.h>
#include <iostream>
#include <stdexcept>

static void Check(bool result, const char* message) { if (!result) throw std::runtime_error(message); }
static int Render() {
    HWND window = CreateWindowExW(0, L"STATIC", L"SmoothFrames integration fixture", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        0, 0, 128, 128, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return 2;
    DXGI_SWAP_CHAIN_DESC desc{};
    desc.BufferDesc.Width = desc.BufferDesc.Height = 64;
    desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1; desc.BufferCount = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.OutputWindow = window; desc.Windowed = TRUE;
    IDXGISwapChain* chain{}; ID3D11Device* device{}; ID3D11DeviceContext* context{};
    auto hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
    if (FAILED(hr)) hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &desc, &chain, &device, nullptr, &context);
    if (FAILED(hr)) return 3;
    const auto name = sf::MappingName(GetCurrentProcessId());
    HANDLE mapping{};
    sf::Shared* control{};
    const auto start = GetTickCount();
    while (GetTickCount() - start < 35000) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (!control) {
            mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
            if (mapping) control = static_cast<sf::Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(sf::Shared)));
        }
        if (control) InterlockedIncrement(&control->reserved); // Independent fixture frame counter.
        chain->Present(0, 0);
    }
    if (control) UnmapViewOfFile(control);
    if (mapping) CloseHandle(mapping);
    context->Release(); device->Release(); chain->Release(); DestroyWindow(window);
    return 0;
}
struct Child {
    PROCESS_INFORMATION pi{};
    ~Child() {
        if (pi.hProcess) { TerminateProcess(pi.hProcess, 0); CloseHandle(pi.hProcess); }
        if (pi.hThread) CloseHandle(pi.hThread);
    }
    void Start(std::wstring command) {
        STARTUPINFOW si{sizeof(si)};
        Check(CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi) != 0, "CreateProcess failed");
    }
};
static double Rate(sf::Shared* shared, DWORD milliseconds, bool heartbeat = true) {
    const auto before = static_cast<uint32_t>(sf::Read(&shared->sequence));
    const auto start = GetTickCount();
    while (GetTickCount() - start < milliseconds) {
        if (heartbeat) InterlockedExchange(&shared->heartbeat, static_cast<LONG>(GetTickCount()));
        Sleep(10);
    }
    return static_cast<uint32_t>(sf::Read(&shared->sequence) - before) * 1000.0 / (GetTickCount() - start);
}
int wmain(int argc, wchar_t**) {
    if (argc > 1) return Render();
    try {
        wchar_t executable[32768]{};
        GetModuleFileNameW(nullptr, executable, 32768);
        const std::wstring own(executable);
        const auto directory = own.substr(0, own.find_last_of(L"\\/") + 1);
        Child game; game.Start(L"\"" + own + L"\" --render");
        const auto name = sf::MappingName(game.pi.dwProcessId);
        const HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(sf::Shared), name.c_str());
        Check(mapping != nullptr, "Mapping failed");
        auto shared = static_cast<sf::Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(sf::Shared)));
        Check(shared != nullptr, "View failed");
        shared->magic = sf::Magic; shared->version = sf::Version;
        InterlockedExchange(&shared->heartbeat, static_cast<LONG>(GetTickCount()));
        FILETIME created{}, exited{}, kernel{}, user{};
        Check(GetProcessTimes(game.pi.hProcess, &created, &exited, &kernel, &user) != 0, "Process identity failed");
        const auto stamp = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        Sleep(500);
        Child injector;
        injector.Start(L"\"" + directory + L"SmoothFramesInjector.exe\" " + std::to_wstring(game.pi.dwProcessId) + L" " + std::to_wstring(stamp));
        Check(WaitForSingleObject(injector.pi.hProcess, 15000) == WAIT_OBJECT_0, "Injector timed out");
        DWORD code{}; GetExitCodeProcess(injector.pi.hProcess, &code);
        Check(code == 0, "Injector failed");
        const auto waitStart = GetTickCount();
        while (sf::Read(&shared->status) == 0 && GetTickCount() - waitStart < 10000) {
            InterlockedExchange(&shared->heartbeat, static_cast<LONG>(GetTickCount())); Sleep(20);
        }
        Check(sf::Read(&shared->status) == 1, "Hooks failed to initialize");
        InterlockedExchange(&shared->targetMilliFps, 30000);
        Rate(shared, 200);
        const auto low = Rate(shared, 1600);
        InterlockedExchange(&shared->targetMilliFps, 60000);
        Rate(shared, 200);
        const auto high = Rate(shared, 1600);
        InterlockedExchange(&shared->targetMilliFps, 0);
        Rate(shared, 200);
        const auto uncapped = Rate(shared, 1000);
        InterlockedExchange(&shared->targetMilliFps, 30000);
        Rate(shared, 200);
        // Simulate a controller crash while 30 FPS is requested. Telemetry also stops on expiry.
        Rate(shared, 3000, false);
        const auto stale = static_cast<uint32_t>(sf::Read(&shared->sequence));
        const auto renderStart = static_cast<uint32_t>(sf::Read(&shared->reserved));
        const auto clockStart = GetTickCount();
        Sleep(500);
        const auto failOpenRate = static_cast<uint32_t>(sf::Read(&shared->reserved) - renderStart) * 1000.0 / (GetTickCount() - clockStart);
        Check(failOpenRate > high * 1.3, "Controller loss did not remove the cap");
        Check(static_cast<uint32_t>(sf::Read(&shared->sequence)) == stale, "Stale controller was not ignored");
        // Resume heartbeat with cap disabled; capture recovers without re-injecting.
        InterlockedExchange(&shared->targetMilliFps, 0);
        Rate(shared, 200);
        const auto recovered = Rate(shared, 1000);
        std::cout << "30 cap: " << low << "; 60 cap: " << high << "; disabled: " << uncapped << "; controller lost: " << failOpenRate << "; recovered: " << recovered << '\n';
        Check(low >= 22 && low <= 38, "30 FPS pacing outside tolerance");
        Check(high >= 44 && high <= 75 && high > low * 1.4, "60 FPS pacing outside tolerance");
        Check(uncapped > high * 1.3 && recovered > high * 1.3, "Disable/recovery did not remove cap");
        Check(sf::Read(&shared->api) == 1, "DXGI telemetry missing");
        Child wrongIdentity;
        wrongIdentity.Start(L"\"" + directory + L"SmoothFramesInjector.exe\" " + std::to_wstring(game.pi.dwProcessId) + L" 1");
        Check(WaitForSingleObject(wrongIdentity.pi.hProcess, 5000) == WAIT_OBJECT_0, "Identity rejection timed out");
        GetExitCodeProcess(wrongIdentity.pi.hProcess, &code);
        Check(code != 0, "Process identity mismatch was accepted");
        UnmapViewOfFile(shared); CloseHandle(mapping);
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
