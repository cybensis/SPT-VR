// SPTVRXrFrameWatch.dll: watches how Unity's OpenXR plugin hands frames to the runtime, for SPT-VR's cost probe (StereoCostProbe,
// Ctrl+F10: how long xrWaitFrame holds the frame, and on which thread). It sits in front of the OpenXR frame functions through the
// xrGetInstanceProcAddr chain
// Unity lets every feature hook (Source/XR/OpenXRFrameWatch.cs) and only looks: every call goes to the runtime unchanged.
//  - xrEndFrame: the layers submitted, each projection view's swapchain, image rect, array slice and pose
//  - xrAcquire/Wait/ReleaseSwapchainImage: which swapchains were taken this frame, and how long the waits were
//  - xrWaitFrame / xrBeginFrame: how long they held the caller
// Native because xrEndFrame and the swapchain calls run on Unity's render thread. Struct layouts follow the OpenXR spec
// (checked against openxr_reflection_full.h in the OpenXR package): x64, natural alignment.
#include <windows.h>
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

static PFN_GetInstanceProcAddr nextGetInstanceProcAddr;
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
    XrResult result = runtimeReleaseImage(swapchain, releaseInfo);
    Lock();
    totals[StatReleases] += 1;
    SwapchainTake* take = TakeOf(swapchain);
    if (take)
        take->released++;
    Unlock();
    return result;
}

static XrResult OurEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo)
{
    endFrameThread = (LONG)GetCurrentThreadId();
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
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }
    frameTakeCount = 0;
    Unlock();

    double start = NowMs();
    XrResult result = runtimeEndFrame(session, frameEndInfo);
    double took = NowMs() - start;
    Lock();
    AddTime(StatEndFrameMs, StatEndFrameMax, took);
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
