// Connects to MeetCamDShow in-process and checks that a YUY2 frame arrives.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dshow.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

const int kWidth = 1280;
const int kHeight = 720;

class SinkPin : public IPin, public IMemInputPin {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** object) {
        if (object == nullptr)
            return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IPin)
            *object = static_cast<IPin*>(this);
        else if (riid == IID_IMemInputPin)
            *object = static_cast<IMemInputPin*>(this);
        else {
            *object = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&refs_); }
    STDMETHODIMP_(ULONG) Release() {
        ULONG refs = InterlockedDecrement(&refs_);
        if (refs == 0)
            delete this;
        return refs;
    }
    STDMETHODIMP Connect(IPin*, const AM_MEDIA_TYPE*) { return E_UNEXPECTED; }
    STDMETHODIMP ReceiveConnection(IPin*, const AM_MEDIA_TYPE* type) {
        return (type != nullptr && type->subtype == MEDIASUBTYPE_YUY2) ? S_OK : VFW_E_TYPE_NOT_ACCEPTED;
    }
    STDMETHODIMP Disconnect() { return S_OK; }
    STDMETHODIMP ConnectedTo(IPin** pin) {
        if (pin == nullptr)
            return E_POINTER;
        return VFW_E_NOT_CONNECTED;
    }
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE*) { return VFW_E_NOT_CONNECTED; }
    STDMETHODIMP QueryPinInfo(PIN_INFO* info) {
        if (info == nullptr)
            return E_POINTER;
        ZeroMemory(info, sizeof(*info));
        info->dir = PINDIR_INPUT;
        lstrcpynW(info->achName, L"In", MAX_PIN_NAME);
        return S_OK;
    }
    STDMETHODIMP QueryDirection(PIN_DIRECTION* direction) {
        if (direction == nullptr)
            return E_POINTER;
        *direction = PINDIR_INPUT;
        return S_OK;
    }
    STDMETHODIMP QueryId(LPWSTR* id) {
        if (id == nullptr)
            return E_POINTER;
        *id = static_cast<LPWSTR>(CoTaskMemAlloc(sizeof(L"In")));
        if (*id == nullptr)
            return E_OUTOFMEMORY;
        memcpy(*id, L"In", sizeof(L"In"));
        return S_OK;
    }
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* type) {
        return (type != nullptr && type->subtype == MEDIASUBTYPE_YUY2) ? S_OK : S_FALSE;
    }
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** enumerator) {
        if (enumerator == nullptr)
            return E_POINTER;
        *enumerator = nullptr;
        return VFW_E_NOT_FOUND;
    }
    STDMETHODIMP QueryInternalConnections(IPin**, ULONG*) { return E_NOTIMPL; }
    STDMETHODIMP EndOfStream() { return S_OK; }
    STDMETHODIMP BeginFlush() { return S_OK; }
    STDMETHODIMP EndFlush() { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) { return S_OK; }

    STDMETHODIMP GetAllocator(IMemAllocator** allocator) {
        if (allocator != nullptr)
            *allocator = nullptr;
        return E_FAIL;
    }
    STDMETHODIMP NotifyAllocator(IMemAllocator*, BOOL) { return S_OK; }
    STDMETHODIMP GetAllocatorRequirements(ALLOCATOR_PROPERTIES*) { return E_NOTIMPL; }
    STDMETHODIMP Receive(IMediaSample* sample) {
        if (sample == nullptr)
            return E_POINTER;
        BYTE* data = nullptr;
        long size = sample->GetActualDataLength();
        if (FAILED(sample->GetPointer(&data)) || data == nullptr || size < kWidth * kHeight * 2)
            return S_OK;
        int pair = ((kHeight / 2) * kWidth + (kWidth / 2 & ~1)) * 2;
        y_ = data[pair];
        u_ = data[pair + 1];
        v_ = data[pair + 3];
        InterlockedIncrement(&frames_);
        return S_OK;
    }
    STDMETHODIMP ReceiveMultiple(IMediaSample** samples, long count, long* processed) {
        long done = 0;
        HRESULT hr = S_OK;
        while (done < count) {
            hr = Receive(samples[done]);
            if (hr != S_OK)
                break;
            ++done;
        }
        if (processed != nullptr)
            *processed = done;
        return hr;
    }
    STDMETHODIMP ReceiveCanBlock() { return S_FALSE; }

    LONG frames_ = 0;
    uint8_t y_ = 0;
    uint8_t u_ = 0;
    uint8_t v_ = 0;

private:
    LONG refs_ = 1;
};

using DllGetClassObjectFn = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);

}  // namespace

int wmain() {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        printf("CoInitialize 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }

    HMODULE module = LoadLibraryW(L"MeetCamDShow.dll");
    if (module == nullptr) {
        printf("LoadLibrary failed %lu\n", GetLastError());
        return 1;
    }
    auto getClass = reinterpret_cast<DllGetClassObjectFn>(GetProcAddress(module, "DllGetClassObject"));
    if (getClass == nullptr) {
        printf("DllGetClassObject missing\n");
        return 1;
    }

    const GUID clsid = {0xb7e4c1a2, 0x6d38, 0x4f05, {0x9a, 0x71, 0x3c, 0x8e, 0x5d, 0x0b, 0x4f, 0x26}};
    IClassFactory* factory = nullptr;
    hr = getClass(clsid, IID_IClassFactory, reinterpret_cast<void**>(&factory));
    if (FAILED(hr)) {
        printf("class factory 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }
    IBaseFilter* filter = nullptr;
    hr = factory->CreateInstance(nullptr, IID_IBaseFilter, reinterpret_cast<void**>(&filter));
    factory->Release();
    if (FAILED(hr)) {
        printf("create 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }

    IEnumPins* pins = nullptr;
    filter->EnumPins(&pins);
    IPin* pin = nullptr;
    pins->Next(1, &pin, nullptr);
    pins->Release();

    IKsPropertySet* props = nullptr;
    hr = pin->QueryInterface(IID_IKsPropertySet, reinterpret_cast<void**>(&props));
    if (FAILED(hr)) {
        printf("IKsPropertySet 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }
    GUID category{};
    DWORD returned = 0;
    hr = props->Get(AMPROPSETID_Pin, AMPROPERTY_PIN_CATEGORY, nullptr, 0, &category, sizeof(category), &returned);
    props->Release();
    if (FAILED(hr) || category != PIN_CATEGORY_CAPTURE) {
        printf("pin category 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }

    SinkPin* sink = new SinkPin();
    hr = pin->Connect(sink, nullptr);
    if (FAILED(hr)) {
        printf("connect 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }
    hr = filter->Run(0);
    if (FAILED(hr)) {
        printf("run 0x%08lx\n", static_cast<unsigned long>(hr));
        return 1;
    }
    Sleep(400);
    filter->Stop();
    pin->Disconnect();
    printf("frames=%ld Y=%u U=%u V=%u\n", sink->frames_, sink->y_, sink->u_, sink->v_);
    bool ok = sink->frames_ >= 2;
    wchar_t frameOverride[4];
    // An isolated frame file that failed to parse used to be delivered as solid black.
    if (GetEnvironmentVariableW(L"MEETCAM_FRAME", frameOverride, 4) > 0 &&
        sink->y_ < 30 && sink->u_ > 110 && sink->u_ < 146 && sink->v_ > 110 && sink->v_ < 146)
        ok = false;
    sink->Release();
    pin->Release();
    filter->Release();
    FreeLibrary(module);
    CoUninitialize();
    if (!ok) {
        printf("DSHOW_TEST_FAIL\n");
        return 1;
    }
    printf("DSHOW_TEST_OK\n");
    return 0;
}
