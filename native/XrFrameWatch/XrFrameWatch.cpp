// SPTVRXrFrameWatch.dll: watches how Unity's OpenXR plugin hands frames to the runtime, for SPT-VR's cost probe (StereoCostProbe,
// Ctrl+F10: how long xrWaitFrame holds the frame, and on which thread). It sits in front of the OpenXR frame functions through the
// xrGetInstanceProcAddr chain
// Unity lets every feature hook (Source/XR/OpenXRFrameWatch.cs) and only looks: every call goes to the runtime unchanged. It also
// tells SPT-VR's stereo replay (native/StereoReplay) each view's D3D11 images and calls it before an image is released.
//  - xrEndFrame: the layers submitted, each projection view's swapchain, image rect, array slice and pose
//  - xrAcquire/Wait/ReleaseSwapchainImage: which swapchains were taken this frame, and how long the waits were
//  - xrWaitFrame / xrBeginFrame: how long they held the caller
// Native because xrEndFrame and the swapchain calls run on Unity's render thread. Struct layouts follow the OpenXR spec
// (checked against openxr_reflection_full.h in the OpenXR package): x64, natural alignment.
#include <windows.h>
#include <math.h>
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

static_assert(sizeof(XrProjectionView) == 96, "XrCompositionLayerProjectionView layout");
static_assert(offsetof(XrProjectionView, subImage) == 64, "XrSwapchainSubImage offset");
static_assert(offsetof(XrLayerProjection, views) == 40, "XrCompositionLayerProjection layout");
static_assert(offsetof(XrFrameEndInfo, layers) == 32, "XrFrameEndInfo layout");

typedef XrResult (*PFN_EndFrame)(XrSession session, const XrFrameEndInfo* frameEndInfo);
typedef XrResult (*PFN_WaitFrame)(XrSession session, const void* frameWaitInfo, void* frameState);
typedef XrResult (*PFN_BeginFrame)(XrSession session, const void* frameBeginInfo);
typedef XrResult (*PFN_AcquireImage)(XrSwapchain swapchain, const void* acquireInfo, uint32_t* index);
typedef XrResult (*PFN_WaitImage)(XrSwapchain swapchain, const void* waitInfo);
typedef XrResult (*PFN_ReleaseImage)(XrSwapchain swapchain, const void* releaseInfo);
typedef XrResult (*PFN_EnumerateImages)(XrSwapchain swapchain, uint32_t capacity, uint32_t* count, void* images);
typedef void (*PFN_ReleaseHook)(int view, void* texture);

// XrSwapchainImageD3D11KHR: type, next, ID3D11Texture2D* (the stride of the image array)
static const int32_t XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR = 1000027001;
struct XrImageD3D11 { int32_t type; const void* next; void* texture; };
static_assert(sizeof(XrImageD3D11) == 24, "XrSwapchainImageD3D11KHR layout");

static PFN_GetInstanceProcAddr nextGetInstanceProcAddr;
static PFN_EnumerateImages runtimeEnumerateImages;
static PFN_EndFrame runtimeEndFrame;
static PFN_WaitFrame runtimeWaitFrame;
static PFN_BeginFrame runtimeBeginFrame;
static PFN_AcquireImage runtimeAcquireImage;
static PFN_WaitImage runtimeWaitImage;
static PFN_ReleaseImage runtimeReleaseImage;
static volatile LONG hookedFunctions;   // bits: 1 end, 2 wait frame, 4 begin, 8 acquire, 16 wait image, 32 release
static volatile LONG waitFrameThread, beginFrameThread, endFrameThread;   // the OS thread each was last called on

static CRITICAL_SECTION statsLock;
static bool statsLockReady;
static double clockToMs;

// This frame's swapchain takes (reset at xrEndFrame)
static const int MAX_SWAPCHAINS = 16;
struct SwapchainTake { XrSwapchain swapchain; int acquired; int released; uint32_t lastIndex; };
static SwapchainTake frameTakes[MAX_SWAPCHAINS];
static int frameTakeCount;

// Totals since the last XrWatch_Stats call
enum Stat
{
    StatFrames, StatLayers, StatProjectionLayers, StatViews, StatViewsShareSwapchain,
    StatView0Acquired, StatView1Acquired, StatView0SamePose, StatView1SamePose,
    StatAcquires, StatReleases, StatSwapchainsTaken,
    StatWaitFrameMs, StatWaitFrameMax, StatBeginFrameMs, StatBeginFrameMax, StatWaitImageMs, StatWaitImageMax,
    StatEndFrameMs, StatEndFrameMax, StatAcquireMs, StatView0ArraySlice, StatView1ArraySlice, StatWaitFrames, StatBeginFrames,
    StatCount
};
static double totals[StatCount];
static XrPose lastPose[2];
static bool havePose[2];

// For SPT-VR's stereo replay (native/StereoReplay): each swapchain's D3D11 images, the swapchain each view was submitted from
// (the last xrEndFrame), and a call before an image goes back to the runtime
static const int MAX_IMAGES = 8;
struct SwapchainImages { XrSwapchain swapchain; int count; void* textures[MAX_IMAGES]; };
static SwapchainImages swapchainImages[MAX_SWAPCHAINS];
static int swapchainImageCount;
static XrSwapchain viewSwapchain[2];
static PFN_ReleaseHook releaseHook;

static SwapchainImages* ImagesOf(XrSwapchain swapchain)
{
    for (int index = 0; index < swapchainImageCount; index++)
    {
        if (swapchainImages[index].swapchain == swapchain)
            return &swapchainImages[index];
    }
    return nullptr;
}

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

static SwapchainTake* TakeOf(XrSwapchain swapchain)
{
    for (int index = 0; index < frameTakeCount; index++)
    {
        if (frameTakes[index].swapchain == swapchain)
            return &frameTakes[index];
    }
    if (frameTakeCount >= MAX_SWAPCHAINS)
        return nullptr;
    SwapchainTake* take = &frameTakes[frameTakeCount++];
    take->swapchain = swapchain;
    take->acquired = 0;
    take->released = 0;
    take->lastIndex = 0;
    return take;
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

static XrResult OurWaitFrame(XrSession session, const void* frameWaitInfo, void* frameState)
{
    double start = NowMs();
    XrResult result = runtimeWaitFrame(session, frameWaitInfo, frameState);
    double took = NowMs() - start;
    waitFrameThread = (LONG)GetCurrentThreadId();
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
    beginFrameThread = (LONG)GetCurrentThreadId();
    Lock();
    AddTime(StatBeginFrameMs, StatBeginFrameMax, took);
    totals[StatBeginFrames] += 1;
    Unlock();
    return result;
}

static XrResult OurAcquireImage(XrSwapchain swapchain, const void* acquireInfo, uint32_t* index)
{
    double start = NowMs();
    XrResult result = runtimeAcquireImage(swapchain, acquireInfo, index);
    double took = NowMs() - start;
    Lock();
    totals[StatAcquireMs] += took;
    totals[StatAcquires] += 1;
    SwapchainTake* take = TakeOf(swapchain);
    if (take)
    {
        take->acquired++;
        if (index)
            take->lastIndex = *index;
    }
    Unlock();
    return result;
}

static XrResult OurWaitImage(XrSwapchain swapchain, const void* waitInfo)
{
    double start = NowMs();
    XrResult result = runtimeWaitImage(swapchain, waitInfo);
    double took = NowMs() - start;
    Lock();
    AddTime(StatWaitImageMs, StatWaitImageMax, took);
    Unlock();
    return result;
}

static XrResult OurReleaseImage(XrSwapchain swapchain, const void* releaseInfo)
{
    PFN_ReleaseHook hook = releaseHook;
    if (hook)
    {
        void* texture = nullptr;
        int view = -1;
        Lock();
        for (int candidate = 0; candidate < 2; candidate++)
        {
            if (viewSwapchain[candidate] != 0 && viewSwapchain[candidate] == swapchain)
                view = candidate;
        }
        SwapchainTake* take = view >= 0 ? TakeOf(swapchain) : nullptr;
        SwapchainImages* images = view >= 0 ? ImagesOf(swapchain) : nullptr;
        if (take && take->acquired > 0 && images && take->lastIndex < (uint32_t)images->count)
            texture = images->textures[take->lastIndex];
        Unlock();
        if (texture)
            hook(view, texture);
    }
    XrResult result = runtimeReleaseImage(swapchain, releaseInfo);
    Lock();
    totals[StatReleases] += 1;
    SwapchainTake* take = TakeOf(swapchain);
    if (take)
        take->released++;
    Unlock();
    return result;
}

//------------------------------------------------------------------------------------------------------------------------------------
// The right eye's submitted pose, for SPT-VR's stereo replay: Unity then draws only the left eye, and the pose it submits for the right
// view need not be the one the replay drew it from (the compositor turns the image by the difference). While on, view 1's pose = view
// 0's + the eyes' offset, taken from the frames before (Unity drew both).

static bool fixRightPose;
static bool haveEyeOffset;
static XrPose eyeOffset;                // the right eye in the left eye's frame
static double poseStats[4];             // frames fixed, largest position / angle difference (cm / deg), frames off by > 0.5 cm or 0.5 deg
static XrProjectionView fixedViews[2];
static XrLayerProjection fixedLayer;
static const XrLayerHeader* fixedLayers[16];
static XrFrameEndInfo fixedInfo;

static XrQuaternion QMul(const XrQuaternion& a, const XrQuaternion& b)
{
    return { a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z };
}
static XrQuaternion QConj(const XrQuaternion& q) { return { -q.x, -q.y, -q.z, q.w }; }
static XrVector3 QRot(const XrQuaternion& q, const XrVector3& v)
{
    XrVector3 t = { 2.0f * (q.y * v.z - q.z * v.y), 2.0f * (q.z * v.x - q.x * v.z), 2.0f * (q.x * v.y - q.y * v.x) };
    return { v.x + q.w * t.x + (q.y * t.z - q.z * t.y), v.y + q.w * t.y + (q.z * t.x - q.x * t.z), v.z + q.w * t.z + (q.x * t.y - q.y * t.x) };
}
static XrPose Relative(const XrPose& from, const XrPose& to)
{
    XrQuaternion inverse = QConj(from.orientation);
    XrVector3 delta = { to.position.x - from.position.x, to.position.y - from.position.y, to.position.z - from.position.z };
    return { QMul(inverse, to.orientation), QRot(inverse, delta) };
}
static XrPose Compose(const XrPose& base, const XrPose& offset)
{
    XrVector3 moved = QRot(base.orientation, offset.position);
    return { QMul(base.orientation, offset.orientation), { base.position.x + moved.x, base.position.y + moved.y, base.position.z + moved.z } };
}

// The frame to submit: as Unity made it, or with view 1's pose fixed (under the stats lock)
static const XrFrameEndInfo* RightPoseFixed(const XrFrameEndInfo* frameEndInfo)
{
    if (!frameEndInfo || !frameEndInfo->layers || frameEndInfo->layerCount > 16)
        return frameEndInfo;
    for (uint32_t layer = 0; layer < frameEndInfo->layerCount; layer++)
    {
        const XrLayerHeader* header = frameEndInfo->layers[layer];
        if (!header || header->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION)
            continue;
        const XrLayerProjection* projection = (const XrLayerProjection*)header;
        if (projection->viewCount != 2 || !projection->views)
            return frameEndInfo;
        if (!fixRightPose)
        {
            eyeOffset = Relative(projection->views[0].pose, projection->views[1].pose);
            haveEyeOffset = true;
            return frameEndInfo;
        }
        if (!haveEyeOffset)
            return frameEndInfo;
        XrPose fixed = Compose(projection->views[0].pose, eyeOffset);
        const XrPose& submitted = projection->views[1].pose;
        float dx = fixed.position.x - submitted.position.x, dy = fixed.position.y - submitted.position.y, dz = fixed.position.z - submitted.position.z;
        double centimetres = sqrt(dx * dx + dy * dy + dz * dz) * 100.0;
        double dot = fabs(fixed.orientation.x * submitted.orientation.x + fixed.orientation.y * submitted.orientation.y
            + fixed.orientation.z * submitted.orientation.z + fixed.orientation.w * submitted.orientation.w);
        double degrees = 2.0 * acos(dot < 1.0 ? dot : 1.0) * 57.29577951;
        poseStats[0] += 1;
        if (centimetres > poseStats[1]) poseStats[1] = centimetres;
        if (degrees > poseStats[2]) poseStats[2] = degrees;
        if (centimetres > 0.5 || degrees > 0.5) poseStats[3] += 1;
        fixedViews[0] = projection->views[0];
        fixedViews[1] = projection->views[1];
        fixedViews[1].pose = fixed;
        fixedLayer = *projection;
        fixedLayer.views = fixedViews;
        for (uint32_t index = 0; index < frameEndInfo->layerCount; index++)
            fixedLayers[index] = index == layer ? (const XrLayerHeader*)&fixedLayer : frameEndInfo->layers[index];
        fixedInfo = *frameEndInfo;
        fixedInfo.layers = fixedLayers;
        return &fixedInfo;
    }
    return frameEndInfo;
}

static XrResult OurEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo)
{
    endFrameThread = (LONG)GetCurrentThreadId();
    const XrFrameEndInfo* submit = frameEndInfo;
    Lock();
    __try
    {
        totals[StatFrames] += 1;
        totals[StatSwapchainsTaken] += frameTakeCount;
        bool describe = detailFramesLeft > 0;
        if (describe)
        {
            detailFramesLeft--;
            Detail("frame: %u layers; swapchains taken:", frameEndInfo ? frameEndInfo->layerCount : 0);
            for (int index = 0; index < frameTakeCount; index++)
                Detail(" %llx (acquired %d released %d image %u)", (unsigned long long)frameTakes[index].swapchain,
                    frameTakes[index].acquired, frameTakes[index].released, frameTakes[index].lastIndex);
            Detail("\n");
        }
        bool firstProjection = true;
        if (frameEndInfo && frameEndInfo->layers)
        {
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
                    if (firstProjection)
                        viewSwapchain[view] = projectionView.subImage.swapchain;
                    SwapchainTake* take = nullptr;
                    for (int index = 0; index < frameTakeCount; index++)
                    {
                        if (frameTakes[index].swapchain == projectionView.subImage.swapchain)
                            take = &frameTakes[index];
                    }
                    if (take && take->acquired > 0)
                        totals[view == 0 ? StatView0Acquired : StatView1Acquired] += 1;
                    if (havePose[view] && SamePose(lastPose[view], projectionView.pose))
                        totals[view == 0 ? StatView0SamePose : StatView1SamePose] += 1;
                    lastPose[view] = projectionView.pose;
                    havePose[view] = true;
                    totals[view == 0 ? StatView0ArraySlice : StatView1ArraySlice] = projectionView.subImage.imageArrayIndex;
                    if (describe)
                        Detail("    view %u: type %d swapchain %llx rect %d,%d %dx%d slice %u pose (%.3f %.3f %.3f %.3f) (%.3f %.3f %.3f) fov %.3f %.3f %.3f %.3f next %p\n",
                            view, projectionView.type, (unsigned long long)projectionView.subImage.swapchain,
                            projectionView.subImage.imageRect.x, projectionView.subImage.imageRect.y,
                            projectionView.subImage.imageRect.width, projectionView.subImage.imageRect.height,
                            projectionView.subImage.imageArrayIndex,
                            projectionView.pose.orientation.x, projectionView.pose.orientation.y, projectionView.pose.orientation.z,
                            projectionView.pose.orientation.w, projectionView.pose.position.x, projectionView.pose.position.y,
                            projectionView.pose.position.z, projectionView.fov.angleLeft, projectionView.fov.angleRight,
                            projectionView.fov.angleUp, projectionView.fov.angleDown, projectionView.next);
                }
                firstProjection = false;
            }
        }
        submit = RightPoseFixed(frameEndInfo);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        submit = frameEndInfo;
    }
    frameTakeCount = 0;
    Unlock();

    double start = NowMs();
    XrResult result = runtimeEndFrame(session, submit);
    double took = NowMs() - start;
    Lock();
    AddTime(StatEndFrameMs, StatEndFrameMax, took);
    Unlock();
    return result;
}

static XrResult OurEnumerateImages(XrSwapchain swapchain, uint32_t capacity, uint32_t* count, void* images)
{
    XrResult result = runtimeEnumerateImages(swapchain, capacity, count, images);
    if (result != 0 || capacity == 0 || !images || !count)
        return result;
    Lock();
    __try
    {
        const XrImageD3D11* first = (const XrImageD3D11*)images;
        if (first->type == XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR)
        {
            SwapchainImages* entry = ImagesOf(swapchain);
            if (!entry && swapchainImageCount < MAX_SWAPCHAINS)
                entry = &swapchainImages[swapchainImageCount++];
            if (entry)
            {
                entry->swapchain = swapchain;
                entry->count = 0;
                for (uint32_t index = 0; index < *count && index < capacity && index < (uint32_t)MAX_IMAGES; index++)
                    entry->textures[entry->count++] = first[index].texture;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }
    Unlock();
    return result;
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

// The D3D11 images (ID3D11Texture2D*) of the swapchain the view was last submitted from: how many, 0 = not known yet
extern "C" __declspec(dllexport) int XrWatch_ViewImages(int view, void** out, int max)
{
    if (view < 0 || view > 1 || !out || max <= 0)
        return 0;
    Lock();
    SwapchainImages* images = viewSwapchain[view] != 0 ? ImagesOf(viewSwapchain[view]) : nullptr;
    int count = images ? (images->count < max ? images->count : max) : 0;
    for (int index = 0; index < count; index++)
        out[index] = images->textures[index];
    Unlock();
    return count;
}

// The view's image taken since the last xrEndFrame (the one this frame draws into), null if none yet
extern "C" __declspec(dllexport) void* XrWatch_ViewAcquiredImage(int view)
{
    if (view < 0 || view > 1)
        return nullptr;
    void* texture = nullptr;
    Lock();
    XrSwapchain swapchain = viewSwapchain[view];
    SwapchainImages* images = swapchain != 0 ? ImagesOf(swapchain) : nullptr;
    for (int index = 0; images && index < frameTakeCount; index++)
    {
        const SwapchainTake& take = frameTakes[index];
        if (take.swapchain == swapchain && take.acquired > take.released && take.lastIndex < (uint32_t)images->count)
            texture = images->textures[take.lastIndex];
    }
    Unlock();
    return texture;
}

// The right view's submitted pose from the left one + the eyes' offset (stereo replay), on / off
extern "C" __declspec(dllexport) void XrWatch_SetFixRightPose(int on)
{
    Lock();
    fixRightPose = on != 0;
    Unlock();
}

// Since the last call (then zeroed): frames fixed, largest position / angle difference (cm / deg), frames off by > 0.5 cm or 0.5 deg
extern "C" __declspec(dllexport) int XrWatch_PoseStats(double* out, int max)
{
    if (!out || max <= 0)
        return 0;
    Lock();
    int count = max < 4 ? max : 4;
    for (int index = 0; index < count; index++)
        out[index] = poseStats[index];
    for (int index = 0; index < 4; index++)
        poseStats[index] = 0;
    Unlock();
    return count;
}

// Called (render thread) with a view's image just before it goes back to the runtime; null = none
extern "C" __declspec(dllexport) void XrWatch_SetReleaseHook(void* hook)
{
    releaseHook = (PFN_ReleaseHook)hook;
}

// The OS threads xrWaitFrame, xrBeginFrame and xrEndFrame were last called on (0 = not yet)
extern "C" __declspec(dllexport) int XrWatch_Threads(int* out, int max)
{
    if (!out || max < 3)
        return 0;
    out[0] = (int)waitFrameThread;
    out[1] = (int)beginFrameThread;
    out[2] = (int)endFrameThread;
    return 3;
}
