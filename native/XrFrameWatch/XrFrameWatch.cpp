// SPTVRXrFrameWatch.dll: sits in front of the OpenXR frame functions Unity's OpenXR plugin calls, through the xrGetInstanceProcAddr
// chain Unity lets every feature hook (Source/XR/OpenXRFrameWatch.cs), for SPT-VR's alternate-eye rendering
// (Source/Misc/AlternateEyeExperiment.cs).
//  - Watching (always): the layers submitted, each projection view's swapchain / rect / slice / pose, which swapchains were taken,
//    and how long xrWaitFrame / xrBeginFrame / the swapchain waits / xrEndFrame held the caller.
//  - Alternate eyes (only on a frame the game marks so, via the render event): the game rendered one eye. Unity still takes and
//    hands back an image of both eyes' swapchains every frame (measured), so the eye left out would show whatever was last drawn in
//    that image, several frames old, labelled with this frame's pose. Instead:
//      on hand-back (xrReleaseSwapchainImage) the drawn eye's image is copied into our own copy of it, and the eye left out gets its
//      own last drawing copied into the image it hands back;
//      on submit (xrEndFrame) the eye left out goes with the pose and field of view it was drawn with, moved by however far the
//      rig has turned and moved since (the runtime then reprojects it to the head's pose like any late frame).
// Native because xrEndFrame and the swapchain calls run on Unity's render thread. Struct layouts follow the OpenXR spec (checked
// against openxr_reflection_full.h in the OpenXR package): x64, natural alignment. Anything unexpected passes the call through.
#include <windows.h>
#include <d3d11.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef int32_t XrResult;
typedef uint64_t XrInstance;
typedef uint64_t XrSession;
typedef uint64_t XrSwapchain;
typedef uint64_t XrSpace;
typedef int64_t XrTime;
typedef void (*PFN_xrVoidFunction)(void);
typedef XrResult (*PFN_GetInstanceProcAddr)(XrInstance instance, const char* name, PFN_xrVoidFunction* function);

static const int32_t XR_TYPE_COMPOSITION_LAYER_PROJECTION = 35;
static const int32_t XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW = 48;
static const int32_t XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR = 1000027001;

struct XrQuaternion { float x, y, z, w; };
struct XrVector3 { float x, y, z; };
struct XrPose { XrQuaternion orientation; XrVector3 position; };
struct XrFov { float angleLeft, angleRight, angleUp, angleDown; };
struct XrRect { int32_t x, y, width, height; };
struct XrSubImage { XrSwapchain swapchain; XrRect imageRect; uint32_t imageArrayIndex; };
struct XrProjectionView { int32_t type; const void* next; XrPose pose; XrFov fov; XrSubImage subImage; };
struct XrLayerHeader { int32_t type; const void* next; uint64_t layerFlags; XrSpace space; };
struct XrLayerProjection { int32_t type; const void* next; uint64_t layerFlags; XrSpace space; uint32_t viewCount; const XrProjectionView* views; };
struct XrFrameEndInfo { int32_t type; const void* next; XrTime displayTime; int32_t environmentBlendMode; uint32_t layerCount; const XrLayerHeader* const* layers; };
struct XrSwapchainImageD3D11 { int32_t type; void* next; ID3D11Texture2D* texture; };

static_assert(sizeof(XrProjectionView) == 96, "XrCompositionLayerProjectionView layout");
static_assert(offsetof(XrProjectionView, subImage) == 64, "XrSwapchainSubImage offset");
static_assert(offsetof(XrLayerProjection, views) == 40, "XrCompositionLayerProjection layout");
static_assert(offsetof(XrFrameEndInfo, layers) == 32, "XrFrameEndInfo layout");
static_assert(sizeof(XrSwapchainImageD3D11) == 24, "XrSwapchainImageD3D11KHR layout");

typedef XrResult (*PFN_EndFrame)(XrSession session, const XrFrameEndInfo* frameEndInfo);
typedef XrResult (*PFN_WaitFrame)(XrSession session, const void* frameWaitInfo, void* frameState);
typedef XrResult (*PFN_BeginFrame)(XrSession session, const void* frameBeginInfo);
typedef XrResult (*PFN_AcquireImage)(XrSwapchain swapchain, const void* acquireInfo, uint32_t* index);
typedef XrResult (*PFN_WaitImage)(XrSwapchain swapchain, const void* waitInfo);
typedef XrResult (*PFN_ReleaseImage)(XrSwapchain swapchain, const void* releaseInfo);
typedef XrResult (*PFN_EnumerateImages)(XrSwapchain swapchain, uint32_t capacity, uint32_t* count, void* images);

static PFN_GetInstanceProcAddr nextGetInstanceProcAddr;
static PFN_EndFrame runtimeEndFrame;
static PFN_WaitFrame runtimeWaitFrame;
static PFN_BeginFrame runtimeBeginFrame;
static PFN_AcquireImage runtimeAcquireImage;
static PFN_WaitImage runtimeWaitImage;
static PFN_ReleaseImage runtimeReleaseImage;
static PFN_EnumerateImages runtimeEnumerateImages;
static volatile LONG hookedFunctions;   // bits: 1 end, 2 wait frame, 4 begin, 8 acquire, 16 wait image, 32 release, 64 enumerate

static CRITICAL_SECTION statsLock;
static bool statsLockReady;
static double clockToMs;

//------------------------------------------------------------------------------------------------------------------------------------
// Swapchains: their images (xrEnumerateSwapchainImages) and this frame's take

static const int MAX_SWAPCHAINS = 16;
static const int MAX_IMAGES = 8;
struct Swapchain
{
    XrSwapchain handle;
    ID3D11Texture2D* images[MAX_IMAGES];
    uint32_t imageCount;
    int acquired;           // this frame
    int released;
    uint32_t lastIndex;     // the image acquired this frame
};
static Swapchain swapchains[MAX_SWAPCHAINS];
static int swapchainCount;

static Swapchain* SwapchainOf(XrSwapchain handle, bool add)
{
    for (int index = 0; index < swapchainCount; index++)
    {
        if (swapchains[index].handle == handle)
            return &swapchains[index];
    }
    if (!add || swapchainCount >= MAX_SWAPCHAINS)
        return nullptr;
    Swapchain* swapchain = &swapchains[swapchainCount++];
    memset(swapchain, 0, sizeof(Swapchain));
    swapchain->handle = handle;
    return swapchain;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Alternate eyes: what the game says about this frame (render event), and what we keep per eye

static int frameEye = -1;                   // the eye the game rendered this frame (0 left, 1 right); -1 = both, nothing to do
static int frameRigSlot = -1;
static const int RIG_SLOTS = 16;
static float rigSlots[RIG_SLOTS][8];        // the rig's world pose a frame: position xyz, rotation xyzw, scale (Unity's space)
static XrSwapchain eyeSwapchain[2];         // learned from the views Unity submits (view 0 left, view 1 right)

struct KeptEye
{
    ID3D11Texture2D* copy;                  // our copy of its last drawing
    D3D11_TEXTURE2D_DESC desc;
    bool copyValid;
    XrPose pose;                            // what it was drawn with (as Unity submitted it then)
    XrFov fov;
    float rig[8];
    bool poseValid;
    bool keptThisFrame;                     // this frame's hand-back copied it (drawn) or gave it its last drawing (left out)
};
static KeptEye keptEyes[2];
static ID3D11DeviceContext* context;

// The frame end info we hand on when an eye's view is rewritten: copies, Unity's are const
static const int MAX_LAYERS = 16;
static XrFrameEndInfo rewrittenInfo;
static const XrLayerHeader* rewrittenLayers[MAX_LAYERS];
static XrLayerProjection rewrittenProjection;
static XrProjectionView rewrittenViews[2];

//------------------------------------------------------------------------------------------------------------------------------------
// Totals since the last XrWatch_Stats call

enum Stat
{
    StatFrames, StatLayers, StatProjectionLayers, StatViews, StatViewsShareSwapchain,
    StatView0Acquired, StatView1Acquired, StatView0SamePose, StatView1SamePose,
    StatAcquires, StatReleases, StatSwapchainsTaken,
    StatWaitFrameMs, StatWaitFrameMax, StatBeginFrameMs, StatBeginFrameMax, StatWaitImageMs, StatWaitImageMax,
    StatEndFrameMs, StatEndFrameMax, StatAcquireMs, StatView0ArraySlice, StatView1ArraySlice, StatWaitFrames, StatBeginFrames,
    StatAltFrames, StatAltKept, StatAltRestored, StatAltPosesFixed, StatAltNoImage, StatAltNoCopyYet, StatAltCopyFailed,
    StatCount
};
static double totals[StatCount];
static XrPose lastPose[2];
static bool havePose[2];

static char detail[4096];
static int detailLength;
static int detailFramesLeft = 3;

static double NowMs()
{
    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * clockToMs;
}

static void Lock() { if (statsLockReady) EnterCriticalSection(&statsLock); }
static void Unlock() { if (statsLockReady) LeaveCriticalSection(&statsLock); }

static void AddTime(Stat sum, Stat max, double ms)
{
    totals[sum] += ms;
    if (ms > totals[max])
        totals[max] = ms;
}

static bool SamePose(const XrPose& first, const XrPose& second)
{
    return memcmp(&first, &second, sizeof(XrPose)) == 0;
}

static void Detail(const char* format, ...)
{
    if (detailLength >= (int)sizeof(detail) - 1)
        return;
    va_list arguments;
    va_start(arguments, format);
    int written = vsnprintf(detail + detailLength, sizeof(detail) - detailLength, format, arguments);
    va_end(arguments);
    if (written > 0)
        detailLength += written < (int)sizeof(detail) - detailLength ? written : (int)sizeof(detail) - detailLength - 1;
}

//------------------------------------------------------------------------------------------------------------------------------------
// Pose math. OpenXR is right-handed (z back), Unity left-handed (z forward): between the two, z flips, and a rotation's x and y.

static XrQuaternion Multiply(const XrQuaternion& a, const XrQuaternion& b)
{
    XrQuaternion r;
    r.w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z;
    r.x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y;
    r.y = a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x;
    r.z = a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w;
    return r;
}

static XrQuaternion Inverse(const XrQuaternion& q)
{
    XrQuaternion r = { -q.x, -q.y, -q.z, q.w };
    return r;
}

static XrVector3 Rotate(const XrQuaternion& q, const XrVector3& v)
{
    XrQuaternion p = { v.x, v.y, v.z, 0.0f };
    XrQuaternion r = Multiply(Multiply(q, p), Inverse(q));
    XrVector3 out = { r.x, r.y, r.z };
    return out;
}

static XrPose Flip(const XrPose& pose)
{
    XrPose flipped;
    flipped.position = { pose.position.x, pose.position.y, -pose.position.z };
    flipped.orientation = { -pose.orientation.x, -pose.orientation.y, pose.orientation.z, pose.orientation.w };
    return flipped;
}

// An eye's pose drawn under the rig where it was (then) into the rig's space now: the same place in the world
static XrPose MovedByRig(const XrPose& poseThen, const float* rigThen, const float* rigNow)
{
    XrQuaternion rotationThen = { rigThen[3], rigThen[4], rigThen[5], rigThen[6] };
    XrQuaternion rotationNow = { rigNow[3], rigNow[4], rigNow[5], rigNow[6] };
    float scaleNow = rigNow[7] > 1e-6f ? rigNow[7] : 1.0f;
    float scaleThen = rigThen[7] > 1e-6f ? rigThen[7] : 1.0f;
    XrQuaternion toNow = Multiply(Inverse(rotationNow), rotationThen);
    XrVector3 offset = { rigThen[0] - rigNow[0], rigThen[1] - rigNow[1], rigThen[2] - rigNow[2] };
    XrVector3 shift = Rotate(Inverse(rotationNow), offset);

    XrPose unity = Flip(poseThen);
    XrVector3 position = { unity.position.x * scaleThen, unity.position.y * scaleThen, unity.position.z * scaleThen };
    XrVector3 turned = Rotate(toNow, position);
    XrPose moved;
    moved.orientation = Multiply(toNow, unity.orientation);
    moved.position = { (turned.x + shift.x) / scaleNow, (turned.y + shift.y) / scaleNow, (turned.z + shift.z) / scaleNow };
    return Flip(moved);
}

//------------------------------------------------------------------------------------------------------------------------------------
// Images

static bool EnsureCopy(KeptEye& eye, ID3D11Texture2D* like)
{
    D3D11_TEXTURE2D_DESC desc;
    like->GetDesc(&desc);
    if (eye.copy && eye.desc.Width == desc.Width && eye.desc.Height == desc.Height && eye.desc.Format == desc.Format
        && eye.desc.ArraySize == desc.ArraySize && eye.desc.MipLevels == desc.MipLevels && eye.desc.SampleDesc.Count == desc.SampleDesc.Count)
        return true;
    if (eye.copy)
    {
        eye.copy->Release();
        eye.copy = nullptr;
    }
    eye.copyValid = false;
    ID3D11Device* device = nullptr;
    like->GetDevice(&device);
    if (!device)
        return false;
    if (!context)
        device->GetImmediateContext(&context);
    D3D11_TEXTURE2D_DESC ours = desc;
    ours.Usage = D3D11_USAGE_DEFAULT;
    ours.BindFlags = 0;
    ours.CPUAccessFlags = 0;
    ours.MiscFlags = 0;
    HRESULT created = device->CreateTexture2D(&ours, nullptr, &eye.copy);
    device->Release();
    if (FAILED(created) || !eye.copy)
    {
        eye.copy = nullptr;
        return false;
    }
    eye.desc = desc;
    return true;
}

// Before Unity hands an eye's image back on a frame the game drew one eye: keep the drawn one, give the other its last drawing
static void BeforeRelease(Swapchain* swapchain)
{
    if (frameEye < 0 || !swapchain)
        return;
    int eye = swapchain->handle == eyeSwapchain[0] ? 0 : swapchain->handle == eyeSwapchain[1] ? 1 : -1;
    if (eye < 0)
        return;
    if (swapchain->lastIndex >= swapchain->imageCount || !swapchain->images[swapchain->lastIndex])
    {
        totals[StatAltNoImage] += 1;
        return;
    }
    ID3D11Texture2D* image = swapchain->images[swapchain->lastIndex];
    KeptEye& kept = keptEyes[eye];
    if (!EnsureCopy(kept, image) || !context)
    {
        totals[StatAltCopyFailed] += 1;
        return;
    }
    if (eye == frameEye)
    {
        context->CopyResource(kept.copy, image);
        kept.copyValid = true;
        kept.keptThisFrame = true;
        totals[StatAltKept] += 1;
    }
    else if (kept.copyValid)
    {
        context->CopyResource(image, kept.copy);
        kept.keptThisFrame = true;
        totals[StatAltRestored] += 1;
    }
    else
    {
        totals[StatAltNoCopyYet] += 1;
    }
}

//------------------------------------------------------------------------------------------------------------------------------------

static XrResult OurWaitFrame(XrSession session, const void* frameWaitInfo, void* frameState)
{
    double start = NowMs();
    XrResult result = runtimeWaitFrame(session, frameWaitInfo, frameState);
    double took = NowMs() - start;
    Lock();
    AddTime(StatWaitFrameMs, StatWaitFrameMax, took);
    totals[StatWaitFrames] += 1;
    Unlock();
    return result;
}

static XrResult OurBeginFrame(XrSession session, const void* frameBeginInfo)
{
    double start = NowMs();
    XrResult result = runtimeBeginFrame(session, frameBeginInfo);
    double took = NowMs() - start;
    Lock();
    AddTime(StatBeginFrameMs, StatBeginFrameMax, took);
    totals[StatBeginFrames] += 1;
    Unlock();
    return result;
}

static XrResult OurEnumerateImages(XrSwapchain handle, uint32_t capacity, uint32_t* count, void* images)
{
    XrResult result = runtimeEnumerateImages(handle, capacity, count, images);
    if (result != 0 || !images || capacity == 0 || !count)
        return result;
    Lock();
    __try
    {
        XrSwapchainImageD3D11* d3d11Images = (XrSwapchainImageD3D11*)images;
        Swapchain* swapchain = SwapchainOf(handle, true);
        if (swapchain && d3d11Images[0].type == XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR)
        {
            uint32_t filled = *count < capacity ? *count : capacity;
            swapchain->imageCount = filled < MAX_IMAGES ? filled : MAX_IMAGES;
            for (uint32_t index = 0; index < swapchain->imageCount; index++)
                swapchain->images[index] = d3d11Images[index].texture;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }
    Unlock();
    return result;
}

static XrResult OurAcquireImage(XrSwapchain handle, const void* acquireInfo, uint32_t* index)
{
    double start = NowMs();
    XrResult result = runtimeAcquireImage(handle, acquireInfo, index);
    double took = NowMs() - start;
    Lock();
    totals[StatAcquireMs] += took;
    totals[StatAcquires] += 1;
    Swapchain* swapchain = SwapchainOf(handle, true);
    if (swapchain)
    {
        swapchain->acquired++;
        if (index && result == 0)
            swapchain->lastIndex = *index;
    }
    Unlock();
    return result;
}

static XrResult OurWaitImage(XrSwapchain handle, const void* waitInfo)
{
    double start = NowMs();
    XrResult result = runtimeWaitImage(handle, waitInfo);
    double took = NowMs() - start;
    Lock();
    AddTime(StatWaitImageMs, StatWaitImageMax, took);
    Unlock();
    return result;
}

static XrResult OurReleaseImage(XrSwapchain handle, const void* releaseInfo)
{
    Lock();
    __try
    {
        Swapchain* swapchain = SwapchainOf(handle, true);
        BeforeRelease(swapchain);
        totals[StatReleases] += 1;
        if (swapchain)
            swapchain->released++;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        totals[StatAltCopyFailed] += 1;
    }
    Unlock();
    return runtimeReleaseImage(handle, releaseInfo);
}

// Watching, and on an alternate-eye frame the eye left out's view rewritten: returns the frame end info to hand on
static const XrFrameEndInfo* LookAtFrame(const XrFrameEndInfo* frameEndInfo)
{
    const XrFrameEndInfo* handOn = frameEndInfo;
    totals[StatFrames] += 1;
    int taken = 0;
    for (int index = 0; index < swapchainCount; index++)
        taken += swapchains[index].acquired > 0 ? 1 : 0;
    totals[StatSwapchainsTaken] += taken;
    bool describe = detailFramesLeft > 0;
    if (describe)
    {
        detailFramesLeft--;
        Detail("frame: %u layers, eye drawn %d; swapchains taken:", frameEndInfo ? frameEndInfo->layerCount : 0, frameEye);
        for (int index = 0; index < swapchainCount; index++)
        {
            if (swapchains[index].acquired > 0)
                Detail(" %llx (acquired %d released %d image %u of %u)", (unsigned long long)swapchains[index].handle,
                    swapchains[index].acquired, swapchains[index].released, swapchains[index].lastIndex, swapchains[index].imageCount);
        }
        Detail("\n");
    }
    if (!frameEndInfo || !frameEndInfo->layers)
        return handOn;
    for (uint32_t layer = 0; layer < frameEndInfo->layerCount; layer++)
    {
        const XrLayerHeader* header = frameEndInfo->layers[layer];
        if (!header)
            continue;
        totals[StatLayers] += 1;
        if (describe)
            Detail("  layer %u: type %d flags %llx space %llx\n", layer, header->type, (unsigned long long)header->layerFlags,
                (unsigned long long)header->space);
        if (header->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION)
            continue;
        const XrLayerProjection* projection = (const XrLayerProjection*)header;
        totals[StatProjectionLayers] += 1;
        totals[StatViews] += projection->viewCount;
        if (projection->viewCount >= 2 && projection->views
            && projection->views[0].subImage.swapchain == projection->views[1].subImage.swapchain)
            totals[StatViewsShareSwapchain] += 1;
        for (uint32_t view = 0; view < projection->viewCount && view < 2 && projection->views; view++)
        {
            const XrProjectionView& projectionView = projection->views[view];
            Swapchain* swapchain = SwapchainOf(projectionView.subImage.swapchain, false);
            if (swapchain && swapchain->acquired > 0)
                totals[view == 0 ? StatView0Acquired : StatView1Acquired] += 1;
            if (havePose[view] && SamePose(lastPose[view], projectionView.pose))
                totals[view == 0 ? StatView0SamePose : StatView1SamePose] += 1;
            lastPose[view] = projectionView.pose;
            havePose[view] = true;
            totals[view == 0 ? StatView0ArraySlice : StatView1ArraySlice] = projectionView.subImage.imageArrayIndex;
            if (describe)
                Detail("    view %u: swapchain %llx rect %d,%d %dx%d slice %u pose (%.3f %.3f %.3f %.3f) (%.3f %.3f %.3f) fov %.3f %.3f %.3f %.3f\n",
                    view, (unsigned long long)projectionView.subImage.swapchain, projectionView.subImage.imageRect.x,
                    projectionView.subImage.imageRect.y, projectionView.subImage.imageRect.width, projectionView.subImage.imageRect.height,
                    projectionView.subImage.imageArrayIndex, projectionView.pose.orientation.x, projectionView.pose.orientation.y,
                    projectionView.pose.orientation.z, projectionView.pose.orientation.w, projectionView.pose.position.x,
                    projectionView.pose.position.y, projectionView.pose.position.z, projectionView.fov.angleLeft,
                    projectionView.fov.angleRight, projectionView.fov.angleUp, projectionView.fov.angleDown);
        }
        // The first projection layer with both eyes: learn which swapchain is which eye, and on an alternate-eye frame rewrite it
        if (projection->viewCount != 2 || !projection->views || handOn != frameEndInfo)
            continue;
        eyeSwapchain[0] = projection->views[0].subImage.swapchain;
        eyeSwapchain[1] = projection->views[1].subImage.swapchain;
        if (frameEye < 0 || frameEndInfo->layerCount > MAX_LAYERS)
            continue;
        totals[StatAltFrames] += 1;
        int drawn = frameEye;
        int leftOut = 1 - frameEye;
        const float* rigNow = frameRigSlot >= 0 ? rigSlots[frameRigSlot] : nullptr;
        // The drawn eye's pose goes with the copy just kept of it (none kept = neither is good for later)
        KeptEye& drawnEye = keptEyes[drawn];
        drawnEye.pose = projection->views[drawn].pose;
        drawnEye.fov = projection->views[drawn].fov;
        if (rigNow)
            memcpy(drawnEye.rig, rigNow, sizeof(drawnEye.rig));
        drawnEye.poseValid = rigNow != nullptr && drawnEye.keptThisFrame;
        KeptEye& outEye = keptEyes[leftOut];
        if (!outEye.poseValid || !outEye.keptThisFrame || !rigNow)
            continue;
        rewrittenViews[0] = projection->views[0];
        rewrittenViews[1] = projection->views[1];
        rewrittenViews[leftOut].pose = MovedByRig(outEye.pose, outEye.rig, rigNow);
        rewrittenViews[leftOut].fov = outEye.fov;
        rewrittenProjection = *projection;
        rewrittenProjection.views = rewrittenViews;
        for (uint32_t index = 0; index < frameEndInfo->layerCount; index++)
            rewrittenLayers[index] = index == layer ? (const XrLayerHeader*)&rewrittenProjection : frameEndInfo->layers[index];
        rewrittenInfo = *frameEndInfo;
        rewrittenInfo.layers = rewrittenLayers;
        handOn = &rewrittenInfo;
        totals[StatAltPosesFixed] += 1;
        if (describe)
            Detail("    eye %d left out: sent with its pose from when it was drawn (%.3f %.3f %.3f %.3f) (%.3f %.3f %.3f)\n", leftOut,
                rewrittenViews[leftOut].pose.orientation.x, rewrittenViews[leftOut].pose.orientation.y, rewrittenViews[leftOut].pose.orientation.z,
                rewrittenViews[leftOut].pose.orientation.w, rewrittenViews[leftOut].pose.position.x, rewrittenViews[leftOut].pose.position.y,
                rewrittenViews[leftOut].pose.position.z);
    }
    return handOn;
}

static XrResult OurEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo)
{
    const XrFrameEndInfo* handOn = frameEndInfo;
    Lock();
    __try
    {
        handOn = LookAtFrame(frameEndInfo);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        handOn = frameEndInfo;
    }
    for (int index = 0; index < swapchainCount; index++)
    {
        swapchains[index].acquired = 0;
        swapchains[index].released = 0;
    }
    // The next frame is both eyes unless the game says otherwise
    keptEyes[0].keptThisFrame = false;
    keptEyes[1].keptThisFrame = false;
    frameEye = -1;
    frameRigSlot = -1;
    Unlock();

    double start = NowMs();
    XrResult result = runtimeEndFrame(session, handOn);
    double took = NowMs() - start;
    Lock();
    AddTime(StatEndFrameMs, StatEndFrameMax, took);
    Unlock();
    return result;
}

//------------------------------------------------------------------------------------------------------------------------------------
// The game's word on the frame, on the render thread in order with the frame's own commands (GL.IssuePluginEvent from LateUpdate,
// before its cameras render and so before Unity hands the eye images back): id = 0x5A000000 | eye << 8 | rig slot

static void __stdcall OnRenderEvent(int eventId)
{
    if (((unsigned)eventId >> 24) != 0x5A)
        return;
    Lock();
    frameEye = (eventId >> 8) & 0xFF;
    if (frameEye > 1)
        frameEye = -1;
    frameRigSlot = eventId & 0xFF;
    if (frameRigSlot >= RIG_SLOTS)
        frameRigSlot = -1;
    Unlock();
}

//------------------------------------------------------------------------------------------------------------------------------------

template <typename T>
static void Wrap(PFN_xrVoidFunction* function, T& runtime, T ours, LONG bit)
{
    PFN_xrVoidFunction found = *function;
    if (found == (PFN_xrVoidFunction)ours)
        return;
    runtime = (T)found;
    *function = (PFN_xrVoidFunction)ours;
    InterlockedOr(&hookedFunctions, bit);
}

static XrResult OurGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function)
{
    XrResult result = nextGetInstanceProcAddr(instance, name, function);
    if (result != 0 || !name || !function || !*function)
        return result;
    if (strcmp(name, "xrEndFrame") == 0)
        Wrap(function, runtimeEndFrame, (PFN_EndFrame)OurEndFrame, 1);
    else if (strcmp(name, "xrWaitFrame") == 0)
        Wrap(function, runtimeWaitFrame, (PFN_WaitFrame)OurWaitFrame, 2);
    else if (strcmp(name, "xrBeginFrame") == 0)
        Wrap(function, runtimeBeginFrame, (PFN_BeginFrame)OurBeginFrame, 4);
    else if (strcmp(name, "xrAcquireSwapchainImage") == 0)
        Wrap(function, runtimeAcquireImage, (PFN_AcquireImage)OurAcquireImage, 8);
    else if (strcmp(name, "xrWaitSwapchainImage") == 0)
        Wrap(function, runtimeWaitImage, (PFN_WaitImage)OurWaitImage, 16);
    else if (strcmp(name, "xrReleaseSwapchainImage") == 0)
        Wrap(function, runtimeReleaseImage, (PFN_ReleaseImage)OurReleaseImage, 32);
    else if (strcmp(name, "xrEnumerateSwapchainImages") == 0)
        Wrap(function, runtimeEnumerateImages, (PFN_EnumerateImages)OurEnumerateImages, 64);
    return result;
}

//------------------------------------------------------------------------------------------------------------------------------------

extern "C" __declspec(dllexport) void* XrWatch_Hook(void* next)
{
    if (!statsLockReady)
    {
        InitializeCriticalSection(&statsLock);
        statsLockReady = true;
        LARGE_INTEGER frequency;
        QueryPerformanceFrequency(&frequency);
        clockToMs = 1000.0 / (double)frequency.QuadPart;
    }
    nextGetInstanceProcAddr = (PFN_GetInstanceProcAddr)next;
    return (void*)OurGetInstanceProcAddr;
}

extern "C" __declspec(dllexport) int XrWatch_Hooked()
{
    return (int)hookedFunctions;
}

// The totals since the last call (then zeroed), in Stat order
extern "C" __declspec(dllexport) int XrWatch_Stats(double* out, int max)
{
    if (!out || max <= 0)
        return 0;
    Lock();
    int count = max < StatCount ? max : StatCount;
    for (int index = 0; index < count; index++)
        out[index] = totals[index];
    for (int index = 0; index < StatCount; index++)
    {
        if (index != StatView0ArraySlice && index != StatView1ArraySlice)
            totals[index] = 0;
    }
    Unlock();
    return count;
}

// The first frames' layers and views in words (once), and again on request
extern "C" __declspec(dllexport) int XrWatch_Detail(char* out, int max)
{
    if (!out || max <= 0)
        return 0;
    Lock();
    int length = detailLength < max - 1 ? detailLength : max - 1;
    memcpy(out, detail, length);
    out[length] = 0;
    detailLength = 0;
    Unlock();
    return length;
}

extern "C" __declspec(dllexport) void XrWatch_DescribeNext(int frames)
{
    Lock();
    detailFramesLeft = frames;
    Unlock();
}

// Alternate eyes: the rig's world pose for a frame (Unity's space: position xyz, rotation xyzw, uniform scale), main thread,
// before that frame's render event names the slot
extern "C" __declspec(dllexport) void XrAlt_SetRig(int slot, float px, float py, float pz, float qx, float qy, float qz, float qw, float scale)
{
    if (slot < 0 || slot >= RIG_SLOTS)
        return;
    Lock();
    float* rig = rigSlots[slot];
    rig[0] = px; rig[1] = py; rig[2] = pz; rig[3] = qx; rig[4] = qy; rig[5] = qz; rig[6] = qw; rig[7] = scale;
    Unlock();
}

extern "C" __declspec(dllexport) void* XrAlt_GetRenderEventFunc()
{
    return (void*)OnRenderEvent;
}

// Our copies dropped (leaving alternate eyes): the next time starts clean
extern "C" __declspec(dllexport) void XrAlt_Forget()
{
    Lock();
    for (int eye = 0; eye < 2; eye++)
    {
        keptEyes[eye].copyValid = false;
        keptEyes[eye].poseValid = false;
    }
    Unlock();
}
