// DirectShow capture source for Firefox. Firefox 159 opens cameras only
// through DirectShow and accepts I420, YUY2, RGB24, UYVY, and MJPEG.
// The Media Foundation virtual camera is invisible there.
// Frames come from the same file the player writes:
// C:\Users\Public\MeetCam\frame.bin (BGRA 1280x720, three finished slots).
// MEETCAM_FRAME overrides that path for tests only.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dshow.h>
#include <cstdint>
#include <cstdlib>
#include <cstring>

namespace {

const int kWidth = 1280;
const int kHeight = 720;
const int kHeaderSize = 32;
const int kSlotCount = 3;
const uint32_t kMagic = 0x56434D32;
const int kPixelBytes = kWidth * kHeight * 4;
const REFERENCE_TIME kFrameDuration = 10000000 / 30;
const wchar_t* kFramePath = L"C:\\Users\\Public\\MeetCam\\frame.bin";

const wchar_t* FrameFilePath() {
    static wchar_t overridePath[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"MEETCAM_FRAME", overridePath, MAX_PATH);
    if (length > 0 && length < MAX_PATH)
        return overridePath;
    return kFramePath;
}

const GUID CLSID_MeetCamDShow = {
    0xb7e4c1a2, 0x6d38, 0x4f05, {0x9a, 0x71, 0x3c, 0x8e, 0x5d, 0x0b, 0x4f, 0x26}};

HMODULE g_module = nullptr;
volatile LONG g_locks = 0;
volatile LONG g_objects = 0;

void LogLine(const char* text) {
    HANDLE file = CreateFileW(L"C:\\Users\\Public\\MeetCam\\dshow.log", FILE_APPEND_DATA,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;
    DWORD written = 0;
    WriteFile(file, text, static_cast<DWORD>(lstrlenA(text)), &written, nullptr);
    WriteFile(file, "\r\n", 2, &written, nullptr);
    CloseHandle(file);
}

void FreeMediaType(AM_MEDIA_TYPE* type) {
    if (type == nullptr)
        return;
    if (type->cbFormat != 0) {
        CoTaskMemFree(type->pbFormat);
        type->pbFormat = nullptr;
        type->cbFormat = 0;
    }
    if (type->pUnk != nullptr) {
        type->pUnk->Release();
        type->pUnk = nullptr;
    }
}

void CopyMediaType(AM_MEDIA_TYPE* dest, const AM_MEDIA_TYPE* source) {
    *dest = *source;
    dest->pbFormat = nullptr;
    dest->pUnk = nullptr;
    if (source->cbFormat != 0 && source->pbFormat != nullptr) {
        dest->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(source->cbFormat));
        if (dest->pbFormat != nullptr)
            memcpy(dest->pbFormat, source->pbFormat, source->cbFormat);
        else
            dest->cbFormat = 0;
    } else {
        dest->cbFormat = 0;
    }
    if (source->pUnk != nullptr) {
        dest->pUnk = source->pUnk;
        dest->pUnk->AddRef();
    }
}

void FillYuy2Type(AM_MEDIA_TYPE* type) {
    ZeroMemory(type, sizeof(*type));
    type->majortype = MEDIATYPE_Video;
    type->subtype = MEDIASUBTYPE_YUY2;
    type->bFixedSizeSamples = TRUE;
    type->lSampleSize = kWidth * kHeight * 2;
    type->formattype = FORMAT_VideoInfo;
    type->cbFormat = sizeof(VIDEOINFOHEADER);
    type->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(sizeof(VIDEOINFOHEADER)));
    if (type->pbFormat == nullptr) {
        type->cbFormat = 0;
        return;
    }
    ZeroMemory(type->pbFormat, sizeof(VIDEOINFOHEADER));
    auto* header = reinterpret_cast<VIDEOINFOHEADER*>(type->pbFormat);
    header->AvgTimePerFrame = kFrameDuration;
    header->dwBitRate = static_cast<DWORD>(kWidth * kHeight * 16 * 30);
    header->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    header->bmiHeader.biWidth = kWidth;
    header->bmiHeader.biHeight = kHeight;
    header->bmiHeader.biPlanes = 1;
    header->bmiHeader.biBitCount = 16;
    header->bmiHeader.biCompression = MAKEFOURCC('Y', 'U', 'Y', '2');
    header->bmiHeader.biSizeImage = kWidth * kHeight * 2;
}

bool IsYuy2(const AM_MEDIA_TYPE* type) {
    if (type == nullptr)
        return false;
    if (type->majortype != MEDIATYPE_Video || type->subtype != MEDIASUBTYPE_YUY2)
        return false;
    if (type->pbFormat == nullptr || type->formattype != FORMAT_VideoInfo)
        return true;
    auto* header = reinterpret_cast<VIDEOINFOHEADER*>(type->pbFormat);
    return header->bmiHeader.biWidth == kWidth && abs(header->bmiHeader.biHeight) == kHeight &&
           header->bmiHeader.biCompression == MAKEFOURCC('Y', 'U', 'Y', '2');
}

int ClampByte(int value) {
    if (value < 0)
        return 0;
    if (value > 255)
        return 255;
    return value;
}

void BgraToYuy2(const uint8_t* bgra, uint8_t* yuy2, int width, int height) {
    for (int y = 0; y < height; ++y) {
        const uint8_t* src = bgra + static_cast<size_t>(y) * width * 4;
        uint8_t* dest = yuy2 + static_cast<size_t>(y) * width * 2;
        for (int x = 0; x < width; x += 2) {
            int b0 = src[0], g0 = src[1], r0 = src[2];
            int b1 = src[4], g1 = src[5], r1 = src[6];
            int y0 = ClampByte(((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16);
            int y1 = ClampByte(((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16);
            int r = (r0 + r1) / 2;
            int g = (g0 + g1) / 2;
            int b = (b0 + b1) / 2;
            int u = ClampByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
            int v = ClampByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            dest[0] = static_cast<uint8_t>(y0);
            dest[1] = static_cast<uint8_t>(u);
            dest[2] = static_cast<uint8_t>(y1);
            dest[3] = static_cast<uint8_t>(v);
            src += 8;
            dest += 4;
        }
    }
}

class FrameMap {
public:
    FrameMap() = default;
    ~FrameMap() { Close(); }

    bool Read(uint8_t* bgra, int bytes) {
        if (!Ensure() || view_ == nullptr)
            return false;
        uint32_t magic = *reinterpret_cast<volatile uint32_t*>(view_);
        MemoryBarrier();
        if (magic != kMagic)
            return false;
        if (*reinterpret_cast<volatile int*>(view_ + 4) != kWidth ||
            *reinterpret_cast<volatile int*>(view_ + 8) != kHeight)
            return false;

        // Copy a finished slot. If its index changes mid-copy, the bytes are
        // torn, so take the slot that is published now. Never return a mix.
        for (int attempt = 0; attempt < kSlotCount; ++attempt) {
            int slot = *reinterpret_cast<volatile int*>(view_ + 16);
            MemoryBarrier();
            if (slot < 0 || slot >= kSlotCount)
                return false;
            memcpy(bgra, view_ + kHeaderSize + (static_cast<size_t>(slot) * kPixelBytes), bytes);
            MemoryBarrier();
            if (*reinterpret_cast<volatile int*>(view_ + 16) == slot)
                return true;
        }
        return false;
    }

private:
    bool Ensure() {
        if (view_ != nullptr)
            return true;
        HANDLE file = CreateFileW(FrameFilePath(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) ||
            size.QuadPart < static_cast<LONGLONG>(kHeaderSize + (static_cast<size_t>(kSlotCount) * kPixelBytes))) {
            CloseHandle(file);
            return false;
        }
        HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        CloseHandle(file);
        if (mapping == nullptr)
            return false;
        view_ = static_cast<uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, kHeaderSize + (static_cast<size_t>(kSlotCount) * kPixelBytes)));
        CloseHandle(mapping);
        return view_ != nullptr;
    }

    void Close() {
        if (view_ != nullptr) {
            UnmapViewOfFile(view_);
            view_ = nullptr;
        }
    }

    uint8_t* view_ = nullptr;
};

class Filter;

class OutputPin : public IPin, public IAMStreamConfig, public IKsPropertySet {
public:
    explicit OutputPin(Filter* filter) : filter_(filter) {}
    ~OutputPin() { TearDown(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** object);
    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();

    STDMETHODIMP Connect(IPin* receive, const AM_MEDIA_TYPE* type);
    STDMETHODIMP ReceiveConnection(IPin*, const AM_MEDIA_TYPE*) { return E_UNEXPECTED; }
    STDMETHODIMP Disconnect();
    STDMETHODIMP ConnectedTo(IPin** pin);
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* type);
    STDMETHODIMP QueryPinInfo(PIN_INFO* info);
    STDMETHODIMP QueryDirection(PIN_DIRECTION* direction);
    STDMETHODIMP QueryId(LPWSTR* id);
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* type);
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** enumerator);
    STDMETHODIMP QueryInternalConnections(IPin**, ULONG*) { return E_NOTIMPL; }
    STDMETHODIMP EndOfStream() { return S_OK; }
    STDMETHODIMP BeginFlush() { return S_OK; }
    STDMETHODIMP EndFlush() { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) { return S_OK; }

    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* type);
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** type);
    STDMETHODIMP GetNumberOfCapabilities(int* count, int* size);
    STDMETHODIMP GetStreamCaps(int index, AM_MEDIA_TYPE** type, BYTE* caps);

    STDMETHODIMP Set(REFGUID, DWORD, LPVOID, DWORD, LPVOID, DWORD) { return E_NOTIMPL; }
    STDMETHODIMP Get(REFGUID set, DWORD id, LPVOID, DWORD, LPVOID data, DWORD dataSize, DWORD* returned);
    STDMETHODIMP QuerySupported(REFGUID set, DWORD id, DWORD* support);

    void StartDelivery();
    void StopDelivery();
    Filter* Owner() const { return filter_; }

private:
    HRESULT DecideAllocator();
    void TearDown();
    DWORD ThreadMain();
    static DWORD WINAPI ThreadProc(LPVOID self) { return static_cast<OutputPin*>(self)->ThreadMain(); }

    Filter* filter_;
    IPin* connected_ = nullptr;
    IMemInputPin* input_ = nullptr;
    IMemAllocator* allocator_ = nullptr;
    AM_MEDIA_TYPE mediaType_{};
    bool hasType_ = false;
    HANDLE thread_ = nullptr;
    HANDLE stopEvent_ = nullptr;
    LONG frames_ = 0;
};

class PinEnumerator : public IEnumPins {
public:
    PinEnumerator(Filter* filter, int position);
    ~PinEnumerator();
    STDMETHODIMP QueryInterface(REFIID riid, void** object);
    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();
    STDMETHODIMP Next(ULONG count, IPin** pins, ULONG* fetched);
    STDMETHODIMP Skip(ULONG count);
    STDMETHODIMP Reset();
    STDMETHODIMP Clone(IEnumPins** enumerator);

private:
    Filter* filter_;
    int position_;
    LONG refs_ = 1;
};

class MediaTypeEnumerator : public IEnumMediaTypes {
public:
    explicit MediaTypeEnumerator(int position) : position_(position) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** object);
    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();
    STDMETHODIMP Next(ULONG count, AM_MEDIA_TYPE** types, ULONG* fetched);
    STDMETHODIMP Skip(ULONG count);
    STDMETHODIMP Reset();
    STDMETHODIMP Clone(IEnumMediaTypes** enumerator);

private:
    int position_;
    LONG refs_ = 1;
};

class Filter : public IBaseFilter {
public:
    Filter();
    ~Filter();

    STDMETHODIMP QueryInterface(REFIID riid, void** object);
    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();
    STDMETHODIMP GetClassID(CLSID* clsid);

    STDMETHODIMP Stop();
    STDMETHODIMP Pause();
    STDMETHODIMP Run(REFERENCE_TIME start);
    STDMETHODIMP GetState(DWORD, FILTER_STATE* state);
    STDMETHODIMP SetSyncSource(IReferenceClock* clock);
    STDMETHODIMP GetSyncSource(IReferenceClock** clock);

    STDMETHODIMP EnumPins(IEnumPins** enumerator);
    STDMETHODIMP FindPin(LPCWSTR id, IPin** pin);
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* info);
    STDMETHODIMP JoinFilterGraph(IFilterGraph* graph, LPCWSTR name);
    STDMETHODIMP QueryVendorInfo(LPWSTR*) { return E_NOTIMPL; }

    OutputPin* Pin() const { return pin_; }
    FILTER_STATE State() const { return static_cast<FILTER_STATE>(state_); }
    CRITICAL_SECTION lock_;

private:
    LONG refs_ = 1;
    volatile LONG state_ = State_Stopped;
    OutputPin* pin_;
    IFilterGraph* graph_ = nullptr;
    IReferenceClock* clock_ = nullptr;
    wchar_t name_[128] = L"MeetCam";
};

class ClassFactory : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** object);
    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object);
    STDMETHODIMP LockServer(BOOL lock);

private:
    LONG refs_ = 1;
};

Filter::Filter() : pin_(new OutputPin(this)) {
    InitializeCriticalSection(&lock_);
    InterlockedIncrement(&g_objects);
}

Filter::~Filter() {
    delete pin_;
    if (clock_ != nullptr)
        clock_->Release();
    DeleteCriticalSection(&lock_);
    InterlockedDecrement(&g_objects);
}

STDMETHODIMP Filter::QueryInterface(REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IBaseFilter || riid == IID_IMediaFilter || riid == IID_IPersist)
        *object = static_cast<IBaseFilter*>(this);
    else {
        *object = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) Filter::AddRef() { return InterlockedIncrement(&refs_); }

STDMETHODIMP_(ULONG) Filter::Release() {
    ULONG refs = InterlockedDecrement(&refs_);
    if (refs == 0)
        delete this;
    return refs;
}

STDMETHODIMP Filter::GetClassID(CLSID* clsid) {
    if (clsid == nullptr)
        return E_POINTER;
    *clsid = CLSID_MeetCamDShow;
    return S_OK;
}

STDMETHODIMP Filter::Stop() {
    state_ = State_Stopped;
    pin_->StopDelivery();
    return S_OK;
}

STDMETHODIMP Filter::Pause() {
    if (state_ == State_Stopped)
        state_ = State_Paused;
    return S_OK;
}

STDMETHODIMP Filter::Run(REFERENCE_TIME) {
    if (state_ == State_Stopped)
        Pause();
    pin_->StartDelivery();
    state_ = State_Running;
    LogLine("Run");
    return S_OK;
}

STDMETHODIMP Filter::GetState(DWORD, FILTER_STATE* state) {
    if (state == nullptr)
        return E_POINTER;
    *state = static_cast<FILTER_STATE>(state_);
    return S_OK;
}

STDMETHODIMP Filter::SetSyncSource(IReferenceClock* clock) {
    if (clock_ != nullptr)
        clock_->Release();
    clock_ = clock;
    if (clock_ != nullptr)
        clock_->AddRef();
    return S_OK;
}

STDMETHODIMP Filter::GetSyncSource(IReferenceClock** clock) {
    if (clock == nullptr)
        return E_POINTER;
    *clock = clock_;
    if (clock_ != nullptr)
        clock_->AddRef();
    return S_OK;
}

STDMETHODIMP Filter::EnumPins(IEnumPins** enumerator) {
    if (enumerator == nullptr)
        return E_POINTER;
    *enumerator = new PinEnumerator(this, 0);
    return S_OK;
}

STDMETHODIMP Filter::FindPin(LPCWSTR id, IPin** pin) {
    if (pin == nullptr)
        return E_POINTER;
    if (id != nullptr && lstrcmpW(id, L"Capture") != 0)
        return VFW_E_NOT_FOUND;
    *pin = pin_;
    pin_->AddRef();
    return S_OK;
}

STDMETHODIMP Filter::QueryFilterInfo(FILTER_INFO* info) {
    if (info == nullptr)
        return E_POINTER;
    info->pGraph = graph_;
    if (graph_ != nullptr)
        graph_->AddRef();
    lstrcpynW(info->achName, name_, MAX_FILTER_NAME);
    return S_OK;
}

STDMETHODIMP Filter::JoinFilterGraph(IFilterGraph* graph, LPCWSTR name) {
    graph_ = graph;
    if (name != nullptr)
        lstrcpynW(name_, name, 128);
    return S_OK;
}

STDMETHODIMP OutputPin::QueryInterface(REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IPin)
        *object = static_cast<IPin*>(this);
    else if (riid == IID_IAMStreamConfig)
        *object = static_cast<IAMStreamConfig*>(this);
    else if (riid == IID_IKsPropertySet)
        *object = static_cast<IKsPropertySet*>(this);
    else {
        *object = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) OutputPin::AddRef() { return filter_->AddRef(); }
STDMETHODIMP_(ULONG) OutputPin::Release() { return filter_->Release(); }

STDMETHODIMP OutputPin::Connect(IPin* receive, const AM_MEDIA_TYPE* type) {
    if (receive == nullptr)
        return E_POINTER;
    if (connected_ != nullptr)
        return VFW_E_ALREADY_CONNECTED;

    PIN_DIRECTION direction;
    HRESULT hr = receive->QueryDirection(&direction);
    if (FAILED(hr))
        return hr;
    if (direction != PINDIR_INPUT)
        return VFW_E_INVALID_DIRECTION;

    AM_MEDIA_TYPE proposed{};
    bool ownProposed = false;
    const AM_MEDIA_TYPE* use = type;
    if (type == nullptr || type->majortype == GUID_NULL || type->subtype == GUID_NULL) {
        FillYuy2Type(&proposed);
        ownProposed = true;
        use = &proposed;
    }
    if (!IsYuy2(use)) {
        if (ownProposed)
            FreeMediaType(&proposed);
        return VFW_E_TYPE_NOT_ACCEPTED;
    }

    hr = receive->ReceiveConnection(static_cast<IPin*>(this), use);
    if (FAILED(hr)) {
        if (ownProposed)
            FreeMediaType(&proposed);
        return hr;
    }

    connected_ = receive;
    connected_->AddRef();
    hr = receive->QueryInterface(IID_IMemInputPin, reinterpret_cast<void**>(&input_));
    if (FAILED(hr)) {
        Disconnect();
        if (ownProposed)
            FreeMediaType(&proposed);
        return hr;
    }
    hr = DecideAllocator();
    if (FAILED(hr)) {
        Disconnect();
        if (ownProposed)
            FreeMediaType(&proposed);
        LogLine("DecideAllocator failed");
        return hr;
    }
    if (hasType_)
        FreeMediaType(&mediaType_);
    CopyMediaType(&mediaType_, use);
    hasType_ = true;
    if (ownProposed)
        FreeMediaType(&proposed);
    LogLine("Connected YUY2");
    return S_OK;
}

STDMETHODIMP OutputPin::Disconnect() {
    TearDown();
    return S_OK;
}

void OutputPin::TearDown() {
    StopDelivery();
    if (allocator_ != nullptr) {
        allocator_->Decommit();
        allocator_->Release();
        allocator_ = nullptr;
    }
    if (input_ != nullptr) {
        input_->Release();
        input_ = nullptr;
    }
    if (connected_ != nullptr) {
        connected_->Release();
        connected_ = nullptr;
    }
    if (hasType_) {
        FreeMediaType(&mediaType_);
        hasType_ = false;
    }
}

HRESULT OutputPin::DecideAllocator() {
    ALLOCATOR_PROPERTIES want{};
    want.cBuffers = 4;
    want.cbBuffer = kWidth * kHeight * 2;
    want.cbAlign = 1;
    ALLOCATOR_PROPERTIES actual{};

    IMemAllocator* allocator = nullptr;
    HRESULT hr = input_->GetAllocator(&allocator);
    if (SUCCEEDED(hr) && allocator != nullptr) {
        if (FAILED(allocator->SetProperties(&want, &actual)) || actual.cbBuffer < want.cbBuffer) {
            allocator->Release();
            allocator = nullptr;
        }
    }
    if (allocator == nullptr) {
        hr = CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER, IID_IMemAllocator,
                              reinterpret_cast<void**>(&allocator));
        if (FAILED(hr))
            return hr;
        hr = allocator->SetProperties(&want, &actual);
        if (FAILED(hr)) {
            allocator->Release();
            return hr;
        }
    }
    hr = input_->NotifyAllocator(allocator, FALSE);
    if (FAILED(hr)) {
        allocator->Release();
        return hr;
    }
    allocator_ = allocator;
    return S_OK;
}

STDMETHODIMP OutputPin::ConnectedTo(IPin** pin) {
    if (pin == nullptr)
        return E_POINTER;
    if (connected_ == nullptr)
        return VFW_E_NOT_CONNECTED;
    *pin = connected_;
    connected_->AddRef();
    return S_OK;
}

STDMETHODIMP OutputPin::ConnectionMediaType(AM_MEDIA_TYPE* type) {
    if (type == nullptr)
        return E_POINTER;
    if (!hasType_)
        return VFW_E_NOT_CONNECTED;
    CopyMediaType(type, &mediaType_);
    return S_OK;
}

STDMETHODIMP OutputPin::QueryPinInfo(PIN_INFO* info) {
    if (info == nullptr)
        return E_POINTER;
    info->pFilter = filter_;
    filter_->AddRef();
    info->dir = PINDIR_OUTPUT;
    lstrcpynW(info->achName, L"Capture", MAX_PIN_NAME);
    return S_OK;
}

STDMETHODIMP OutputPin::QueryDirection(PIN_DIRECTION* direction) {
    if (direction == nullptr)
        return E_POINTER;
    *direction = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP OutputPin::QueryId(LPWSTR* id) {
    if (id == nullptr)
        return E_POINTER;
    size_t bytes = sizeof(L"Capture");
    *id = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (*id == nullptr)
        return E_OUTOFMEMORY;
    memcpy(*id, L"Capture", bytes);
    return S_OK;
}

STDMETHODIMP OutputPin::QueryAccept(const AM_MEDIA_TYPE* type) {
    return IsYuy2(type) ? S_OK : S_FALSE;
}

STDMETHODIMP OutputPin::EnumMediaTypes(IEnumMediaTypes** enumerator) {
    if (enumerator == nullptr)
        return E_POINTER;
    *enumerator = new MediaTypeEnumerator(0);
    return S_OK;
}

STDMETHODIMP OutputPin::SetFormat(AM_MEDIA_TYPE* type) {
    if (type != nullptr && !IsYuy2(type))
        return VFW_E_INVALIDMEDIATYPE;
    return S_OK;
}

STDMETHODIMP OutputPin::GetFormat(AM_MEDIA_TYPE** type) {
    if (type == nullptr)
        return E_POINTER;
    *type = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (*type == nullptr)
        return E_OUTOFMEMORY;
    if (hasType_)
        CopyMediaType(*type, &mediaType_);
    else
        FillYuy2Type(*type);
    return S_OK;
}

STDMETHODIMP OutputPin::GetNumberOfCapabilities(int* count, int* size) {
    if (count == nullptr || size == nullptr)
        return E_POINTER;
    *count = 1;
    *size = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP OutputPin::GetStreamCaps(int index, AM_MEDIA_TYPE** type, BYTE* caps) {
    if (type == nullptr || caps == nullptr)
        return E_POINTER;
    if (index != 0)
        return S_FALSE;
    *type = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (*type == nullptr)
        return E_OUTOFMEMORY;
    FillYuy2Type(*type);
    auto* video = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(caps);
    ZeroMemory(video, sizeof(*video));
    video->guid = FORMAT_VideoInfo;
    video->InputSize.cx = kWidth;
    video->InputSize.cy = kHeight;
    video->MinCroppingSize = video->InputSize;
    video->MaxCroppingSize = video->InputSize;
    video->CropGranularityX = 1;
    video->CropGranularityY = 1;
    video->CropAlignX = 1;
    video->CropAlignY = 1;
    video->MinOutputSize = video->InputSize;
    video->MaxOutputSize = video->InputSize;
    video->OutputGranularityX = 1;
    video->OutputGranularityY = 1;
    video->MinFrameInterval = kFrameDuration;
    video->MaxFrameInterval = kFrameDuration;
    video->MinBitsPerSecond = kWidth * kHeight * 16 * 30;
    video->MaxBitsPerSecond = video->MinBitsPerSecond;
    return S_OK;
}

STDMETHODIMP OutputPin::Get(REFGUID set, DWORD id, LPVOID, DWORD, LPVOID data, DWORD dataSize, DWORD* returned) {
    if (set != AMPROPSETID_Pin || id != AMPROPERTY_PIN_CATEGORY)
        return E_PROP_ID_UNSUPPORTED;
    if (data == nullptr || dataSize < sizeof(GUID))
        return E_POINTER;
    *reinterpret_cast<GUID*>(data) = PIN_CATEGORY_CAPTURE;
    if (returned != nullptr)
        *returned = sizeof(GUID);
    return S_OK;
}

STDMETHODIMP OutputPin::QuerySupported(REFGUID set, DWORD id, DWORD* support) {
    if (support == nullptr)
        return E_POINTER;
    if (set == AMPROPSETID_Pin && id == AMPROPERTY_PIN_CATEGORY) {
        *support = KSPROPERTY_SUPPORT_GET;
        return S_OK;
    }
    return E_PROP_ID_UNSUPPORTED;
}

void OutputPin::StartDelivery() {
    if (thread_ != nullptr || allocator_ == nullptr || input_ == nullptr)
        return;
    allocator_->Commit();
    if (stopEvent_ == nullptr)
        stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ResetEvent(stopEvent_);
    frames_ = 0;
    thread_ = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
}

void OutputPin::StopDelivery() {
    if (thread_ == nullptr)
        return;
    if (stopEvent_ != nullptr)
        SetEvent(stopEvent_);
    WaitForSingleObject(thread_, 5000);
    CloseHandle(thread_);
    thread_ = nullptr;
    if (allocator_ != nullptr)
        allocator_->Decommit();
}

DWORD OutputPin::ThreadMain() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    FrameMap frames;
    uint8_t* bgra = new uint8_t[kWidth * kHeight * 4];
    LONG index = 0;
    bool logged = false;
    bool haveFrame = false;
    while (WaitForSingleObject(stopEvent_, 0) == WAIT_TIMEOUT) {
        if (filter_->State() == State_Running && allocator_ != nullptr && input_ != nullptr) {
            IMediaSample* sample = nullptr;
            HRESULT hr = allocator_->GetBuffer(&sample, nullptr, nullptr, AM_GBF_NOWAIT);
            if (SUCCEEDED(hr) && sample != nullptr) {
                BYTE* data = nullptr;
                if (SUCCEEDED(sample->GetPointer(&data)) && data != nullptr &&
                    sample->GetSize() >= kWidth * kHeight * 2) {
                    if (frames.Read(bgra, kWidth * kHeight * 4))
                        haveFrame = true;
                    else if (!haveFrame)
                        memset(bgra, 0, kWidth * kHeight * 4);
                    BgraToYuy2(bgra, data, kWidth, kHeight);
                    sample->SetActualDataLength(kWidth * kHeight * 2);
                    if (!logged) {
                        LogLine("frame");
                        logged = true;
                    }
                }
                REFERENCE_TIME start = index * kFrameDuration;
                REFERENCE_TIME stop = start + kFrameDuration;
                sample->SetTime(&start, &stop);
                sample->SetSyncPoint(TRUE);
                ++index;
                input_->Receive(sample);
                sample->Release();
            }
        }
        if (WaitForSingleObject(stopEvent_, 33) == WAIT_OBJECT_0)
            break;
    }
    delete[] bgra;
    CoUninitialize();
    return 0;
}

PinEnumerator::PinEnumerator(Filter* filter, int position) : filter_(filter), position_(position) { filter_->AddRef(); }
PinEnumerator::~PinEnumerator() { filter_->Release(); }

STDMETHODIMP PinEnumerator::QueryInterface(REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IEnumPins)
        *object = static_cast<IEnumPins*>(this);
    else {
        *object = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) PinEnumerator::AddRef() { return InterlockedIncrement(&refs_); }
STDMETHODIMP_(ULONG) PinEnumerator::Release() {
    ULONG refs = InterlockedDecrement(&refs_);
    if (refs == 0)
        delete this;
    return refs;
}

STDMETHODIMP PinEnumerator::Next(ULONG count, IPin** pins, ULONG* fetched) {
    if (pins == nullptr)
        return E_POINTER;
    ULONG produced = 0;
    if (position_ == 0 && count > 0) {
        pins[0] = filter_->Pin();
        pins[0]->AddRef();
        position_ = 1;
        produced = 1;
    }
    if (fetched != nullptr)
        *fetched = produced;
    else if (count != produced)
        return E_INVALIDARG;
    return produced == count ? S_OK : S_FALSE;
}

STDMETHODIMP PinEnumerator::Skip(ULONG count) {
    position_ += static_cast<int>(count);
    return position_ > 1 ? S_FALSE : S_OK;
}
STDMETHODIMP PinEnumerator::Reset() {
    position_ = 0;
    return S_OK;
}
STDMETHODIMP PinEnumerator::Clone(IEnumPins** enumerator) {
    if (enumerator == nullptr)
        return E_POINTER;
    *enumerator = new PinEnumerator(filter_, position_);
    return S_OK;
}

STDMETHODIMP MediaTypeEnumerator::QueryInterface(REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IEnumMediaTypes)
        *object = static_cast<IEnumMediaTypes*>(this);
    else {
        *object = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}
STDMETHODIMP_(ULONG) MediaTypeEnumerator::AddRef() { return InterlockedIncrement(&refs_); }
STDMETHODIMP_(ULONG) MediaTypeEnumerator::Release() {
    ULONG refs = InterlockedDecrement(&refs_);
    if (refs == 0)
        delete this;
    return refs;
}
STDMETHODIMP MediaTypeEnumerator::Next(ULONG count, AM_MEDIA_TYPE** types, ULONG* fetched) {
    if (types == nullptr)
        return E_POINTER;
    ULONG produced = 0;
    if (position_ == 0 && count > 0) {
        types[0] = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
        if (types[0] == nullptr)
            return E_OUTOFMEMORY;
        FillYuy2Type(types[0]);
        position_ = 1;
        produced = 1;
    }
    if (fetched != nullptr)
        *fetched = produced;
    return produced == count ? S_OK : S_FALSE;
}
STDMETHODIMP MediaTypeEnumerator::Skip(ULONG count) {
    position_ += static_cast<int>(count);
    return position_ > 1 ? S_FALSE : S_OK;
}
STDMETHODIMP MediaTypeEnumerator::Reset() {
    position_ = 0;
    return S_OK;
}
STDMETHODIMP MediaTypeEnumerator::Clone(IEnumMediaTypes** enumerator) {
    if (enumerator == nullptr)
        return E_POINTER;
    *enumerator = new MediaTypeEnumerator(position_);
    return S_OK;
}

STDMETHODIMP ClassFactory::QueryInterface(REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
        *object = static_cast<IClassFactory*>(this);
    else {
        *object = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}
STDMETHODIMP_(ULONG) ClassFactory::AddRef() { return InterlockedIncrement(&refs_); }
STDMETHODIMP_(ULONG) ClassFactory::Release() {
    ULONG refs = InterlockedDecrement(&refs_);
    if (refs == 0)
        delete this;
    return refs;
}
STDMETHODIMP ClassFactory::CreateInstance(IUnknown* outer, REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (outer != nullptr)
        return CLASS_E_NOAGGREGATION;
    Filter* filter = new Filter();
    HRESULT hr = filter->QueryInterface(riid, object);
    filter->Release();
    return hr;
}
STDMETHODIMP ClassFactory::LockServer(BOOL lock) {
    if (lock)
        InterlockedIncrement(&g_locks);
    else
        InterlockedDecrement(&g_locks);
    return S_OK;
}

HRESULT WriteInprocServer() {
    wchar_t path[MAX_PATH];
    if (GetModuleFileNameW(g_module, path, MAX_PATH) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    const wchar_t* clsid = L"Software\\Classes\\CLSID\\{B7E4C1A2-6D38-4F05-9A71-3C8E5D0B4F26}";
    HKEY key = nullptr;
    LONG error = RegCreateKeyExW(HKEY_LOCAL_MACHINE, clsid, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (error != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(error);
    const wchar_t* name = L"MeetCam";
    RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(name),
                   static_cast<DWORD>((lstrlenW(name) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);

    error = RegCreateKeyExW(HKEY_LOCAL_MACHINE,
                            L"Software\\Classes\\CLSID\\{B7E4C1A2-6D38-4F05-9A71-3C8E5D0B4F26}\\InprocServer32",
                            0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (error != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(error);
    RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path),
                   static_cast<DWORD>((lstrlenW(path) + 1) * sizeof(wchar_t)));
    const wchar_t* model = L"Both";
    RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model),
                   static_cast<DWORD>((lstrlenW(model) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return S_OK;
}

HRESULT RegisterCategory() {
    IFilterMapper2* mapper = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2,
                                  reinterpret_cast<void**>(&mapper));
    if (FAILED(hr))
        return hr;

    REGPINTYPES types[1] = {};
    types[0].clsMajorType = &MEDIATYPE_Video;
    types[0].clsMinorType = &MEDIASUBTYPE_YUY2;
    REGFILTERPINS2 pin{};
    pin.dwFlags = REG_PINFLAG_B_OUTPUT;
    pin.cInstances = 1;
    pin.nMediaTypes = 1;
    pin.lpMediaType = types;
    pin.nMediums = 0;
    pin.lpMedium = nullptr;
    pin.clsPinCategory = &PIN_CATEGORY_CAPTURE;

    REGFILTER2 reg{};
    reg.dwVersion = 2;
    reg.dwMerit = MERIT_DO_NOT_USE;
    reg.cPins2 = 1;
    reg.rgPins2 = &pin;
    hr = mapper->RegisterFilter(CLSID_MeetCamDShow, L"MeetCam", nullptr, &CLSID_VideoInputDeviceCategory, nullptr, &reg);
    mapper->Release();
    return hr;
}

}  // namespace

extern "C" {

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** object) {
    if (object == nullptr)
        return E_POINTER;
    if (clsid != CLSID_MeetCamDShow)
        return CLASS_E_CLASSNOTAVAILABLE;
    ClassFactory* factory = new ClassFactory();
    HRESULT hr = factory->QueryInterface(riid, object);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return (g_locks == 0 && g_objects == 0) ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer() {
    HRESULT hr = WriteInprocServer();
    if (FAILED(hr))
        return hr;
    hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool shouldUninit = SUCCEEDED(hr);
    hr = RegisterCategory();
    if (shouldUninit)
        CoUninitialize();
    return hr;
}

STDAPI DllUnregisterServer() {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool shouldUninit = SUCCEEDED(hr);
    IFilterMapper2* mapper = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2,
                          reinterpret_cast<void**>(&mapper));
    if (SUCCEEDED(hr)) {
        hr = mapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory, nullptr, CLSID_MeetCamDShow);
        mapper->Release();
    }
    if (shouldUninit)
        CoUninitialize();
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, L"Software\\Classes\\CLSID\\{B7E4C1A2-6D38-4F05-9A71-3C8E5D0B4F26}");
    return hr;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

}  // extern "C"
