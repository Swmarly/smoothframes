#include <windows.h>
#include <tlhelp32.h>
#include <iostream>
#include <string>
#include <cwchar>
#include <cstdint>

struct Handle {
    HANDLE value;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
static uintptr_t ModuleBase(DWORD pid, const wchar_t* name) {
    Handle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)};
    MODULEENTRY32W entry{sizeof(entry)};
    if (snapshot.value != INVALID_HANDLE_VALUE && Module32FirstW(snapshot.value, &entry)) {
        do {
            if (_wcsicmp(entry.szModule, name) == 0) return reinterpret_cast<uintptr_t>(entry.modBaseAddr);
        } while (Module32NextW(snapshot.value, &entry));
    }
    return 0;
}
static int Fail(const wchar_t* reason) {
    std::wcerr << reason << L" (Windows " << GetLastError() << L").";
    return 1;
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return Fail(L"Expected process ID and creation time");
    const DWORD pid = wcstoul(argv[1], nullptr, 10);
    if (!pid) return Fail(L"Invalid process ID");
    Handle process{OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid)};
    if (!process.value) return Fail(L"Cannot open the game. Use the same Windows permissions; protected games may reject attachment");
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process.value, &created, &exited, &kernel, &user)) return Fail(L"Cannot identify the game");
    const ULONGLONG stamp = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    if (stamp != _wcstoui64(argv[2], nullptr, 10)) return Fail(L"The selected process has changed. Refresh games");
    if (ModuleBase(pid, L"SmoothFramesHook.dll")) return 0;
    wchar_t ownPath[32768]{};
    if (!GetModuleFileNameW(nullptr, ownPath, 32768)) return Fail(L"Cannot locate the bundled engine");
    const std::wstring path(ownPath);
    const auto dll = path.substr(0, path.find_last_of(L"\\/") + 1) + L"SmoothFramesHook.dll";
    if (GetFileAttributesW(dll.c_str()) == INVALID_FILE_ATTRIBUTES) return Fail(L"Bundled engine is missing. Extract the complete release zip");
    const auto load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HMODULE implementation{};
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(load), &implementation)) return Fail(L"Cannot resolve Windows loader");
    wchar_t modulePath[MAX_PATH]{};
    GetModuleFileNameW(implementation, modulePath, MAX_PATH);
    const wchar_t* leaf = wcsrchr(modulePath, L'\\');
    const auto remoteBase = ModuleBase(pid, leaf ? leaf + 1 : modulePath);
    if (!remoteBase) return Fail(L"Cannot locate Windows loader in game");
    const auto remoteLoad = remoteBase + reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(implementation);
    const SIZE_T size = (dll.size() + 1) * sizeof(wchar_t);
    void* remotePath = VirtualAllocEx(process.value, nullptr, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!remotePath) return Fail(L"Cannot allocate engine path");
    if (!WriteProcessMemory(process.value, remotePath, dll.c_str(), size, nullptr)) {
        VirtualFreeEx(process.value, remotePath, 0, MEM_RELEASE);
        return Fail(L"Cannot send engine path");
    }
    Handle thread{CreateRemoteThread(process.value, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteLoad), remotePath, 0, nullptr)};
    if (!thread.value) {
        VirtualFreeEx(process.value, remotePath, 0, MEM_RELEASE);
        return Fail(L"The game rejected attachment");
    }
    if (WaitForSingleObject(thread.value, 10000) != WAIT_OBJECT_0)
        return Fail(L"Attachment timed out. Restart the game before retrying"); // The loader may still read its path.
    VirtualFreeEx(process.value, remotePath, 0, MEM_RELEASE);
    if (!ModuleBase(pid, L"SmoothFramesHook.dll")) return Fail(L"Windows could not load the bundled engine");
    return 0;
}
