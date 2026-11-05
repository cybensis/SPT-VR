// SPTVRGpuProbe.dll - GPU timing for SPT-VR's StereoCostProbe (measurement only).
// Unity calls OnRenderEvent on its render thread at each plugin event the probe queues (camera / light events
// plus frame markers). Each event stamps a D3D11 timestamp query on the immediate context and a CPU clock; a
// frame's queries are read back a few frames later without stalling (D3D11_ASYNC_GETDATA_DONOTFLUSH) and handed
// to C# as (event id, cpu ms, gpu ms) relative to the frame start. A frame whose GPU half fails still delivers
// its CPU half (gpu = NaN), and every failure is counted by reason (GpuProbe_Stats).
// Each event also takes the render thread's CPU cycle count (QueryThreadCycleTime: cycles the thread actually ran,
// waits not counted), turned into ms by the TSC rate (calibrated against QueryPerformanceCounter): busy = work, while
// the cpu clock is wall time and includes the render thread waiting for the main thread's commands.
#include <windows.h>
#include <d3d11.h>
#include <intrin.h>
#include <limits>

#define GP_EXPORT extern "C" __declspec(dllexport)

static const int kRing = 10;       // frames in flight
static const int kMarkers = 1024;  // events per frame (the probe's breakdown brackets every buffer / effect)
static const int kResults = 64;    // finished frames waiting for C#
static const int kFrameStart = 1, kFrameEnd = 99, kRelease = 250, kDiscard = 251, kHarvest = 252;

struct Slot
{
    ID3D11Query* disjoint = nullptr;
    ID3D11Query* ts[kMarkers] = {};
    int ids[kMarkers] = {};
    LONGLONG qpc[kMarkers] = {};
    ULONG64 cycles[kMarkers] = {};
    int count = 0;
    int state = 0;                 // 0 free, 1 recording, 2 waiting for the GPU
};

struct Result
{
    int count;
    int ids[kMarkers];
    double cpuMs[kMarkers];
    float gpuMs[kMarkers];
    float busyMs[kMarkers];
};

// failure / acceptance counters, read by C#
enum { StNotReady, StDisjoint, StTsFail, StGpuOk, StCpuOnly, StBadDelta, StCount };

static ID3D11Device* gDev = nullptr;
static ID3D11DeviceContext* gCtx = nullptr;
static Slot gSlots[kRing];
static int gCur = -1, gNext = 0;
static LARGE_INTEGER gQpcFreq;
static Result gRes[kResults];
static int gResHead = 0, gResCount = 0;
static SRWLOCK gLock = SRWLOCK_INIT;
static volatile LONG gStats[StCount] = {};
static volatile LONG gDropped = 0;
static volatile LONGLONG gLastFreq = 0;
static volatile LONG gRenderThreadId = 0;
static double gTscPerMs = 0;             // TSC ticks a millisecond, from the first frame start on (render thread)
static LONGLONG gCalibrationQpc = 0;
static unsigned __int64 gCalibrationTsc = 0;

// The TSC rate against QueryPerformanceCounter over everything since the first frame (QueryThreadCycleTime counts TSC ticks)
static void Calibrate()
{
    unsigned __int64 tsc = __rdtsc();
    LARGE_INTEGER q; QueryPerformanceCounter(&q);
    if (gCalibrationQpc == 0) { gCalibrationQpc = q.QuadPart; gCalibrationTsc = tsc; return; }
    double ms = (double)(q.QuadPart - gCalibrationQpc) * 1000.0 / (double)gQpcFreq.QuadPart;
    if (ms > 200.0) gTscPerMs = (double)(tsc - gCalibrationTsc) / ms;
}

static void ReleaseAll()
{
    for (Slot& s : gSlots)
    {
        if (s.disjoint) { s.disjoint->Release(); s.disjoint = nullptr; }
        for (ID3D11Query*& q : s.ts) if (q) { q->Release(); q = nullptr; }
        s.count = 0; s.state = 0;
    }
    if (gCtx) { gCtx->Release(); gCtx = nullptr; }
    if (gDev) { gDev->Release(); gDev = nullptr; }
    gCur = -1;
}

// Device methods are free-threaded, so the queries can be made on the main thread; only the context is
// confined to the render thread (OnRenderEvent).
GP_EXPORT int GpuProbe_Init(void* anyD3D11Resource)
{
    if (gDev) return 1;
    if (!anyD3D11Resource) return 0;
    ((ID3D11Resource*)anyD3D11Resource)->GetDevice(&gDev);
    if (!gDev) return 0;
    gDev->GetImmediateContext(&gCtx);
    QueryPerformanceFrequency(&gQpcFreq);
    D3D11_QUERY_DESC dj = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 }, ts = { D3D11_QUERY_TIMESTAMP, 0 };
    for (Slot& s : gSlots)
    {
        if (FAILED(gDev->CreateQuery(&dj, &s.disjoint))) { ReleaseAll(); return 0; }
        for (ID3D11Query*& q : s.ts)
            if (FAILED(gDev->CreateQuery(&ts, &q))) { ReleaseAll(); return 0; }
    }
    return 1;
}

// Hands one slot to C#: CPU times always, GPU times only when gpuOk.
static void Push(Slot& s, const UINT64* gpuTicks, UINT64 freq, bool gpuOk)
{
    static Result r;               // render thread only
    r.count = s.count;
    const float nan = std::numeric_limits<float>::quiet_NaN();
    for (int i = 0; i < s.count; i++)
    {
        r.ids[i] = s.ids[i];
        r.cpuMs[i] = (double)(s.qpc[i] - s.qpc[0]) * 1000.0 / (double)gQpcFreq.QuadPart;
        r.gpuMs[i] = gpuOk ? (float)((double)(gpuTicks[i] - gpuTicks[0]) * 1000.0 / (double)freq) : nan;
        r.busyMs[i] = gTscPerMs > 0 ? (float)((double)(s.cycles[i] - s.cycles[0]) / gTscPerMs) : nan;
    }
    InterlockedIncrement(&gStats[gpuOk ? StGpuOk : StCpuOnly]);
    AcquireSRWLockExclusive(&gLock);
    gRes[(gResHead + gResCount) % kResults] = r;
    if (gResCount < kResults) gResCount++; else gResHead = (gResHead + 1) % kResults;
    ReleaseSRWLockExclusive(&gLock);
}

static void Harvest()
{
    static UINT64 ticks[kMarkers];
    for (int k = 1; k <= kRing; k++)
    {
        Slot& s = gSlots[(gNext + k) % kRing];      // oldest first
        if (s.state != 2) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj;
        if (gCtx->GetData(s.disjoint, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        // AMD answers the disjoint query at once, long before the GPU reaches the frame: wait for the frame's
        // LAST timestamp (in-order, so every earlier one is then done too) before taking the slot.
        UINT64 last;
        HRESULT hr = gCtx->GetData(s.ts[s.count - 1], &last, sizeof(last), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr == S_FALSE) continue;
        s.state = 0;
        // Disjoint = the driver could not promise a steady clock over the frame. Kept anyway when the readings
        // are sane (monotonic, frame < 100 ms) - counted, so the report says how many frames carried the flag.
        if (dj.Disjoint) InterlockedIncrement(&gStats[StDisjoint]);
        bool ok = dj.Frequency > 0;
        if (ok) gLastFreq = (LONGLONG)dj.Frequency;
        for (int i = 0; i < s.count && ok; i++)
            if (gCtx->GetData(s.ts[i], &ticks[i], sizeof(UINT64), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK)
            {
                InterlockedIncrement(&gStats[StTsFail]);
                ok = false;
            }
        if (ok)
            for (int i = 1; i < s.count; i++)
                if (ticks[i] < ticks[i - 1] || (double)(ticks[i] - ticks[0]) > 0.1 * (double)dj.Frequency)
                {
                    InterlockedIncrement(&gStats[StBadDelta]);
                    ok = false;
                    break;
                }
        Push(s, ticks, dj.Frequency, ok);
    }
}

static void Mark(int id)
{
    Slot& s = gSlots[gCur];
    if (s.count >= kMarkers - (id == kFrameEnd ? 0 : 1)) return;   // the last slot is kept for the frame end
    gCtx->End(s.ts[s.count]);
    LARGE_INTEGER q; QueryPerformanceCounter(&q);
    s.qpc[s.count] = q.QuadPart;
    ULONG64 cycles = 0;
    QueryThreadCycleTime(GetCurrentThread(), &cycles);
    s.cycles[s.count] = cycles;
    s.ids[s.count] = id;
    s.count++;
}

static void __stdcall OnRenderEvent(int id)
{
    if (id == kRelease) { ReleaseAll(); return; }
    if (!gCtx) return;
    if (id == kHarvest) { Harvest(); return; }                             // after a run: collect the last frames
    if (id == kDiscard)                                                     // before a run: forget the last run's frames
    {
        for (Slot& s : gSlots) s.state = 0;
        gCur = -1;
        AcquireSRWLockExclusive(&gLock); gResCount = 0; ReleaseSRWLockExclusive(&gLock);
        for (int i = 0; i < StCount; i++) gStats[i] = 0;
        return;
    }
    if (id == kFrameStart)
    {
        gRenderThreadId = (LONG)GetCurrentThreadId();
        Calibrate();
        Harvest();
        if (gCur >= 0 && gSlots[gCur].state == 1) gSlots[gCur].state = 0;   // a frame that never ended
        Slot& s = gSlots[gNext];
        if (s.state == 2)                                                   // GPU still not done after kRing frames
        {
            InterlockedIncrement(&gStats[StNotReady]);
            InterlockedIncrement(&gDropped);
            Push(s, nullptr, 0, false);
        }
        gCur = gNext; gNext = (gNext + 1) % kRing;
        s.count = 0; s.state = 1;
        gCtx->Begin(s.disjoint);
        Mark(id);
        return;
    }
    if (gCur < 0 || gSlots[gCur].state != 1) return;
    Mark(id);
    if (id == kFrameEnd)
    {
        gCtx->End(gSlots[gCur].disjoint);
        gSlots[gCur].state = 2;
        Harvest();                                                          // older frames, while we're here
    }
}

GP_EXPORT void* GpuProbe_GetRenderEventFunc() { return (void*)&OnRenderEvent; }

// Main thread: one finished frame per call; returns the event count, or -1 when none is waiting.
GP_EXPORT int GpuProbe_PopFrame(int* ids, double* cpuMs, float* gpuMs, float* busyMs, int max)
{
    AcquireSRWLockExclusive(&gLock);
    if (gResCount == 0) { ReleaseSRWLockExclusive(&gLock); return -1; }
    Result& r = gRes[gResHead];
    int n = r.count < max ? r.count : max;
    for (int i = 0; i < n; i++) { ids[i] = r.ids[i]; cpuMs[i] = r.cpuMs[i]; gpuMs[i] = r.gpuMs[i]; busyMs[i] = r.busyMs[i]; }
    gResHead = (gResHead + 1) % kResults; gResCount--;
    ReleaseSRWLockExclusive(&gLock);
    return n;
}

GP_EXPORT int GpuProbe_Dropped() { return (int)gDropped; }

// [not ready after kRing frames, disjoint flag, timestamp read failed, GPU ok, CPU only, bad delta]; returns count.
GP_EXPORT int GpuProbe_Stats(int* out, int max)
{
    int n = max < StCount ? max : StCount;
    for (int i = 0; i < n; i++) out[i] = (int)gStats[i];
    return n;
}

GP_EXPORT double GpuProbe_Frequency() { return (double)gLastFreq; }

// TSC ticks a millisecond (0 until calibrated): turns QueryThreadCycleTime counts into ms, on any thread
GP_EXPORT double GpuProbe_TscPerMs() { return gTscPerMs; }

GP_EXPORT int GpuProbe_RenderThreadId() { return (int)gRenderThreadId; }
