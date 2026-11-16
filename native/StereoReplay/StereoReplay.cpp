// SPTVRStereoReplay.dll - the headset camera drawn once for both eyes (proof of concept; Source/Graphics/StereoReplay.cs, Ctrl+F9).
// Unity renders the LEFT eye only (Camera.stereoTargetEye = Left). Between the START and END plugin events every call Unity makes on
// the D3D11 immediate context is made again on a deferred context, with two swaps:
//  - constant buffers: the left eye's camera values (the patterns C# sends: view, projection, their products and inverses, camera
//    position) are replaced by the right eye's wherever they sit, so no shader's layout is assumed
//  - the left eye's swapchain image: our own texture (rightTexture)
// At END the recorded command list runs and our texture goes into the right eye's swapchain image (now if this frame's image is
// taken, else when it is handed back: XrFrameWatch's release hook). The main thread does one eye's work; the render thread and the
// GPU still do both.
// Hooks are vtable patches on the immediate context (calls on any other context pass straight through). Calls that can't be
// recorded (tiles, fences, context state swaps) drop that frame's replay. Dynamic vertex / index buffers are not copied: a replayed
// draw reads them as the left eye left them (fine for ring buffers written once a frame, wrong for one rewritten mid-pass).
#include <windows.h>
#include <d3d11_4.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <unordered_map>
#include <vector>

#define SR_EXPORT extern "C" __declspec(dllexport)

// ID3D11DeviceContext4's vtable slots (declaration order in d3d11_3.h)
enum Slot
{
    SVSSetConstantBuffers = 7, SPSSetShaderResources = 8, SPSSetShader = 9, SPSSetSamplers = 10, SVSSetShader = 11, SDrawIndexed = 12,
    SDraw = 13, SMap = 14, SUnmap = 15, SPSSetConstantBuffers = 16, SIASetInputLayout = 17, SIASetVertexBuffers = 18,
    SIASetIndexBuffer = 19, SDrawIndexedInstanced = 20, SDrawInstanced = 21, SGSSetConstantBuffers = 22, SGSSetShader = 23,
    SIASetPrimitiveTopology = 24, SVSSetShaderResources = 25, SVSSetSamplers = 26, SSetPredication = 30, SGSSetShaderResources = 31,
    SGSSetSamplers = 32, SOMSetRenderTargets = 33, SOMSetRenderTargetsAndUnorderedAccessViews = 34, SOMSetBlendState = 35,
    SOMSetDepthStencilState = 36, SSOSetTargets = 37, SDrawAuto = 38, SDrawIndexedInstancedIndirect = 39, SDrawInstancedIndirect = 40,
    SDispatch = 41, SDispatchIndirect = 42, SRSSetState = 43, SRSSetViewports = 44, SRSSetScissorRects = 45,
    SCopySubresourceRegion = 46, SCopyResource = 47, SUpdateSubresource = 48, SCopyStructureCount = 49, SClearRenderTargetView = 50,
    SClearUnorderedAccessViewUint = 51, SClearUnorderedAccessViewFloat = 52, SClearDepthStencilView = 53, SGenerateMips = 54,
    SSetResourceMinLOD = 55, SResolveSubresource = 57, SExecuteCommandList = 58, SHSSetShaderResources = 59, SHSSetShader = 60,
    SHSSetSamplers = 61, SHSSetConstantBuffers = 62, SDSSetShaderResources = 63, SDSSetShader = 64, SDSSetSamplers = 65,
    SDSSetConstantBuffers = 66, SCSSetShaderResources = 67, SCSSetUnorderedAccessViews = 68, SCSSetShader = 69, SCSSetSamplers = 70,
    SCSSetConstantBuffers = 71, SClearState = 110, SCopySubresourceRegion1 = 115, SUpdateSubresource1 = 116, SDiscardResource = 117,
    SDiscardView = 118, SVSSetConstantBuffers1 = 119, SHSSetConstantBuffers1 = 120, SDSSetConstantBuffers1 = 121,
    SGSSetConstantBuffers1 = 122, SPSSetConstantBuffers1 = 123, SCSSetConstantBuffers1 = 124, SSwapDeviceContextState = 131,
    SClearView = 132, SDiscardView1 = 133, SUpdateTileMappings = 134, SCopyTileMappings = 135, SCopyTiles = 136, SUpdateTiles = 137,
    SResizeTilePool = 138, STiledResourceBarrier = 139, SSignal = 147, SWait = 148, VtableSize = 149,
    SBegin = 27, SEnd = 28, SGetData = 29, SFlush = 111, SSetMarkerInt = 141, SBeginEventInt = 142, SEndEvent = 143
};

typedef ID3D11DeviceContext Ctx;

// Counters since the last SR_Stats (C# StereoReplay.Stat has the same order)
enum Stat
{
    StWindows, StDropped, StUnpaired, StNoImages, StAborted, StFinishFailed, StMirrored, StDraws, StCbMaps, StCbPatched, StCbUpdates,
    StCbNoOverwrite, StOtherDiscards, StViewsSwapped, StCopiesSwapped, StLeftUnswappable, StNestedLists, StPredication, StCopiedNow,
    StCopiedAtRelease, StWindowMs, StExecuteMs, StScanMs, StCaptureMs,
    // where the draws went: the immediate context outside the window, Unity's own deferred contexts (graphics jobs) in and out of it
    StImmediateDrawsOutside, StImmediateListsOutside, StWorkTakenBack, StRightTakenAtEnd, StSameImageTwice,
    StRehooked, StCount
};
static double stats[StCount];
// written from Unity's job threads too
static volatile LONG drawCounters[StCount];
static int lastAbortSlot;

static LARGE_INTEGER clockFrequency;
static double NowMs()
{
    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * 1000.0 / (double)clockFrequency.QuadPart;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Device, contexts, hooks

static ID3D11Device* device;
static ID3D11Device1* device1;
static Ctx* immediate;
static ID3D11DeviceContext1* immediate1;
static ID3D11DeviceContext1* deferred;
static void** vtable;
static void* original[VtableSize];
static bool hooked;
static bool sharedVtable;            // the deferred context runs through the same (hooked) vtable
static bool driverCommandLists;
static UINT uavSlots = 8;
static char info[2048];
static int infoLength;

static volatile LONG enabled;
static bool recording;
static bool aborted;

// The left eye's swapchain images (from XrFrameWatch) and our texture in their place
static ID3D11Resource* leftImages[8];
static int leftCount;
static ID3D11Texture2D* rightTexture;
static D3D11_TEXTURE2D_DESC rightDesc;
static bool pendingCopy;
static bool copyAtOnce = false;      // true: into the right image at END (anything drawing the right eye later overwrites it)

typedef int (*PFN_ViewImages)(int view, void** out, int max);
typedef void* (*PFN_ViewAcquiredImage)(int view);
typedef void (*PFN_SetReleaseHook)(void* hook);
static PFN_ViewImages viewImages;
static PFN_ViewAcquiredImage viewAcquiredImage;

static void Info(const char* format, ...)
{
    if (infoLength >= (int)sizeof(info) - 1)
        return;
    va_list arguments;
    va_start(arguments, format);
    int written = vsnprintf(info + infoLength, sizeof(info) - infoLength, format, arguments);
    va_end(arguments);
    if (written > 0)
        infoLength += written < (int)sizeof(info) - infoLength ? written : (int)sizeof(info) - infoLength - 1;
}

// The runtime swaps the work entries (draws, dispatches, copies, clears, uploads) between its own variants as state changes, many
// times a frame: inside a window they are taken back after every call on the immediate context, the runtime's latest variant being
// what they call on
static const int workSlots[] = { SDrawIndexed, SDraw, SDrawIndexedInstanced, SDrawInstanced, SDrawAuto, SDrawIndexedInstancedIndirect,
    SDrawInstancedIndirect, SDispatch, SDispatchIndirect, SCopySubresourceRegion, SCopyResource, SUpdateSubresource, SCopyStructureCount,
    SClearRenderTargetView, SClearUnorderedAccessViewUint, SClearUnorderedAccessViewFloat, SClearDepthStencilView, SGenerateMips,
    SResolveSubresource, SCopySubresourceRegion1, SUpdateSubresource1, SClearView, SCopyTiles, SUpdateTiles };
static void* workHooks[VtableSize];
static uintptr_t runtimeStart, runtimeEnd;     // d3d11.dll's image: a variant from anywhere else is not taken as the original

static inline void KeepWorkHooks()
{
    for (int slot : workSlots)
    {
        void* now = vtable[slot];
        if (now == workHooks[slot])
            continue;
        if ((uintptr_t)now >= runtimeStart && (uintptr_t)now < runtimeEnd)
            original[slot] = now;
        vtable[slot] = workHooks[slot];
        stats[StWorkTakenBack] += 1;
    }
}

static inline bool Mirroring(Ctx* self)
{
    if (self != immediate || !recording)
        return false;
    KeepWorkHooks();
    return !aborted;
}

static inline void CountImmediateDraw(Ctx* self)
{
    if (self == immediate && !recording)
        InterlockedIncrement(&drawCounters[StImmediateDrawsOutside]);
}

static void Abort(int slot)
{
    if (recording && !aborted)
    {
        aborted = true;
        lastAbortSlot = slot;
        stats[StAborted] += 1;
    }
}

//------------------------------------------------------------------------------------------------------------------------------------
// Camera value patterns (C# fills a slot a frame, the START event names it)

struct Pattern { int length; float tolerance; float left[16]; float right[16]; };
static const int kFrameSlots = 8;
static const int kMaxPatterns = 64;
// endEvent: the END that closes this frame's window (the last camera drawing the eye this frame)
struct FrameSlot { int count; int endEvent; Pattern patterns[kMaxPatterns]; };
static int expectedEnd;
static FrameSlot frameSlots[kFrameSlots];
static Pattern active[kMaxPatterns];
static int activeCount;
static double patternHits[kMaxPatterns];

static inline bool Near(float value, float wanted, float tolerance)
{
    float scale = fabsf(wanted);
    return fabsf(value - wanted) <= tolerance * (scale > 1.0f ? scale : 1.0f);
}

// Every 16-byte register of the data that starts a left-eye pattern gets the right eye's value
static int PatchConstants(float* data, size_t bytes)
{
    double start = NowMs();
    size_t floats = bytes / 4;
    int patched = 0;
    for (size_t at = 0; at + 3 <= floats; at += 4)
    {
        for (int index = 0; index < activeCount; index++)
        {
            const Pattern& pattern = active[index];
            if (at + pattern.length > floats || !Near(data[at], pattern.left[0], pattern.tolerance))
                continue;
            bool match = true;
            for (int element = 1; element < pattern.length && match; element++)
                match = Near(data[at + element], pattern.left[element], pattern.tolerance);
            if (!match)
                continue;
            memcpy(data + at, pattern.right, pattern.length * sizeof(float));
            patternHits[index] += 1;
            patched++;
            at += ((pattern.length + 3) / 4 - 1) * 4;
            break;
        }
    }
    stats[StScanMs] += NowMs() - start;
    return patched;
}

//------------------------------------------------------------------------------------------------------------------------------------
// The left image swapped for ours

static inline bool IsLeft(ID3D11Resource* resource)
{
    for (int index = 0; index < leftCount; index++)
    {
        if (leftImages[index] == resource)
            return true;
    }
    return false;
}

static inline ID3D11Resource* SwapResource(ID3D11Resource* resource)
{
    if (resource && rightTexture && IsLeft(resource))
    {
        stats[StCopiesSwapped] += 1;
        return rightTexture;
    }
    return resource;
}

enum ViewKind { KindRtv, KindSrv, KindUav, KindDsv };

// Per window: view -> what the replay binds (itself, or ours on rightTexture)
static const int kCacheSize = 4096;
struct CacheEntry { void* key; void* value; };
static CacheEntry viewCache[kCacheSize];
static std::unordered_map<void*, ID3D11View*> ourViews;      // a left-image view -> its twin on rightTexture

static inline size_t Hash(void* pointer) { return (((uintptr_t)pointer >> 4) * 2654435761u) & (kCacheSize - 1); }

static ID3D11View* OurView(ID3D11View* view, ViewKind kind)
{
    auto found = ourViews.find(view);
    if (found != ourViews.end())
        return found->second;
    ID3D11View* made = nullptr;
    HRESULT result = E_FAIL;
    if (kind == KindRtv)
    {
        D3D11_RENDER_TARGET_VIEW_DESC description;
        ((ID3D11RenderTargetView*)view)->GetDesc(&description);
        result = device->CreateRenderTargetView(rightTexture, &description, (ID3D11RenderTargetView**)&made);
    }
    else if (kind == KindSrv)
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC description;
        ((ID3D11ShaderResourceView*)view)->GetDesc(&description);
        result = device->CreateShaderResourceView(rightTexture, &description, (ID3D11ShaderResourceView**)&made);
    }
    else if (kind == KindUav)
    {
        D3D11_UNORDERED_ACCESS_VIEW_DESC description;
        ((ID3D11UnorderedAccessView*)view)->GetDesc(&description);
        result = device->CreateUnorderedAccessView(rightTexture, &description, (ID3D11UnorderedAccessView**)&made);
    }
    if (FAILED(result))
        made = nullptr;
    ourViews[view] = made;
    return made;
}

// What the replay binds for this view: the view itself, or its twin on our texture (null if that couldn't be made)
static void* SwapView(void* view, ViewKind kind)
{
    if (!view || leftCount == 0 || !rightTexture)
        return view;
    CacheEntry& entry = viewCache[Hash(view)];
    if (entry.key == view)
        return entry.value;
    ID3D11Resource* resource = nullptr;
    ((ID3D11View*)view)->GetResource(&resource);
    bool left = resource && IsLeft(resource);
    if (resource)
        resource->Release();
    void* value = view;
    if (left)
    {
        value = kind == KindDsv ? nullptr : OurView((ID3D11View*)view, kind);
        stats[value ? StViewsSwapped : StLeftUnswappable] += 1;
    }
    entry.key = view;
    entry.value = value;
    return value;
}

template <typename T>
static T* const* SwapViews(T* const* views, UINT count, T** scratch, UINT capacity, ViewKind kind)
{
    if (!views || count == 0 || count > capacity || leftCount == 0)
        return views;
    bool changed = false;
    for (UINT index = 0; index < count; index++)
    {
        scratch[index] = (T*)SwapView(views[index], kind);
        changed |= scratch[index] != views[index];
    }
    return changed ? scratch : views;
}

// A view of unknown kind (ClearView / DiscardView): its replacement, or itself; *skip when it is the left image's and has no twin
static ID3D11View* SwapAnyView(ID3D11View* view, bool* skip)
{
    *skip = false;
    if (!view || leftCount == 0)
        return view;
    void* asKind = nullptr;
    ViewKind kinds[4] = { KindRtv, KindSrv, KindUav, KindDsv };
    const IID* iids[4] = { &__uuidof(ID3D11RenderTargetView), &__uuidof(ID3D11ShaderResourceView), &__uuidof(ID3D11UnorderedAccessView),
        &__uuidof(ID3D11DepthStencilView) };
    for (int index = 0; index < 4; index++)
    {
        if (SUCCEEDED(view->QueryInterface(*iids[index], &asKind)) && asKind)
        {
            ((IUnknown*)asKind)->Release();
            void* swapped = SwapView(asKind, kinds[index]);
            if (!swapped)
                *skip = true;
            return (ID3D11View*)swapped;
        }
    }
    return view;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Constant buffers: the replay gets each upload with the right eye's values; at END the left eye's last upload goes back on top, so
// what Unity thinks a buffer holds after the replay is what it holds

struct PendingMap { ID3D11Resource* resource; UINT subresource; void* driverPointer; UINT size; };
static const int kPending = 16;
static PendingMap pendingMaps[kPending];
static std::vector<unsigned char> scratch[kPending];
static std::vector<unsigned char> patchScratch;

struct LastUpload { std::vector<unsigned char> data; bool dynamic; bool touched; };
static std::unordered_map<ID3D11Resource*, LastUpload> lastUploads;
static std::vector<ID3D11Resource*> touched;

static bool ConstantBufferSize(ID3D11Resource* resource, UINT* size, bool* dynamic)
{
    D3D11_RESOURCE_DIMENSION dimension;
    resource->GetType(&dimension);
    if (dimension != D3D11_RESOURCE_DIMENSION_BUFFER)
        return false;
    D3D11_BUFFER_DESC description;
    ((ID3D11Buffer*)resource)->GetDesc(&description);
    if (!(description.BindFlags & D3D11_BIND_CONSTANT_BUFFER))
        return false;
    *size = description.ByteWidth;
    *dynamic = description.Usage == D3D11_USAGE_DYNAMIC;
    return true;
}

static void RememberUpload(ID3D11Resource* resource, const void* data, UINT size, bool dynamic)
{
    LastUpload& upload = lastUploads[resource];
    upload.data.assign((const unsigned char*)data, (const unsigned char*)data + size);
    upload.dynamic = dynamic;
    if (!upload.touched)
    {
        upload.touched = true;
        resource->AddRef();
        touched.push_back(resource);
    }
}

static void RestoreUploads()
{
    for (ID3D11Resource* resource : touched)
    {
        LastUpload& upload = lastUploads[resource];
        if (!aborted && !upload.data.empty())
        {
            if (upload.dynamic)
            {
                D3D11_MAPPED_SUBRESOURCE mapped;
                if (SUCCEEDED(deferred->Map(resource, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
                {
                    memcpy(mapped.pData, upload.data.data(), upload.data.size());
                    deferred->Unmap(resource, 0);
                }
            }
            else
                deferred->UpdateSubresource(resource, 0, nullptr, upload.data.data(), 0, 0);
        }
        upload.touched = false;
        resource->Release();
    }
    touched.clear();
}

//------------------------------------------------------------------------------------------------------------------------------------
// Hooks. Each calls the original, then (inside a window, on the immediate context) the same on the deferred context.

#define ORIGINAL(slot, signature) ((signature)original[slot])

typedef void (STDMETHODCALLTYPE* PfnSetBuffers)(Ctx*, UINT, UINT, ID3D11Buffer* const*);
typedef void (STDMETHODCALLTYPE* PfnSetSamplers)(Ctx*, UINT, UINT, ID3D11SamplerState* const*);
typedef void (STDMETHODCALLTYPE* PfnSetViews)(Ctx*, UINT, UINT, ID3D11ShaderResourceView* const*);
typedef void (STDMETHODCALLTYPE* PfnSetBuffers1)(Ctx*, UINT, UINT, ID3D11Buffer* const*, const UINT*, const UINT*);

#define HOOK_BUFFERS(Name) \
    static void STDMETHODCALLTYPE Our##Name(Ctx* self, UINT start, UINT count, ID3D11Buffer* const* buffers) \
    { ORIGINAL(S##Name, PfnSetBuffers)(self, start, count, buffers); \
      if (Mirroring(self)) { stats[StMirrored] += 1; deferred->Name(start, count, buffers); } }
#define HOOK_SAMPLERS(Name) \
    static void STDMETHODCALLTYPE Our##Name(Ctx* self, UINT start, UINT count, ID3D11SamplerState* const* samplers) \
    { ORIGINAL(S##Name, PfnSetSamplers)(self, start, count, samplers); \
      if (Mirroring(self)) { stats[StMirrored] += 1; deferred->Name(start, count, samplers); } }
#define HOOK_VIEWS(Name) \
    static void STDMETHODCALLTYPE Our##Name(Ctx* self, UINT start, UINT count, ID3D11ShaderResourceView* const* views) \
    { ORIGINAL(S##Name, PfnSetViews)(self, start, count, views); \
      if (Mirroring(self)) { stats[StMirrored] += 1; ID3D11ShaderResourceView* swapped[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT]; \
        deferred->Name(start, count, SwapViews(views, count, swapped, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, KindSrv)); } }
#define HOOK_SHADER(Name, Type) \
    static void STDMETHODCALLTYPE Our##Name(Ctx* self, Type* shader, ID3D11ClassInstance* const* instances, UINT instanceCount) \
    { ORIGINAL(S##Name, void (STDMETHODCALLTYPE*)(Ctx*, Type*, ID3D11ClassInstance* const*, UINT))(self, shader, instances, instanceCount); \
      if (Mirroring(self)) { stats[StMirrored] += 1; deferred->Name(shader, instances, instanceCount); } }
#define HOOK_BUFFERS1(Name) \
    static void STDMETHODCALLTYPE Our##Name(Ctx* self, UINT start, UINT count, ID3D11Buffer* const* buffers, const UINT* first, const UINT* number) \
    { ORIGINAL(S##Name, PfnSetBuffers1)(self, start, count, buffers, first, number); \
      if (Mirroring(self)) { stats[StMirrored] += 1; deferred->Name(start, count, buffers, first, number); } }

HOOK_BUFFERS(VSSetConstantBuffers) HOOK_BUFFERS(PSSetConstantBuffers) HOOK_BUFFERS(GSSetConstantBuffers)
HOOK_BUFFERS(HSSetConstantBuffers) HOOK_BUFFERS(DSSetConstantBuffers) HOOK_BUFFERS(CSSetConstantBuffers)
HOOK_SAMPLERS(VSSetSamplers) HOOK_SAMPLERS(PSSetSamplers) HOOK_SAMPLERS(GSSetSamplers)
HOOK_SAMPLERS(HSSetSamplers) HOOK_SAMPLERS(DSSetSamplers) HOOK_SAMPLERS(CSSetSamplers)
HOOK_VIEWS(VSSetShaderResources) HOOK_VIEWS(PSSetShaderResources) HOOK_VIEWS(GSSetShaderResources)
HOOK_VIEWS(HSSetShaderResources) HOOK_VIEWS(DSSetShaderResources) HOOK_VIEWS(CSSetShaderResources)
HOOK_SHADER(VSSetShader, ID3D11VertexShader) HOOK_SHADER(PSSetShader, ID3D11PixelShader) HOOK_SHADER(GSSetShader, ID3D11GeometryShader)
HOOK_SHADER(HSSetShader, ID3D11HullShader) HOOK_SHADER(DSSetShader, ID3D11DomainShader) HOOK_SHADER(CSSetShader, ID3D11ComputeShader)
HOOK_BUFFERS1(VSSetConstantBuffers1) HOOK_BUFFERS1(PSSetConstantBuffers1) HOOK_BUFFERS1(GSSetConstantBuffers1)
HOOK_BUFFERS1(HSSetConstantBuffers1) HOOK_BUFFERS1(DSSetConstantBuffers1) HOOK_BUFFERS1(CSSetConstantBuffers1)

// Draws and dispatches
static void STDMETHODCALLTYPE OurDraw(Ctx* self, UINT vertices, UINT first)
{
    CountImmediateDraw(self);
    ORIGINAL(SDraw, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT))(self, vertices, first);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->Draw(vertices, first); }
}
static void STDMETHODCALLTYPE OurDrawIndexed(Ctx* self, UINT indices, UINT first, INT baseVertex)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawIndexed, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, INT))(self, indices, first, baseVertex);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawIndexed(indices, first, baseVertex); }
}
static void STDMETHODCALLTYPE OurDrawInstanced(Ctx* self, UINT vertices, UINT instances, UINT first, UINT firstInstance)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawInstanced, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, UINT, UINT))(self, vertices, instances, first, firstInstance);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawInstanced(vertices, instances, first, firstInstance); }
}
static void STDMETHODCALLTYPE OurDrawIndexedInstanced(Ctx* self, UINT indices, UINT instances, UINT first, INT baseVertex, UINT firstInstance)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawIndexedInstanced, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, UINT, INT, UINT))(self, indices, instances, first, baseVertex, firstInstance);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawIndexedInstanced(indices, instances, first, baseVertex, firstInstance); }
}
static void STDMETHODCALLTYPE OurDrawAuto(Ctx* self)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawAuto, void (STDMETHODCALLTYPE*)(Ctx*))(self);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawAuto(); }
}
static void STDMETHODCALLTYPE OurDrawIndexedInstancedIndirect(Ctx* self, ID3D11Buffer* arguments, UINT offset)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawIndexedInstancedIndirect, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, UINT))(self, arguments, offset);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawIndexedInstancedIndirect(arguments, offset); }
}
static void STDMETHODCALLTYPE OurDrawInstancedIndirect(Ctx* self, ID3D11Buffer* arguments, UINT offset)
{
    CountImmediateDraw(self);
    ORIGINAL(SDrawInstancedIndirect, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, UINT))(self, arguments, offset);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DrawInstancedIndirect(arguments, offset); }
}
static void STDMETHODCALLTYPE OurDispatch(Ctx* self, UINT x, UINT y, UINT z)
{
    CountImmediateDraw(self);
    ORIGINAL(SDispatch, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, UINT))(self, x, y, z);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->Dispatch(x, y, z); }
}
static void STDMETHODCALLTYPE OurDispatchIndirect(Ctx* self, ID3D11Buffer* arguments, UINT offset)
{
    CountImmediateDraw(self);
    ORIGINAL(SDispatchIndirect, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, UINT))(self, arguments, offset);
    if (Mirroring(self)) { stats[StDraws] += 1; deferred->DispatchIndirect(arguments, offset); }
}

// Input assembler, rasterizer, stream out
static void STDMETHODCALLTYPE OurIASetInputLayout(Ctx* self, ID3D11InputLayout* layout)
{
    ORIGINAL(SIASetInputLayout, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11InputLayout*))(self, layout);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->IASetInputLayout(layout); }
}
static void STDMETHODCALLTYPE OurIASetVertexBuffers(Ctx* self, UINT start, UINT count, ID3D11Buffer* const* buffers, const UINT* strides, const UINT* offsets)
{
    ORIGINAL(SIASetVertexBuffers, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, ID3D11Buffer* const*, const UINT*, const UINT*))(self, start, count, buffers, strides, offsets);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->IASetVertexBuffers(start, count, buffers, strides, offsets); }
}
static void STDMETHODCALLTYPE OurIASetIndexBuffer(Ctx* self, ID3D11Buffer* buffer, DXGI_FORMAT format, UINT offset)
{
    ORIGINAL(SIASetIndexBuffer, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, DXGI_FORMAT, UINT))(self, buffer, format, offset);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->IASetIndexBuffer(buffer, format, offset); }
}
static void STDMETHODCALLTYPE OurIASetPrimitiveTopology(Ctx* self, D3D11_PRIMITIVE_TOPOLOGY topology)
{
    ORIGINAL(SIASetPrimitiveTopology, void (STDMETHODCALLTYPE*)(Ctx*, D3D11_PRIMITIVE_TOPOLOGY))(self, topology);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->IASetPrimitiveTopology(topology); }
}
static void STDMETHODCALLTYPE OurRSSetState(Ctx* self, ID3D11RasterizerState* state)
{
    ORIGINAL(SRSSetState, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11RasterizerState*))(self, state);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->RSSetState(state); }
}
static void STDMETHODCALLTYPE OurRSSetViewports(Ctx* self, UINT count, const D3D11_VIEWPORT* viewports)
{
    ORIGINAL(SRSSetViewports, void (STDMETHODCALLTYPE*)(Ctx*, UINT, const D3D11_VIEWPORT*))(self, count, viewports);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->RSSetViewports(count, viewports); }
}
static void STDMETHODCALLTYPE OurRSSetScissorRects(Ctx* self, UINT count, const D3D11_RECT* rects)
{
    ORIGINAL(SRSSetScissorRects, void (STDMETHODCALLTYPE*)(Ctx*, UINT, const D3D11_RECT*))(self, count, rects);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->RSSetScissorRects(count, rects); }
}
static void STDMETHODCALLTYPE OurSOSetTargets(Ctx* self, UINT count, ID3D11Buffer* const* buffers, const UINT* offsets)
{
    ORIGINAL(SSOSetTargets, void (STDMETHODCALLTYPE*)(Ctx*, UINT, ID3D11Buffer* const*, const UINT*))(self, count, buffers, offsets);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->SOSetTargets(count, buffers, offsets); }
}

// Output merger
static void STDMETHODCALLTYPE OurOMSetRenderTargets(Ctx* self, UINT count, ID3D11RenderTargetView* const* views, ID3D11DepthStencilView* depth)
{
    ORIGINAL(SOMSetRenderTargets, void (STDMETHODCALLTYPE*)(Ctx*, UINT, ID3D11RenderTargetView* const*, ID3D11DepthStencilView*))(self, count, views, depth);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11RenderTargetView* swapped[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT];
        deferred->OMSetRenderTargets(count, SwapViews(views, count, swapped, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, KindRtv),
            (ID3D11DepthStencilView*)SwapView(depth, KindDsv));
    }
}
static void STDMETHODCALLTYPE OurOMSetRenderTargetsAndUnorderedAccessViews(Ctx* self, UINT rtvCount, ID3D11RenderTargetView* const* rtvs,
    ID3D11DepthStencilView* depth, UINT uavStart, UINT uavCount, ID3D11UnorderedAccessView* const* uavs, const UINT* initialCounts)
{
    ORIGINAL(SOMSetRenderTargetsAndUnorderedAccessViews, void (STDMETHODCALLTYPE*)(Ctx*, UINT, ID3D11RenderTargetView* const*,
        ID3D11DepthStencilView*, UINT, UINT, ID3D11UnorderedAccessView* const*, const UINT*))(self, rtvCount, rtvs, depth, uavStart, uavCount, uavs, initialCounts);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11RenderTargetView* swappedRtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT];
        ID3D11UnorderedAccessView* swappedUavs[D3D11_1_UAV_SLOT_COUNT];
        bool keepRtvs = rtvCount == D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL;
        bool keepUavs = uavCount == D3D11_KEEP_UNORDERED_ACCESS_VIEWS;
        deferred->OMSetRenderTargetsAndUnorderedAccessViews(rtvCount,
            keepRtvs ? rtvs : SwapViews(rtvs, rtvCount, swappedRtvs, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, KindRtv),
            keepRtvs ? depth : (ID3D11DepthStencilView*)SwapView(depth, KindDsv), uavStart, uavCount,
            keepUavs ? uavs : SwapViews(uavs, uavCount, swappedUavs, D3D11_1_UAV_SLOT_COUNT, KindUav), initialCounts);
    }
}
static void STDMETHODCALLTYPE OurOMSetBlendState(Ctx* self, ID3D11BlendState* state, const FLOAT factor[4], UINT mask)
{
    ORIGINAL(SOMSetBlendState, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11BlendState*, const FLOAT*, UINT))(self, state, factor, mask);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->OMSetBlendState(state, factor, mask); }
}
static void STDMETHODCALLTYPE OurOMSetDepthStencilState(Ctx* self, ID3D11DepthStencilState* state, UINT reference)
{
    ORIGINAL(SOMSetDepthStencilState, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11DepthStencilState*, UINT))(self, state, reference);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->OMSetDepthStencilState(state, reference); }
}
static void STDMETHODCALLTYPE OurCSSetUnorderedAccessViews(Ctx* self, UINT start, UINT count, ID3D11UnorderedAccessView* const* views, const UINT* initialCounts)
{
    ORIGINAL(SCSSetUnorderedAccessViews, void (STDMETHODCALLTYPE*)(Ctx*, UINT, UINT, ID3D11UnorderedAccessView* const*, const UINT*))(self, start, count, views, initialCounts);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11UnorderedAccessView* swapped[D3D11_1_UAV_SLOT_COUNT];
        deferred->CSSetUnorderedAccessViews(start, count, SwapViews(views, count, swapped, D3D11_1_UAV_SLOT_COUNT, KindUav), initialCounts);
    }
}

// Clears, copies, resolves
static void STDMETHODCALLTYPE OurClearRenderTargetView(Ctx* self, ID3D11RenderTargetView* view, const FLOAT color[4])
{
    ORIGINAL(SClearRenderTargetView, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11RenderTargetView*, const FLOAT*))(self, view, color);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11RenderTargetView* swapped = (ID3D11RenderTargetView*)SwapView(view, KindRtv);
        if (swapped)
            deferred->ClearRenderTargetView(swapped, color);
    }
}
static void STDMETHODCALLTYPE OurClearUnorderedAccessViewUint(Ctx* self, ID3D11UnorderedAccessView* view, const UINT values[4])
{
    ORIGINAL(SClearUnorderedAccessViewUint, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11UnorderedAccessView*, const UINT*))(self, view, values);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11UnorderedAccessView* swapped = (ID3D11UnorderedAccessView*)SwapView(view, KindUav);
        if (swapped)
            deferred->ClearUnorderedAccessViewUint(swapped, values);
    }
}
static void STDMETHODCALLTYPE OurClearUnorderedAccessViewFloat(Ctx* self, ID3D11UnorderedAccessView* view, const FLOAT values[4])
{
    ORIGINAL(SClearUnorderedAccessViewFloat, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11UnorderedAccessView*, const FLOAT*))(self, view, values);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11UnorderedAccessView* swapped = (ID3D11UnorderedAccessView*)SwapView(view, KindUav);
        if (swapped)
            deferred->ClearUnorderedAccessViewFloat(swapped, values);
    }
}
static void STDMETHODCALLTYPE OurClearDepthStencilView(Ctx* self, ID3D11DepthStencilView* view, UINT flags, FLOAT depth, UINT8 stencil)
{
    ORIGINAL(SClearDepthStencilView, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11DepthStencilView*, UINT, FLOAT, UINT8))(self, view, flags, depth, stencil);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->ClearDepthStencilView(view, flags, depth, stencil); }
}
static void STDMETHODCALLTYPE OurCopySubresourceRegion(Ctx* self, ID3D11Resource* destination, UINT destinationSub, UINT x, UINT y, UINT z,
    ID3D11Resource* source, UINT sourceSub, const D3D11_BOX* box)
{
    ORIGINAL(SCopySubresourceRegion, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, UINT, UINT, UINT, ID3D11Resource*, UINT, const D3D11_BOX*))(
        self, destination, destinationSub, x, y, z, source, sourceSub, box);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->CopySubresourceRegion(SwapResource(destination), destinationSub, x, y, z, SwapResource(source), sourceSub, box); }
}
static void STDMETHODCALLTYPE OurCopySubresourceRegion1(Ctx* self, ID3D11Resource* destination, UINT destinationSub, UINT x, UINT y, UINT z,
    ID3D11Resource* source, UINT sourceSub, const D3D11_BOX* box, UINT flags)
{
    ORIGINAL(SCopySubresourceRegion1, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, UINT, UINT, UINT, ID3D11Resource*, UINT, const D3D11_BOX*, UINT))(
        self, destination, destinationSub, x, y, z, source, sourceSub, box, flags);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->CopySubresourceRegion1(SwapResource(destination), destinationSub, x, y, z, SwapResource(source), sourceSub, box, flags); }
}
static void STDMETHODCALLTYPE OurCopyResource(Ctx* self, ID3D11Resource* destination, ID3D11Resource* source)
{
    ORIGINAL(SCopyResource, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, ID3D11Resource*))(self, destination, source);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->CopyResource(SwapResource(destination), SwapResource(source)); }
}
static void STDMETHODCALLTYPE OurCopyStructureCount(Ctx* self, ID3D11Buffer* destination, UINT offset, ID3D11UnorderedAccessView* source)
{
    ORIGINAL(SCopyStructureCount, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, UINT, ID3D11UnorderedAccessView*))(self, destination, offset, source);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->CopyStructureCount(destination, offset, (ID3D11UnorderedAccessView*)SwapView(source, KindUav)); }
}
static void STDMETHODCALLTYPE OurGenerateMips(Ctx* self, ID3D11ShaderResourceView* view)
{
    ORIGINAL(SGenerateMips, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11ShaderResourceView*))(self, view);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        ID3D11ShaderResourceView* swapped = (ID3D11ShaderResourceView*)SwapView(view, KindSrv);
        if (swapped)
            deferred->GenerateMips(swapped);
    }
}
static void STDMETHODCALLTYPE OurSetResourceMinLOD(Ctx* self, ID3D11Resource* resource, FLOAT lod)
{
    ORIGINAL(SSetResourceMinLOD, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, FLOAT))(self, resource, lod);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->SetResourceMinLOD(SwapResource(resource), lod); }
}
static void STDMETHODCALLTYPE OurResolveSubresource(Ctx* self, ID3D11Resource* destination, UINT destinationSub, ID3D11Resource* source, UINT sourceSub, DXGI_FORMAT format)
{
    ORIGINAL(SResolveSubresource, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, ID3D11Resource*, UINT, DXGI_FORMAT))(self, destination, destinationSub, source, sourceSub, format);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->ResolveSubresource(SwapResource(destination), destinationSub, SwapResource(source), sourceSub, format); }
}
static void STDMETHODCALLTYPE OurExecuteCommandList(Ctx* self, ID3D11CommandList* list, BOOL restore)
{
    ORIGINAL(SExecuteCommandList, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11CommandList*, BOOL))(self, list, restore);
    if (self == immediate && !recording)
        InterlockedIncrement(&drawCounters[StImmediateListsOutside]);
    if (Mirroring(self)) { stats[StNestedLists] += 1; deferred->ExecuteCommandList(list, restore); }
}
static void STDMETHODCALLTYPE OurClearState(Ctx* self)
{
    ORIGINAL(SClearState, void (STDMETHODCALLTYPE*)(Ctx*))(self);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->ClearState(); }
}
static void STDMETHODCALLTYPE OurDiscardResource(Ctx* self, ID3D11Resource* resource)
{
    ORIGINAL(SDiscardResource, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*))(self, resource);
    if (Mirroring(self)) { stats[StMirrored] += 1; deferred->DiscardResource(SwapResource(resource)); }
}
static void STDMETHODCALLTYPE OurDiscardView(Ctx* self, ID3D11View* view)
{
    ORIGINAL(SDiscardView, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11View*))(self, view);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        bool skip;
        ID3D11View* swapped = SwapAnyView(view, &skip);
        if (!skip && swapped)
            deferred->DiscardView(swapped);
    }
}
static void STDMETHODCALLTYPE OurDiscardView1(Ctx* self, ID3D11View* view, const D3D11_RECT* rects, UINT count)
{
    ORIGINAL(SDiscardView1, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11View*, const D3D11_RECT*, UINT))(self, view, rects, count);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        bool skip;
        ID3D11View* swapped = SwapAnyView(view, &skip);
        if (!skip && swapped)
            deferred->DiscardView1(swapped, rects, count);
    }
}
static void STDMETHODCALLTYPE OurClearView(Ctx* self, ID3D11View* view, const FLOAT color[4], const D3D11_RECT* rects, UINT count)
{
    ORIGINAL(SClearView, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11View*, const FLOAT*, const D3D11_RECT*, UINT))(self, view, color, rects, count);
    if (Mirroring(self))
    {
        stats[StMirrored] += 1;
        bool skip;
        ID3D11View* swapped = SwapAnyView(view, &skip);
        if (!skip && swapped)
            deferred->ClearView(swapped, color, rects, count);
    }
}

// Constant buffer uploads: Unity writes a mapped constant buffer into our cached copy (reading back write-combined memory would be
// slow), which goes to the driver at Unmap and, with the right eye's values, into the replay
static HRESULT STDMETHODCALLTYPE OurMap(Ctx* self, ID3D11Resource* resource, UINT subresource, D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* mapped)
{
    HRESULT result = ORIGINAL(SMap, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*))(
        self, resource, subresource, type, flags, mapped);
    if (!Mirroring(self) || FAILED(result) || !mapped || !resource)
        return result;
    UINT size;
    bool dynamic;
    bool constant = ConstantBufferSize(resource, &size, &dynamic);
    if (type == D3D11_MAP_WRITE_DISCARD && constant)
    {
        for (int index = 0; index < kPending; index++)
        {
            if (pendingMaps[index].resource)
                continue;
            if (scratch[index].size() < size)
                scratch[index].resize(size);
            pendingMaps[index] = { resource, subresource, mapped->pData, size };
            mapped->pData = scratch[index].data();
            stats[StCbMaps] += 1;
            return result;
        }
        Abort(SMap);   // more open maps than we track: this frame can't be replayed
    }
    else if (type == D3D11_MAP_WRITE_NO_OVERWRITE && constant)
        stats[StCbNoOverwrite] += 1;
    else if (type == D3D11_MAP_WRITE_DISCARD)
        stats[StOtherDiscards] += 1;
    return result;
}

static void STDMETHODCALLTYPE OurUnmap(Ctx* self, ID3D11Resource* resource, UINT subresource)
{
    int found = -1;
    if (self == immediate)
    {
        for (int index = 0; index < kPending; index++)
        {
            if (pendingMaps[index].resource == resource && pendingMaps[index].subresource == subresource)
                found = index;
        }
        if (found >= 0)
            memcpy(pendingMaps[found].driverPointer, scratch[found].data(), pendingMaps[found].size);
    }
    ORIGINAL(SUnmap, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT))(self, resource, subresource);
    if (found < 0)
    {
        if (self == immediate && recording)
            KeepWorkHooks();
        return;
    }
    PendingMap map = pendingMaps[found];
    pendingMaps[found].resource = nullptr;
    if (!Mirroring(self))
        return;
    unsigned char* data = scratch[found].data();
    RememberUpload(resource, data, map.size, true);
    if (PatchConstants((float*)data, map.size) > 0)
        stats[StCbPatched] += 1;
    D3D11_MAPPED_SUBRESOURCE replayMapped;
    if (SUCCEEDED(deferred->Map(resource, subresource, D3D11_MAP_WRITE_DISCARD, 0, &replayMapped)))
    {
        memcpy(replayMapped.pData, data, map.size);
        deferred->Unmap(resource, subresource);
    }
    else
        Abort(SUnmap);
}

// UpdateSubresource: constant buffers are replayed with the right eye's values; other resources keep what the left eye's pass wrote
// (the data stays in them), so nothing is redone
static void UpdateConstants(ID3D11Resource* resource, UINT subresource, const D3D11_BOX* box, const void* data, UINT rowPitch, UINT depthPitch,
    UINT copyFlags, bool withFlags)
{
    UINT size;
    bool dynamic;
    if (!data || !ConstantBufferSize(resource, &size, &dynamic))
        return;
    UINT bytes = box ? box->right - box->left : size;
    if (bytes == 0 || bytes > size)
        return;
    stats[StCbUpdates] += 1;
    patchScratch.assign((const unsigned char*)data, (const unsigned char*)data + bytes);
    if (!box)
        RememberUpload(resource, patchScratch.data(), bytes, false);
    if (PatchConstants((float*)patchScratch.data(), bytes) > 0)
        stats[StCbPatched] += 1;
    // a deferred context without driver command lists applies the box offset to the source again (documented D3D11 quirk)
    const unsigned char* source = patchScratch.data();
    if (box && !driverCommandLists)
        source -= box->left;
    if (withFlags)
        deferred->UpdateSubresource1(resource, subresource, box, source, rowPitch, depthPitch, copyFlags);
    else
        deferred->UpdateSubresource(resource, subresource, box, source, rowPitch, depthPitch);
}
static void STDMETHODCALLTYPE OurUpdateSubresource(Ctx* self, ID3D11Resource* resource, UINT subresource, const D3D11_BOX* box, const void* data,
    UINT rowPitch, UINT depthPitch)
{
    ORIGINAL(SUpdateSubresource, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT))(
        self, resource, subresource, box, data, rowPitch, depthPitch);
    if (Mirroring(self))
        UpdateConstants(resource, subresource, box, data, rowPitch, depthPitch, 0, false);
}
static void STDMETHODCALLTYPE OurUpdateSubresource1(Ctx* self, ID3D11Resource* resource, UINT subresource, const D3D11_BOX* box, const void* data,
    UINT rowPitch, UINT depthPitch, UINT flags)
{
    ORIGINAL(SUpdateSubresource1, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT, UINT))(
        self, resource, subresource, box, data, rowPitch, depthPitch, flags);
    if (Mirroring(self))
        UpdateConstants(resource, subresource, box, data, rowPitch, depthPitch, flags, true);
}

// Not recordable: the frame's replay is dropped
static void STDMETHODCALLTYPE OurSetPredication(Ctx* self, ID3D11Predicate* predicate, BOOL value)
{
    ORIGINAL(SSetPredication, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Predicate*, BOOL))(self, predicate, value);
    if (Mirroring(self) && predicate)
        stats[StPredication] += 1;
}
static void STDMETHODCALLTYPE OurSwapDeviceContextState(Ctx* self, ID3DDeviceContextState* state, ID3DDeviceContextState** previous)
{
    if (self == immediate) Abort(SSwapDeviceContextState);
    ORIGINAL(SSwapDeviceContextState, void (STDMETHODCALLTYPE*)(Ctx*, ID3DDeviceContextState*, ID3DDeviceContextState**))(self, state, previous);
}
static HRESULT STDMETHODCALLTYPE OurSignal(Ctx* self, ID3D11Fence* fence, UINT64 value)
{
    if (self == immediate) Abort(SSignal);
    return ORIGINAL(SSignal, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Fence*, UINT64))(self, fence, value);
}
static HRESULT STDMETHODCALLTYPE OurWait(Ctx* self, ID3D11Fence* fence, UINT64 value)
{
    if (self == immediate) Abort(SWait);
    return ORIGINAL(SWait, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Fence*, UINT64))(self, fence, value);
}
static HRESULT STDMETHODCALLTYPE OurUpdateTileMappings(Ctx* self, ID3D11Resource* a, UINT b, const D3D11_TILED_RESOURCE_COORDINATE* c,
    const D3D11_TILE_REGION_SIZE* d, ID3D11Buffer* e, UINT f, const UINT* g, const UINT* h, const UINT* i, UINT j)
{
    if (self == immediate) Abort(SUpdateTileMappings);
    return ORIGINAL(SUpdateTileMappings, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, UINT, const D3D11_TILED_RESOURCE_COORDINATE*,
        const D3D11_TILE_REGION_SIZE*, ID3D11Buffer*, UINT, const UINT*, const UINT*, const UINT*, UINT))(self, a, b, c, d, e, f, g, h, i, j);
}
static HRESULT STDMETHODCALLTYPE OurCopyTileMappings(Ctx* self, ID3D11Resource* a, const D3D11_TILED_RESOURCE_COORDINATE* b, ID3D11Resource* c,
    const D3D11_TILED_RESOURCE_COORDINATE* d, const D3D11_TILE_REGION_SIZE* e, UINT f)
{
    if (self == immediate) Abort(SCopyTileMappings);
    return ORIGINAL(SCopyTileMappings, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, const D3D11_TILED_RESOURCE_COORDINATE*, ID3D11Resource*,
        const D3D11_TILED_RESOURCE_COORDINATE*, const D3D11_TILE_REGION_SIZE*, UINT))(self, a, b, c, d, e, f);
}
static void STDMETHODCALLTYPE OurCopyTiles(Ctx* self, ID3D11Resource* a, const D3D11_TILED_RESOURCE_COORDINATE* b, const D3D11_TILE_REGION_SIZE* c,
    ID3D11Buffer* d, UINT64 e, UINT f)
{
    if (self == immediate) Abort(SCopyTiles);
    ORIGINAL(SCopyTiles, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, const D3D11_TILED_RESOURCE_COORDINATE*, const D3D11_TILE_REGION_SIZE*,
        ID3D11Buffer*, UINT64, UINT))(self, a, b, c, d, e, f);
}
static void STDMETHODCALLTYPE OurUpdateTiles(Ctx* self, ID3D11Resource* a, const D3D11_TILED_RESOURCE_COORDINATE* b, const D3D11_TILE_REGION_SIZE* c,
    const void* d, UINT e)
{
    if (self == immediate) Abort(SUpdateTiles);
    ORIGINAL(SUpdateTiles, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Resource*, const D3D11_TILED_RESOURCE_COORDINATE*, const D3D11_TILE_REGION_SIZE*,
        const void*, UINT))(self, a, b, c, d, e);
}
static HRESULT STDMETHODCALLTYPE OurResizeTilePool(Ctx* self, ID3D11Buffer* pool, UINT64 size)
{
    if (self == immediate) Abort(SResizeTilePool);
    return ORIGINAL(SResizeTilePool, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Buffer*, UINT64))(self, pool, size);
}
static void STDMETHODCALLTYPE OurTiledResourceBarrier(Ctx* self, ID3D11DeviceChild* before, ID3D11DeviceChild* after)
{
    if (self == immediate) Abort(STiledResourceBarrier);
    ORIGINAL(STiledResourceBarrier, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11DeviceChild*, ID3D11DeviceChild*))(self, before, after);
}

static inline void AfterUnrecorded(Ctx* self)
{
    if (self == immediate && recording)
        KeepWorkHooks();
}
static void STDMETHODCALLTYPE OurBegin(Ctx* self, ID3D11Asynchronous* query)
{ ORIGINAL(SBegin, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Asynchronous*))(self, query); AfterUnrecorded(self); }
static void STDMETHODCALLTYPE OurEnd(Ctx* self, ID3D11Asynchronous* query)
{ ORIGINAL(SEnd, void (STDMETHODCALLTYPE*)(Ctx*, ID3D11Asynchronous*))(self, query); AfterUnrecorded(self); }
static HRESULT STDMETHODCALLTYPE OurGetData(Ctx* self, ID3D11Asynchronous* query, void* data, UINT size, UINT flags)
{ HRESULT result = ORIGINAL(SGetData, HRESULT (STDMETHODCALLTYPE*)(Ctx*, ID3D11Asynchronous*, void*, UINT, UINT))(self, query, data, size, flags); AfterUnrecorded(self); return result; }
static void STDMETHODCALLTYPE OurFlush(Ctx* self)
{ ORIGINAL(SFlush, void (STDMETHODCALLTYPE*)(Ctx*))(self); AfterUnrecorded(self); }
static void STDMETHODCALLTYPE OurSetMarkerInt(Ctx* self, LPCWSTR label, INT data)
{ ORIGINAL(SSetMarkerInt, void (STDMETHODCALLTYPE*)(Ctx*, LPCWSTR, INT))(self, label, data); AfterUnrecorded(self); }
static void STDMETHODCALLTYPE OurBeginEventInt(Ctx* self, LPCWSTR label, INT data)
{ ORIGINAL(SBeginEventInt, void (STDMETHODCALLTYPE*)(Ctx*, LPCWSTR, INT))(self, label, data); AfterUnrecorded(self); }
static void STDMETHODCALLTYPE OurEndEvent(Ctx* self)
{ ORIGINAL(SEndEvent, void (STDMETHODCALLTYPE*)(Ctx*))(self); AfterUnrecorded(self); }

struct HookEntry { int slot; void* function; };
static const HookEntry hooks[] =
{
    { SVSSetConstantBuffers, (void*)OurVSSetConstantBuffers }, { SPSSetConstantBuffers, (void*)OurPSSetConstantBuffers },
    { SGSSetConstantBuffers, (void*)OurGSSetConstantBuffers }, { SHSSetConstantBuffers, (void*)OurHSSetConstantBuffers },
    { SDSSetConstantBuffers, (void*)OurDSSetConstantBuffers }, { SCSSetConstantBuffers, (void*)OurCSSetConstantBuffers },
    { SVSSetSamplers, (void*)OurVSSetSamplers }, { SPSSetSamplers, (void*)OurPSSetSamplers }, { SGSSetSamplers, (void*)OurGSSetSamplers },
    { SHSSetSamplers, (void*)OurHSSetSamplers }, { SDSSetSamplers, (void*)OurDSSetSamplers }, { SCSSetSamplers, (void*)OurCSSetSamplers },
    { SVSSetShaderResources, (void*)OurVSSetShaderResources }, { SPSSetShaderResources, (void*)OurPSSetShaderResources },
    { SGSSetShaderResources, (void*)OurGSSetShaderResources }, { SHSSetShaderResources, (void*)OurHSSetShaderResources },
    { SDSSetShaderResources, (void*)OurDSSetShaderResources }, { SCSSetShaderResources, (void*)OurCSSetShaderResources },
    { SVSSetShader, (void*)OurVSSetShader }, { SPSSetShader, (void*)OurPSSetShader }, { SGSSetShader, (void*)OurGSSetShader },
    { SHSSetShader, (void*)OurHSSetShader }, { SDSSetShader, (void*)OurDSSetShader }, { SCSSetShader, (void*)OurCSSetShader },
    { SVSSetConstantBuffers1, (void*)OurVSSetConstantBuffers1 }, { SPSSetConstantBuffers1, (void*)OurPSSetConstantBuffers1 },
    { SGSSetConstantBuffers1, (void*)OurGSSetConstantBuffers1 }, { SHSSetConstantBuffers1, (void*)OurHSSetConstantBuffers1 },
    { SDSSetConstantBuffers1, (void*)OurDSSetConstantBuffers1 }, { SCSSetConstantBuffers1, (void*)OurCSSetConstantBuffers1 },
    { SDraw, (void*)OurDraw }, { SDrawIndexed, (void*)OurDrawIndexed }, { SDrawInstanced, (void*)OurDrawInstanced },
    { SDrawIndexedInstanced, (void*)OurDrawIndexedInstanced }, { SDrawAuto, (void*)OurDrawAuto },
    { SDrawIndexedInstancedIndirect, (void*)OurDrawIndexedInstancedIndirect }, { SDrawInstancedIndirect, (void*)OurDrawInstancedIndirect },
    { SDispatch, (void*)OurDispatch }, { SDispatchIndirect, (void*)OurDispatchIndirect },
    { SIASetInputLayout, (void*)OurIASetInputLayout }, { SIASetVertexBuffers, (void*)OurIASetVertexBuffers },
    { SIASetIndexBuffer, (void*)OurIASetIndexBuffer }, { SIASetPrimitiveTopology, (void*)OurIASetPrimitiveTopology },
    { SRSSetState, (void*)OurRSSetState }, { SRSSetViewports, (void*)OurRSSetViewports }, { SRSSetScissorRects, (void*)OurRSSetScissorRects },
    { SSOSetTargets, (void*)OurSOSetTargets }, { SOMSetRenderTargets, (void*)OurOMSetRenderTargets },
    { SOMSetRenderTargetsAndUnorderedAccessViews, (void*)OurOMSetRenderTargetsAndUnorderedAccessViews },
    { SOMSetBlendState, (void*)OurOMSetBlendState }, { SOMSetDepthStencilState, (void*)OurOMSetDepthStencilState },
    { SCSSetUnorderedAccessViews, (void*)OurCSSetUnorderedAccessViews },
    { SClearRenderTargetView, (void*)OurClearRenderTargetView }, { SClearUnorderedAccessViewUint, (void*)OurClearUnorderedAccessViewUint },
    { SClearUnorderedAccessViewFloat, (void*)OurClearUnorderedAccessViewFloat }, { SClearDepthStencilView, (void*)OurClearDepthStencilView },
    { SCopySubresourceRegion, (void*)OurCopySubresourceRegion }, { SCopySubresourceRegion1, (void*)OurCopySubresourceRegion1 },
    { SCopyResource, (void*)OurCopyResource }, { SCopyStructureCount, (void*)OurCopyStructureCount }, { SGenerateMips, (void*)OurGenerateMips },
    { SSetResourceMinLOD, (void*)OurSetResourceMinLOD }, { SResolveSubresource, (void*)OurResolveSubresource },
    { SExecuteCommandList, (void*)OurExecuteCommandList }, { SClearState, (void*)OurClearState },
    { SDiscardResource, (void*)OurDiscardResource }, { SDiscardView, (void*)OurDiscardView }, { SDiscardView1, (void*)OurDiscardView1 },
    { SClearView, (void*)OurClearView }, { SMap, (void*)OurMap }, { SUnmap, (void*)OurUnmap },
    { SUpdateSubresource, (void*)OurUpdateSubresource }, { SUpdateSubresource1, (void*)OurUpdateSubresource1 },
    { SSetPredication, (void*)OurSetPredication }, { SSwapDeviceContextState, (void*)OurSwapDeviceContextState },
    { SSignal, (void*)OurSignal }, { SWait, (void*)OurWait }, { SUpdateTileMappings, (void*)OurUpdateTileMappings },
    { SCopyTileMappings, (void*)OurCopyTileMappings }, { SCopyTiles, (void*)OurCopyTiles }, { SUpdateTiles, (void*)OurUpdateTiles },
    { SResizeTilePool, (void*)OurResizeTilePool }, { STiledResourceBarrier, (void*)OurTiledResourceBarrier },
    { SBegin, (void*)OurBegin }, { SEnd, (void*)OurEnd }, { SGetData, (void*)OurGetData }, { SFlush, (void*)OurFlush },
    { SSetMarkerInt, (void*)OurSetMarkerInt }, { SBeginEventInt, (void*)OurBeginEventInt }, { SEndEvent, (void*)OurEndEvent },
};

static bool WriteVtable(bool install)
{
    DWORD old;
    if (!VirtualProtect(vtable, sizeof(void*) * VtableSize, PAGE_READWRITE, &old))
        return false;
    for (const HookEntry& hook : hooks)
    {
        if (install)
            vtable[hook.slot] = hook.function;
        else if (vtable[hook.slot] == hook.function)
            vtable[hook.slot] = original[hook.slot];
    }
    VirtualProtect(vtable, sizeof(void*) * VtableSize, old, &old);
    FlushInstructionCache(GetCurrentProcess(), vtable, sizeof(void*) * VtableSize);
    return true;
}

// Who a function pointer belongs to (the module's file name)
static const char* ModuleOf(void* address, char* buffer, int size)
{
    HMODULE module = nullptr;
    buffer[0] = 0;
    if (address && GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)address, &module) && module)
    {
        char path[MAX_PATH];
        DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
        const char* name = path;
        for (DWORD index = 0; index < length; index++)
            if (path[index] == '\\' || path[index] == '/') name = path + index + 1;
        snprintf(buffer, size, "%s", name);
    }
    else
        snprintf(buffer, size, "no module (%p)", address);
    return buffer;
}

static bool SameModule(void* first, void* second)
{
    HMODULE a = nullptr, b = nullptr;
    const DWORD flags = GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
    return GetModuleHandleExA(flags, (LPCSTR)first, &a) && GetModuleHandleExA(flags, (LPCSTR)second, &b) && a && a == b;
}

// What took our vtable entries / the context's vtable since the last report (each event once)
static char hookReport[4096];
static int hookReportLength;
static int reportedSlots[VtableSize];
static void Report(const char* format, ...)
{
    if (hookReportLength >= (int)sizeof(hookReport) - 1)
        return;
    va_list arguments;
    va_start(arguments, format);
    int written = vsnprintf(hookReport + hookReportLength, sizeof(hookReport) - hookReportLength, format, arguments);
    va_end(arguments);
    if (written > 0)
        hookReportLength += written < (int)sizeof(hookReport) - hookReportLength ? written : (int)sizeof(hookReport) - hookReportLength - 1;
}

// Our entries back where something replaced them (what replaced them is called on, as the original), and the context's new vtable
// hooked if its vtable pointer moved
static int ReassertHooks()
{
    char module[MAX_PATH];
    void** current = *(void***)immediate;
    if (current != vtable)
    {
        Report("the immediate context's vtable moved %p -> %p (%s); hooking the new one\n", (void*)vtable, (void*)current,
            ModuleOf(current, module, sizeof(module)));
        vtable = current;
        memcpy(original, vtable, sizeof(original));
        WriteVtable(true);
        return -1;
    }
    int replaced = 0;
    for (const HookEntry& hook : hooks)
    {
        if (vtable[hook.slot] != hook.function)
            replaced++;
    }
    if (replaced == 0)
        return 0;
    DWORD old;
    if (!VirtualProtect(vtable, sizeof(void*) * VtableSize, PAGE_READWRITE, &old))
        return 0;
    for (const HookEntry& hook : hooks)
    {
        void* now = vtable[hook.slot];
        if (now == hook.function)
            continue;
        if (reportedSlots[hook.slot] < 3)
        {
            reportedSlots[hook.slot]++;
            char was[MAX_PATH];
            Report("slot %d was replaced: now %s, our original was %s\n", hook.slot, ModuleOf(now, module, sizeof(module)),
                ModuleOf(original[hook.slot], was, sizeof(was)));
        }
        // the runtime's own swap (same module) is what we call from now on; another hooker's is skipped, since it may call ours back
        if (SameModule(now, original[hook.slot]))
            original[hook.slot] = now;
        vtable[hook.slot] = hook.function;
    }
    VirtualProtect(vtable, sizeof(void*) * VtableSize, old, &old);
    return replaced;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Window start: the immediate context's whole state onto the deferred context (the pass's first calls lean on state set before it)

template <typename T>
static void ReleaseAll(T** items, UINT count)
{
    for (UINT index = 0; index < count; index++)
        if (items[index]) items[index]->Release();
}

// Bound without offsets (every first constant 0): rebound the same way
static bool Plain(ID3D11Buffer* const* buffers, const UINT* first, UINT count)
{
    for (UINT index = 0; index < count; index++)
    {
        if (buffers[index] && first[index] != 0)
            return false;
    }
    return true;
}

static void CopyStageState(int stage)
{
    ID3D11Buffer* buffers[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT] = {};
    UINT first[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT] = {};
    UINT number[D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT] = {};
    ID3D11ShaderResourceView* views[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};
    ID3D11ShaderResourceView* swapped[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT];
    ID3D11SamplerState* samplers[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT] = {};
    const UINT cbCount = D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT;
    const UINT viewCount = D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT;
    const UINT samplerCount = D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT;
    switch (stage)
    {
        case 0:
        {
            ID3D11VertexShader* shader = nullptr; immediate1->VSGetShader(&shader, nullptr, nullptr); deferred->VSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->VSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->VSGetShaderResources(0, viewCount, views); immediate1->VSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->VSSetConstantBuffers(0, cbCount, buffers); else deferred->VSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->VSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->VSSetSamplers(0, samplerCount, samplers);
            break;
        }
        case 1:
        {
            ID3D11HullShader* shader = nullptr; immediate1->HSGetShader(&shader, nullptr, nullptr); deferred->HSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->HSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->HSGetShaderResources(0, viewCount, views); immediate1->HSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->HSSetConstantBuffers(0, cbCount, buffers); else deferred->HSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->HSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->HSSetSamplers(0, samplerCount, samplers);
            break;
        }
        case 2:
        {
            ID3D11DomainShader* shader = nullptr; immediate1->DSGetShader(&shader, nullptr, nullptr); deferred->DSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->DSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->DSGetShaderResources(0, viewCount, views); immediate1->DSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->DSSetConstantBuffers(0, cbCount, buffers); else deferred->DSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->DSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->DSSetSamplers(0, samplerCount, samplers);
            break;
        }
        case 3:
        {
            ID3D11GeometryShader* shader = nullptr; immediate1->GSGetShader(&shader, nullptr, nullptr); deferred->GSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->GSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->GSGetShaderResources(0, viewCount, views); immediate1->GSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->GSSetConstantBuffers(0, cbCount, buffers); else deferred->GSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->GSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->GSSetSamplers(0, samplerCount, samplers);
            break;
        }
        case 4:
        {
            ID3D11PixelShader* shader = nullptr; immediate1->PSGetShader(&shader, nullptr, nullptr); deferred->PSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->PSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->PSGetShaderResources(0, viewCount, views); immediate1->PSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->PSSetConstantBuffers(0, cbCount, buffers); else deferred->PSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->PSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->PSSetSamplers(0, samplerCount, samplers);
            break;
        }
        default:
        {
            ID3D11ComputeShader* shader = nullptr; immediate1->CSGetShader(&shader, nullptr, nullptr); deferred->CSSetShader(shader, nullptr, 0); if (shader) shader->Release();
            immediate1->CSGetConstantBuffers1(0, cbCount, buffers, first, number); immediate1->CSGetShaderResources(0, viewCount, views); immediate1->CSGetSamplers(0, samplerCount, samplers);
            if (Plain(buffers, first, cbCount)) deferred->CSSetConstantBuffers(0, cbCount, buffers); else deferred->CSSetConstantBuffers1(0, cbCount, buffers, first, number); deferred->CSSetShaderResources(0, viewCount, SwapViews(views, viewCount, swapped, viewCount, KindSrv)); deferred->CSSetSamplers(0, samplerCount, samplers);
            ID3D11UnorderedAccessView* uavs[D3D11_1_UAV_SLOT_COUNT] = {};
            ID3D11UnorderedAccessView* swappedUavs[D3D11_1_UAV_SLOT_COUNT];
            UINT keepCounts[D3D11_1_UAV_SLOT_COUNT];
            for (UINT index = 0; index < D3D11_1_UAV_SLOT_COUNT; index++) keepCounts[index] = (UINT)-1;
            immediate1->CSGetUnorderedAccessViews(0, uavSlots, uavs);
            deferred->CSSetUnorderedAccessViews(0, uavSlots, SwapViews(uavs, uavSlots, swappedUavs, D3D11_1_UAV_SLOT_COUNT, KindUav), keepCounts);
            ReleaseAll(uavs, uavSlots);
            break;
        }
    }
    ReleaseAll(buffers, cbCount);
    ReleaseAll(views, viewCount);
    ReleaseAll(samplers, samplerCount);
}

static void CopyState()
{
    double start = NowMs();
    ID3D11InputLayout* layout = nullptr;
    immediate1->IAGetInputLayout(&layout);
    deferred->IASetInputLayout(layout);
    if (layout) layout->Release();
    ID3D11Buffer* vertexBuffers[D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT] = {};
    UINT strides[D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT] = {};
    UINT offsets[D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT] = {};
    immediate1->IAGetVertexBuffers(0, D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT, vertexBuffers, strides, offsets);
    deferred->IASetVertexBuffers(0, D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT, vertexBuffers, strides, offsets);
    ReleaseAll(vertexBuffers, D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT);
    ID3D11Buffer* indexBuffer = nullptr;
    DXGI_FORMAT indexFormat = DXGI_FORMAT_UNKNOWN;
    UINT indexOffset = 0;
    immediate1->IAGetIndexBuffer(&indexBuffer, &indexFormat, &indexOffset);
    deferred->IASetIndexBuffer(indexBuffer, indexFormat, indexOffset);
    if (indexBuffer) indexBuffer->Release();
    D3D11_PRIMITIVE_TOPOLOGY topology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    immediate1->IAGetPrimitiveTopology(&topology);
    deferred->IASetPrimitiveTopology(topology);

    for (int stage = 0; stage < 6; stage++)
        CopyStageState(stage);

    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11RenderTargetView* swappedRtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT];
    ID3D11DepthStencilView* depth = nullptr;
    ID3D11UnorderedAccessView* uavs[D3D11_1_UAV_SLOT_COUNT] = {};
    ID3D11UnorderedAccessView* swappedUavs[D3D11_1_UAV_SLOT_COUNT];
    immediate1->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, &depth);
    immediate1->OMGetRenderTargetsAndUnorderedAccessViews(0, nullptr, nullptr, 0, uavSlots, uavs);
    UINT firstUav = uavSlots;
    for (UINT index = 0; index < uavSlots; index++)
    {
        if (uavs[index]) { firstUav = index; break; }
    }
    ID3D11RenderTargetView* const* boundRtvs = SwapViews(rtvs, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, swappedRtvs, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, KindRtv);
    if (firstUav < uavSlots)
    {
        UINT keepCounts[D3D11_1_UAV_SLOT_COUNT];
        for (UINT index = 0; index < D3D11_1_UAV_SLOT_COUNT; index++) keepCounts[index] = (UINT)-1;
        UINT rtvCount = firstUav < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT ? firstUav : D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT;
        deferred->OMSetRenderTargetsAndUnorderedAccessViews(rtvCount, boundRtvs, (ID3D11DepthStencilView*)SwapView(depth, KindDsv), firstUav,
            uavSlots - firstUav, SwapViews(uavs + firstUav, uavSlots - firstUav, swappedUavs, D3D11_1_UAV_SLOT_COUNT, KindUav), keepCounts);
    }
    else
        deferred->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, boundRtvs, (ID3D11DepthStencilView*)SwapView(depth, KindDsv));
    ReleaseAll(rtvs, D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT);
    if (depth) depth->Release();
    ReleaseAll(uavs, uavSlots);

    ID3D11BlendState* blend = nullptr;
    FLOAT factor[4] = {};
    UINT mask = 0xffffffff;
    immediate1->OMGetBlendState(&blend, factor, &mask);
    deferred->OMSetBlendState(blend, factor, mask);
    if (blend) blend->Release();
    ID3D11DepthStencilState* depthState = nullptr;
    UINT reference = 0;
    immediate1->OMGetDepthStencilState(&depthState, &reference);
    deferred->OMSetDepthStencilState(depthState, reference);
    if (depthState) depthState->Release();
    ID3D11RasterizerState* rasterizer = nullptr;
    immediate1->RSGetState(&rasterizer);
    deferred->RSSetState(rasterizer);
    if (rasterizer) rasterizer->Release();
    D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    UINT viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    immediate1->RSGetViewports(&viewportCount, viewports);
    deferred->RSSetViewports(viewportCount, viewports);
    D3D11_RECT scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    UINT scissorCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    immediate1->RSGetScissorRects(&scissorCount, scissors);
    deferred->RSSetScissorRects(scissorCount, scissors);
    ID3D11Buffer* streamOut[D3D11_SO_BUFFER_SLOT_COUNT] = {};
    immediate1->SOGetTargets(D3D11_SO_BUFFER_SLOT_COUNT, streamOut);
    UINT appendOffsets[D3D11_SO_BUFFER_SLOT_COUNT] = { (UINT)-1, (UINT)-1, (UINT)-1, (UINT)-1 };
    deferred->SOSetTargets(D3D11_SO_BUFFER_SLOT_COUNT, streamOut, appendOffsets);
    ReleaseAll(streamOut, D3D11_SO_BUFFER_SLOT_COUNT);
    ID3D11Predicate* predicate = nullptr;
    BOOL predicateValue = FALSE;
    immediate1->GetPredication(&predicate, &predicateValue);
    if (predicate) { stats[StPredication] += 1; predicate->Release(); }
    stats[StCaptureMs] += NowMs() - start;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Windows

static double windowStart;

static bool EnsureRightTexture(ID3D11Texture2D* left)
{
    D3D11_TEXTURE2D_DESC description;
    left->GetDesc(&description);
    description.MiscFlags &= ~(D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX | D3D11_RESOURCE_MISC_SHARED_NTHANDLE);
    if (rightTexture && memcmp(&description, &rightDesc, sizeof(description)) == 0)
        return true;
    for (auto& view : ourViews)
        if (view.second) view.second->Release();
    ourViews.clear();
    if (rightTexture) { rightTexture->Release(); rightTexture = nullptr; }
    if (FAILED(device->CreateTexture2D(&description, nullptr, &rightTexture)))
    {
        rightTexture = nullptr;
        return false;
    }
    rightDesc = description;
    return true;
}

static void EndWindow(bool drop);

static void BeginWindow(int slot)
{
    if (recording)
    {
        stats[StUnpaired] += 1;
        EndWindow(true);
    }
    if (!enabled || !hooked || slot < 0 || slot >= kFrameSlots)
        return;
    void* images[8];
    int count = viewImages ? viewImages(0, images, 8) : 0;
    if (count <= 0 || !EnsureRightTexture((ID3D11Texture2D*)images[0]))
    {
        stats[StNoImages] += 1;
        return;
    }
    leftCount = count;
    for (int index = 0; index < count; index++)
        leftImages[index] = (ID3D11Resource*)images[index];
    int replaced = ReassertHooks();
    if (replaced != 0)
        stats[StRehooked] += replaced > 0 ? replaced : 1;
    activeCount = frameSlots[slot].count;
    expectedEnd = frameSlots[slot].endEvent;
    memcpy(active, frameSlots[slot].patterns, sizeof(Pattern) * activeCount);
    memset(viewCache, 0, sizeof(viewCache));
    windowStart = NowMs();
    CopyState();
    recording = true;
    aborted = false;
}

static void EndWindow(bool drop)
{
    if (!recording)
        return;
    recording = false;
    RestoreUploads();
    ID3D11CommandList* list = nullptr;
    HRESULT result = deferred->FinishCommandList(FALSE, &list);
    if (FAILED(result) || !list)
    {
        stats[StFinishFailed] += 1;
        return;
    }
    if (drop || aborted)
    {
        list->Release();
        stats[StDropped] += 1;
        return;
    }
    double start = NowMs();
    immediate1->ExecuteCommandList(list, TRUE);
    stats[StExecuteMs] += NowMs() - start;
    list->Release();
    void* right = copyAtOnce && viewAcquiredImage ? viewAcquiredImage(1) : nullptr;
    if (right)
    {
        immediate1->CopyResource((ID3D11Resource*)right, rightTexture);
        stats[StCopiedNow] += 1;
        pendingCopy = false;
    }
    else
        pendingCopy = true;
    if (viewAcquiredImage && viewAcquiredImage(1))
        stats[StRightTakenAtEnd] += 1;
    stats[StWindows] += 1;
    stats[StWindowMs] += NowMs() - windowStart;
}

// XrFrameWatch: an image going back to the runtime
static void* lastCopiedImage;
static void ReleaseHook(int view, void* texture)
{
    if (view == 1)
    {
        if (texture == lastCopiedImage)
            stats[StSameImageTwice] += 1;
        lastCopiedImage = texture;
    }
    if (view != 1 || !pendingCopy || !rightTexture || !immediate1)
        return;
    pendingCopy = false;
    immediate1->CopyResource((ID3D11Resource*)texture, rightTexture);
    stats[StCopiedAtRelease] += 1;
}

static const int kStartEvent = 1000, kEndEvent = 2000, kEndEvents = 16;

static void __stdcall OnRenderEvent(int id)
{
    if (id >= kStartEvent && id < kStartEvent + kFrameSlots)
        BeginWindow(id - kStartEvent);
    else if (id >= kEndEvent && id < kEndEvent + kEndEvents && id == expectedEnd)
        EndWindow(false);
}

//------------------------------------------------------------------------------------------------------------------------------------
// C#

SR_EXPORT int SR_Init(void* anyD3D11Resource)
{
    if (hooked)
        return 1;
    QueryPerformanceFrequency(&clockFrequency);
    if (!anyD3D11Resource)
        return 0;
    ((ID3D11Resource*)anyD3D11Resource)->GetDevice(&device);
    if (!device)
        return 0;
    device->GetImmediateContext(&immediate);
    if (FAILED(immediate->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&immediate1)) || FAILED(device->QueryInterface(__uuidof(ID3D11Device1), (void**)&device1)))
    {
        Info("no D3D11.1 device context\n");
        return 0;
    }
    ID3D11DeviceContext4* immediate4 = nullptr;
    if (FAILED(immediate->QueryInterface(__uuidof(ID3D11DeviceContext4), (void**)&immediate4)) || !immediate4)
    {
        Info("no ID3D11DeviceContext4 (its vtable slots are the ones patched)\n");
        return 0;
    }
    bool samePointer = (void*)immediate4 == (void*)immediate && (void*)immediate1 == (void*)immediate;
    immediate4->Release();
    if (!samePointer)
    {
        Info("the context's interface versions are different pointers - not handled\n");
        return 0;
    }
    if (FAILED(device1->CreateDeferredContext1(0, &deferred)) || !deferred)
    {
        Info("CreateDeferredContext1 failed\n");
        return 0;
    }
    D3D11_FEATURE_DATA_THREADING threading = {};
    device->CheckFeatureSupport(D3D11_FEATURE_THREADING, &threading, sizeof(threading));
    driverCommandLists = threading.DriverCommandLists != FALSE;
    uavSlots = device->GetFeatureLevel() >= D3D_FEATURE_LEVEL_11_1 ? D3D11_1_UAV_SLOT_COUNT : D3D11_PS_CS_UAV_REGISTER_COUNT;
    vtable = *(void***)immediate;
    sharedVtable = *(void***)deferred == vtable;
    memcpy(original, vtable, sizeof(original));
    for (const HookEntry& hook : hooks)
        workHooks[hook.slot] = hook.function;
    HMODULE runtime = GetModuleHandleW(L"d3d11.dll");
    if (runtime)
    {
        const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)runtime;
        const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)((const unsigned char*)runtime + dos->e_lfanew);
        runtimeStart = (uintptr_t)runtime;
        runtimeEnd = runtimeStart + nt->OptionalHeader.SizeOfImage;
    }
    if (!WriteVtable(true))
    {
        Info("vtable not writable\n");
        return 0;
    }
    DWORD previousProtection;
    VirtualProtect(vtable, sizeof(void*) * VtableSize, PAGE_READWRITE, &previousProtection);
    hooked = true;
    char drawModule[MAX_PATH], drawIndexedModule[MAX_PATH], mapModule[MAX_PATH], targetsModule[MAX_PATH];
    Info("the entries we took were in: Draw %s, DrawIndexed %s, Map %s, OMSetRenderTargets %s\n",
        ModuleOf(original[SDraw], drawModule, MAX_PATH), ModuleOf(original[SDrawIndexed], drawIndexedModule, MAX_PATH),
        ModuleOf(original[SMap], mapModule, MAX_PATH), ModuleOf(original[SOMSetRenderTargets], targetsModule, MAX_PATH));
    const int drawSlots[4] = { SDraw, SDrawIndexed, SDrawInstanced, SDrawIndexedInstanced };
    for (int slot : drawSlots)
    {
        const unsigned char* code = (const unsigned char*)original[slot];
        Info("slot %d starts:", slot);
        for (int index = 0; index < 24; index++)
            Info(" %02x", code[index]);
        Info("\n");
    }
    HMODULE watch = GetModuleHandleW(L"SPTVRXrFrameWatch.dll");
    if (watch)
    {
        viewImages = (PFN_ViewImages)GetProcAddress(watch, "XrWatch_ViewImages");
        viewAcquiredImage = (PFN_ViewAcquiredImage)GetProcAddress(watch, "XrWatch_ViewAcquiredImage");
        PFN_SetReleaseHook setReleaseHook = (PFN_SetReleaseHook)GetProcAddress(watch, "XrWatch_SetReleaseHook");
        if (setReleaseHook)
            setReleaseHook((void*)ReleaseHook);
    }
    Info("feature level %x, driver command lists %d, UAV slots %u, deferred context shares the vtable %d, frame watch %s\n",
        device->GetFeatureLevel(), driverCommandLists ? 1 : 0, uavSlots, sharedVtable ? 1 : 0,
        viewImages && viewAcquiredImage ? "found" : "MISSING (no eye images: nothing is replayed)");
    return 1;
}

SR_EXPORT void SR_SetEnabled(int on)
{
    InterlockedExchange(&enabled, on ? 1 : 0);
}

SR_EXPORT void* SR_GetRenderEventFunc()
{
    return (void*)&OnRenderEvent;
}

// The camera values for one frame: count patterns of lengths[i] floats, left / right 16 floats each (unused tail ignored)
SR_EXPORT void SR_SetFrame(int slot, int count, int endEvent, const int* lengths, const float* tolerances, const float* left, const float* right)
{
    if (slot < 0 || slot >= kFrameSlots || count < 0)
        return;
    FrameSlot& frame = frameSlots[slot];
    frame.count = count < kMaxPatterns ? count : kMaxPatterns;
    frame.endEvent = endEvent;
    for (int index = 0; index < frame.count; index++)
    {
        Pattern& pattern = frame.patterns[index];
        pattern.length = lengths[index] < 1 ? 1 : (lengths[index] > 16 ? 16 : lengths[index]);
        pattern.tolerance = tolerances[index];
        memcpy(pattern.left, left + index * 16, sizeof(float) * 16);
        memcpy(pattern.right, right + index * 16, sizeof(float) * 16);
    }
}

// Counters since the last call (then zeroed), in Stat order; the last aborting call's vtable slot after them
SR_EXPORT int SR_Stats(double* out, int max)
{
    if (!out || max <= 0)
        return 0;
    int count = max < StCount ? max : StCount;
    for (int index = 0; index < StCount; index++)
        stats[index] += (double)InterlockedExchange(&drawCounters[index], 0);
    for (int index = 0; index < count; index++)
        out[index] = stats[index];
    if (max > StCount)
        out[StCount] = lastAbortSlot;
    memset(stats, 0, sizeof(stats));
    return count;
}

// How often each pattern was found since the last call (then zeroed)
SR_EXPORT int SR_PatternHits(double* out, int max)
{
    if (!out || max <= 0)
        return 0;
    int count = max < kMaxPatterns ? max : kMaxPatterns;
    for (int index = 0; index < count; index++)
        out[index] = patternHits[index];
    memset(patternHits, 0, sizeof(patternHits));
    return count;
}

// What replaced our hooks since the last call (then cleared)
SR_EXPORT int SR_HookReport(char* out, int max)
{
    if (!out || max <= 0)
        return 0;
    int length = hookReportLength < max - 1 ? hookReportLength : max - 1;
    memcpy(out, hookReport, length);
    out[length] = 0;
    hookReportLength = 0;
    return length;
}

SR_EXPORT int SR_Info(char* out, int max)
{
    if (!out || max <= 0)
        return 0;
    int length = infoLength < max - 1 ? infoLength : max - 1;
    memcpy(out, info, length);
    out[length] = 0;
    return length;
}
