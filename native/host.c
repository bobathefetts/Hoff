#define COBJMACROS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <oleauto.h>
#include <mscoree.h>
#include <stdio.h>

typedef HRESULT (WINAPI *BindRuntime)(LPCWSTR, LPCWSTR, DWORD, REFCLSID, REFIID, LPVOID *);
static const GUID runtime_clsid = {0xcb2f6723,0xab3a,0x11d2,{0x9c,0x40,0x00,0xc0,0x4f,0xa3,0x0a,0x3e}};
static const GUID runtime_iid = {0xcb2f6722,0xab3a,0x11d2,{0x9c,0x40,0x00,0xc0,0x4f,0xa3,0x0a,0x3e}};

static const GUID app_domain_iid = {0x05f696dc,0x2b29,0x3663,{0xad,0x8b,0xc4,0x38,0x9c,0xf2,0xa7,0x13}};
static const GUID assembly_iid = {0x17156360,0x2f1a,0x384a,{0xbc,0x52,0xfd,0xe9,0x3c,0x21,0x5c,0x5b}};
static const GUID method_iid = {0xffcc1b5d,0xecb8,0x38dd,{0x9b,0x01,0x3d,0xc8,0xab,0xc2,0xaa,0x5f}};

/* Fixed CLR 4 COM ABI from the mscorlib type library. The slots include
   IUnknown and, for the dual _Assembly interface, the inherited IDispatch. */
enum { APP_DOMAIN_LOAD_BYTES = 45, ASSEMBLY_ENTRY_POINT = 16, METHOD_INVOKE = 37 };
typedef HRESULT (STDMETHODCALLTYPE *LoadBytes)(IUnknown *, SAFEARRAY *, IUnknown **);
typedef HRESULT (STDMETHODCALLTYPE *EntryPoint)(IUnknown *, IUnknown **);
typedef HRESULT (STDMETHODCALLTYPE *InvokeMethod)(IUnknown *, VARIANT, SAFEARRAY *, VARIANT *);
static void *slot(IUnknown *object, size_t index) { return ((void **)(object->lpVtbl))[index]; }

static HRESULT run(HINSTANCE instance, int *exit_code, const wchar_t **stage) {
    ICorRuntimeHost *runtime = NULL;
    IUnknown *domain = NULL, *domain_api = NULL, *assembly = NULL, *assembly_api = NULL;
    IUnknown *method = NULL, *method_api = NULL;
    SAFEARRAY *bytes = NULL;
    VARIANT target, result;
    HRESULT hr;
    void *destination;
    HRSRC resource = FindResourceW(instance, MAKEINTRESOURCEW(101), MAKEINTRESOURCEW(10));
    DWORD size = resource ? SizeofResource(instance, resource) : 0;
    void *source = size ? LockResource(LoadResource(instance, resource)) : NULL;
    VariantInit(&target);
    VariantInit(&result);
    if (!source) return HRESULT_FROM_WIN32(ERROR_RESOURCE_DATA_NOT_FOUND);
    *stage = L"starting the Windows .NET Framework";
    HMODULE shim = LoadLibraryExW(L"mscoree.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!shim) return HRESULT_FROM_WIN32(GetLastError());
    BindRuntime bind = (BindRuntime)GetProcAddress(shim, "CorBindToRuntimeEx");
    if (!bind) return HRESULT_FROM_WIN32(GetLastError());
    hr = bind(L"v4.0.30319", L"wks", 0, &runtime_clsid, &runtime_iid, (void **)&runtime);
    if (FAILED(hr)) goto done;
    if (FAILED(hr = ICorRuntimeHost_Start(runtime))) goto done;
    *stage = L"loading the embedded application";
    if (FAILED(hr = ICorRuntimeHost_GetDefaultDomain(runtime, &domain))) goto done;
    if (FAILED(hr = IUnknown_QueryInterface(domain, &app_domain_iid, (void **)&domain_api))) goto done;
    bytes = SafeArrayCreateVector(VT_UI1, 0, size);
    if (!bytes) { hr = E_OUTOFMEMORY; goto done; }
    if (FAILED(hr = SafeArrayAccessData(bytes, &destination))) goto done;
    CopyMemory(destination, source, size);
    SafeArrayUnaccessData(bytes);
    if (FAILED(hr = ((LoadBytes)slot(domain_api, APP_DOMAIN_LOAD_BYTES))(domain_api, bytes, &assembly))) goto done;
    if (FAILED(hr = IUnknown_QueryInterface(assembly, &assembly_iid, (void **)&assembly_api))) goto done;
    if (FAILED(hr = ((EntryPoint)slot(assembly_api, ASSEMBLY_ENTRY_POINT))(assembly_api, &method))) goto done;
    if (!method) { hr = E_UNEXPECTED; goto done; }
    if (FAILED(hr = IUnknown_QueryInterface(method, &method_iid, (void **)&method_api))) goto done;
    *stage = L"running Media Compliance Reviewer";
    target.vt = VT_NULL;
    hr = ((InvokeMethod)slot(method_api, METHOD_INVOKE))(method_api, target, NULL, &result);
    if (SUCCEEDED(hr)) {
        if (result.vt == VT_I4) *exit_code = result.lVal;
        else hr = E_UNEXPECTED;
    }
done:
    VariantClear(&result);
    if (method_api) IUnknown_Release(method_api);
    if (method) IUnknown_Release(method);
    if (assembly_api) IUnknown_Release(assembly_api);
    if (assembly) IUnknown_Release(assembly);
    if (bytes) SafeArrayDestroy(bytes);
    if (domain_api) IUnknown_Release(domain_api);
    if (domain) IUnknown_Release(domain);
    if (runtime) ICorRuntimeHost_Release(runtime);
    return hr;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show) {
    int exit_code = 1;
    const wchar_t *stage = L"reading the embedded application";
    /* The exe ships alone and needs no DLL from its own folder, which is often Downloads. The link step sets
       --dependent-load-flag=0x800 so the static imports (such as bcrypt.dll) come from System32 only; this does the
       same for every DLL loaded by name later, so a DLL planted beside the exe is never picked up. Folders that
       code in the process adds on purpose with AddDllDirectory are still searched. */
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS);
    HRESULT init = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    HRESULT hr = FAILED(init) ? init : run(instance, &exit_code, &stage);
    if (SUCCEEDED(init)) CoUninitialize();
    if (FAILED(hr)) {
        wchar_t message[256];
        swprintf(message, 256, L"Error 0x%08lX while %ls.", (unsigned long)hr, stage);
        if (!wcsstr(arguments, L"--self-test") && !wcsstr(arguments, L"--window-smoke") && !wcsstr(arguments, L"--preview-worker") && !wcsstr(arguments, L"--draft-worker") && !wcsstr(arguments, L"--verify-previews") && !wcsstr(arguments, L"--verify-import"))
            MessageBoxW(NULL, message, L"Media Compliance Reviewer could not start", MB_OK | MB_ICONERROR);
        fwprintf(stderr, L"%ls\n", message);
        return 1;
    }
    return exit_code;
}
