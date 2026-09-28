#include <windows.h>
#include <unknwn.h>
#include <string>
#include <cstdio>

// The CLR hosting ABI is independent of UE4SS and the game's UE version.
struct ClrHost : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Start() = 0;
    virtual HRESULT STDMETHODCALLTYPE Stop() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetHostControl(void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCLRControl(void**) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnloadAppDomain(DWORD, BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE ExecuteInAppDomain(DWORD, void*, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentAppDomainId(DWORD*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ExecuteApplication(const wchar_t*, DWORD, const wchar_t**, DWORD, const wchar_t**, int*) = 0;
    virtual HRESULT STDMETHODCALLTYPE ExecuteInDefaultAppDomain(const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, DWORD*) = 0;
};
static HMODULE module;
static volatile LONG started;
static const GUID hostClass = {0x90f1a06e,0x7712,0x4762,{0x86,0xb5,0x7a,0x5e,0xba,0x6b,0xdb,0x02}};
static const GUID hostInterface = {0x90f1a06c,0x7712,0x4762,{0x86,0xb5,0x7a,0x5e,0xba,0x6b,0xdb,0x02}};

static DWORD WINAPI start_worker(void*) {
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(module, path, 32768);
    if (!length || length >= 32768) return 1;
    std::wstring root(path, length);
    root.resize(root.find_last_of(L"\\/"));
    auto core = LoadLibraryW(L"mscoree.dll");
    HRESULT result = E_FAIL;
    DWORD managedResult = 1;
    if (core) {
        using Bind = HRESULT (STDAPICALLTYPE*)(const wchar_t*,const wchar_t*,DWORD,REFCLSID,REFIID,void**);
        auto bind = reinterpret_cast<Bind>(GetProcAddress(core, "CorBindToRuntimeEx"));
        ClrHost* host = nullptr;
        if (bind) result = bind(L"v4.0.30319", L"wks", 0, hostClass, hostInterface, reinterpret_cast<void**>(&host));
        if (SUCCEEDED(result) && host) {
            result = host->Start();
            if (SUCCEEDED(result)) {
                std::wstring dll = root + L"\\DragonSwordRadar.Managed.dll";
                result = host->ExecuteInDefaultAppDomain(dll.c_str(), L"DragonSwordTreasureRadar.InProcessEntry", L"Start", root.c_str(), &managedResult);
            }
            host->Release();
        }
    }
    FILE* log = _wfopen((root + L"\\native_host.log").c_str(), L"a");
    if (log) { fprintf(log, "CLR host HRESULT=0x%08lX managed=%lu\n", static_cast<unsigned long>(result), managedResult); fclose(log); }
    return FAILED(result) ? 1 : managedResult;
}

// Lua's package.loadlib retains the library. No private Lua ABI or UE offsets used.
extern "C" __declspec(dllexport) int luaopen_radar_native(void*) {
    if (InterlockedCompareExchange(&started, 1, 0) == 0) {
        HANDLE thread = CreateThread(nullptr, 0, start_worker, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread); else InterlockedExchange(&started, 0);
    }
    return 0;
}

int Kraken_Decompress(const unsigned char*, size_t, unsigned char*, size_t);
extern "C" __declspec(dllexport) int radar_decompress(const unsigned char* input, size_t inputSize, unsigned char* output, size_t outputSize) {
    return Kraken_Decompress(input, inputSize, output, outputSize);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
