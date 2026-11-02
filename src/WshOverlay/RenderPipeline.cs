using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

// Three threads:
//  - Render thread (Run, paced to the FPS limit): menus, hotkeys, and replay of the latest world drawing
//    re-projected with the camera read *this* frame (3 raw reads), so the drawing never swims.
//  - World worker (Scan rate): everything that reads/modifies the game (scan, shapes, markers, freeze,
//    line of sight, target selection). Its drawing is recorded, not drawn.
//  - Aim loop (Aim update rate): aim assist and recoil control camera writes.
// All game modification stays on the worker; the render thread queues requests (WorkerDo).
internal sealed class DrawRecorder
{
    public readonly List<(Vector3 A,Vector3 B,uint Color)> WorldLines=new(2048);
    public readonly List<(Vector3 P,uint Color,string Text)> WorldTexts=new(128);
    public readonly List<(Vector2 A,Vector2 B,uint Color,float Thickness)> Lines=new();
    public readonly List<(Vector2 P,uint Color,string Text)> Texts=new();
    public readonly List<(Vector2 P,float Radius,uint Color,int Segments,float Thickness)> Circles=new();
    // Screen-space calls (projected with the worker's camera at record time).
    public void AddLine(Vector2 a,Vector2 b,uint color,float thickness)=>Lines.Add((a,b,color,thickness));
    public void AddText(Vector2 p,uint color,string text)=>Texts.Add((p,color,text));
    public void AddCircle(Vector2 p,float radius,uint color,int segments,float thickness)=>Circles.Add((p,radius,color,segments,thickness));
    // World-space calls: projected by the render thread with its current camera.
    public void WorldLine(Vector3 a,Vector3 b,uint color)=>WorldLines.Add((a,b,color));
    public void WorldText(Vector3 p,uint color,string text)=>WorldTexts.Add((p,color,text));
    public void Clear(){WorldLines.Clear();WorldTexts.Clear();Lines.Clear();Texts.Clear();Circles.Clear();}
}

internal sealed partial class GameOverlay
{
    Thread worker,aimThread;volatile bool workerStop;
    volatile int workerRate,aimRate; // measured loops per second (main menu)
    readonly ConcurrentQueue<Action> workerActions=new();
    DrawRecorder recorder=new();                 // worker-only: the frame being recorded
    volatile DrawRecorder publishedDraw;         // latest complete recording (null: nothing to draw)
    // Recorders are reused (a fresh one per world tick was ~15 MB/s of garbage at 250 Hz). Three are
    // enough: one published, one being replayed by the render thread, one being recorded.
    readonly DrawRecorder[] recorderPool=[new(),new(),new()];
    DrawRecorder replaying;                      // render thread: recording it is reading right now
    DrawRecorder NextRecorder()
    {
        var published=publishedDraw;var busy=Volatile.Read(ref replaying);
        foreach(var r in recorderPool)if(r!=published&&r!=busy){r.Clear();return r;}
        return new DrawRecorder(); // unreachable with three; never clear one in use
    }
    volatile bool exitRequested;
    Vector2 displaySize=new(1920,1080);          // written by render, read by worker
    // Camera addresses (FMinimalViewInfo) published by the worker for the render thread's raw reads.
    nint povLocation,povRotation,povFov;
    OverlayWindow window;Direct2D renderer;Gui gui;
    bool closed;long nextFitCheck,nextAliveCheck;

    void WorkerDo(Action action)=>workerActions.Enqueue(action);

    public void Run()
    {
        // No blocking full GCs during play: they stall the render thread for tens of milliseconds.
        System.Runtime.GCSettings.LatencyMode=System.Runtime.GCLatencyMode.SustainedLowLatency;
        timeBeginPeriod(1);DisablePowerThrottling();
        window=new OverlayWindow("Remnant 2 Weakspot Helper");
        float scale=Math.Clamp(OverlayWindow.DpiScale(Mem.Window),1,3);
        window.FitTo(Mem.Window);displaySize=window.Size;
        renderer=new Direct2D(window.Handle,(uint)window.Size.X,(uint)window.Size.Y,14*scale);
        gui=new Gui(renderer){Scale=scale};
        targetFps=ui.FpsLimit;
        Log($"Overlay fitted to game client area: {window.Size.X}x{window.Size.Y} at ({window.Position.X},{window.Position.Y})");
        StartWorker();
        try{while(!closed&&!window.Closed){window.Pump();RenderFrame();}}
        finally{StopWorker();SaveSettings(now:true,remember:false);renderer.Dispose();window.Dispose();}
    }

    void RenderFrame()
    {
        MeasureFps();
        long now=Environment.TickCount64;
        if(now>=nextFitCheck)
        {
            // The overlay must cover exactly the game's client area (re-checked every second for moves/resizes).
            nextFitCheck=now+1000;
            if(window.FitTo(Mem.Window))
            {
                renderer.Resize((uint)window.Size.X,(uint)window.Size.Y);
                Log($"Overlay fitted to game client area: {window.Size.X}x{window.Size.Y} at ({window.Position.X},{window.Position.Y})");
            }
        }
        if(now>=nextAliveCheck)
        {
            nextAliveCheck=now+1000;
            if(Mem.Process.HasExited){Log("Game exited.");closed=true;return;}
        }
        displaySize=window.Size;
        bool relevantFocus=GameFocused();
        HandleHotkeys(relevantFocus);
        SaveSettings();
        if(exitRequested)
        {
            // Stop the worker first so cleanup runs single-threaded, then undo every game modification.
            SaveSettings(now:true); // before RestoreAll, which switches the outline flag off
            StopWorker();RestoreAll();closed=true;
            renderer.Begin();renderer.End();
            return;
        }
        renderer.Begin();
        bool menuShown=false;
        if(relevantFocus)
        {
            ReplayWorld();
            if(menu){DrawWindows();menuShown=true;}
            DrawNotice();
        }
        renderer.End();
        window.SetClickThrough(!(menuShown&&gui.WantsMouse));
        PaceFrame(idle:!relevantFocus);
    }

    void StartWorker()
    {
        worker=new Thread(WorkerLoop){IsBackground=true,Name="WshWorld",Priority=ThreadPriority.Normal};
        worker.Start();
        aimThread=new Thread(AimLoop){IsBackground=true,Name="WshAim",Priority=ThreadPriority.AboveNormal};
        aimThread.Start();
    }
    void StopWorker()
    {
        workerStop=true;
        if(worker!=null&&worker.IsAlive&&Thread.CurrentThread!=worker)worker.Join(3000);
        if(aimThread!=null&&aimThread.IsAlive&&Thread.CurrentThread!=aimThread)aimThread.Join(1000);
    }

    long nextRateLog;
    void WorkerLoop()
    {
        var clock=Stopwatch.StartNew();
        int ticks=0;double rateFrom=0;
        while(!workerStop)
        {
            double start=clock.Elapsed.TotalMilliseconds,period=1000.0/Math.Clamp(ui.WorldHz,10,1000);
            if(start-rateFrom>=1000)
            {
                workerRate=ticks;ticks=0;rateFrom=start;
                if(Environment.TickCount64>=nextRateLog&&GameFocused()){nextRateLog=Environment.TickCount64+30000;Log($"SCAN {workerRate} full scans/s (one per new game frame); aim {aimRate} steps/s");}
            }
            frame++;
            while(workerActions.TryDequeue(out var action))
                try{action();}catch(Exception e){Log($"WORKER action: {e.GetType().Name}: {e.Message}");}
            ApplyToggleCleanup();
            if(!GameFocused())
            {
                try{Hook.SetEnabled(false,0);ReleaseMarkersUnfocused();}catch(Exception e){Once("worker-unfocused:"+e.Message,"WORKER: "+e.Message);}
                publishedDraw=null;
                Thread.Sleep(20);
                continue;
            }
            var rec=NextRecorder();recorder=rec;
            bool fresh=true;
            try{fresh=DrawWorld();}
            catch(Exception e)
            {
                try{Hook.SetEnabled(false,0);}catch{}
                if(frame%120==1)Log("WORKER "+e.GetType().Name+": "+e.Message);
            }
            // No new game frame: the previous recording is still current (the render thread re-projects it).
            if(fresh){ticks++;Interlocked.Exchange(ref publishedDraw,rec);} // full fence: publish before the next pool check
            double left=period-(clock.Elapsed.TotalMilliseconds-start);
            if(left>=1)Thread.Sleep((int)left);
        }
    }

    // Fast aim/RCS loop: activation, camera read, fresh pose of the locked point, smoothing, recoil
    // control and the camera write, paced precisely at AimHz (independent of GUI FPS and world rate).
    void AimLoop()
    {
        GameThreadClass();
        var clock=Stopwatch.StartNew();double next=0,rateFrom=0;int steps=0;
        while(!workerStop)
        {
            double period=1000.0/Math.Clamp(ui.AimHz,30,4000),now=clock.Elapsed.TotalMilliseconds;
            if(now-rateFrom>=1000){aimRate=steps;steps=0;rateFrom=now;}
            if(publishedDraw==null){next=0;Thread.Sleep(10);continue;} // game not focused: no aim, no RCS
            try{AimStep();steps++;}
            catch(Exception e){AimLog("step:"+e.Message,3000,$"AIM step error: {e.GetType().Name}: {e.Message}");}
            now=clock.Elapsed.TotalMilliseconds;
            if(next==0||now-next>period*4)next=now;
            next+=period;
            WaitUntil(clock,next);
        }
    }

    // Render-thread replay: world-space items projected with the camera as it is right now.
    void ReplayWorld()
    {
        DrawRecorder rec;
        while(true)
        {
            rec=publishedDraw;if(rec==null||povLocation==0)return;
            Interlocked.Exchange(ref replaying,rec);
            if(publishedDraw==rec)break; // the worker never recycles the published or the claimed one
        }
        try{ReplayRecording(rec);}finally{Volatile.Write(ref replaying,null);}
    }
    void ReplayRecording(DrawRecorder rec)
    {
        Vector3 camera,rotation;float fov;
        try
        {
            camera=Read<FVector>(povLocation).ToFloats();
            rotation=Read<FVector>(povRotation).ToFloats();
            fov=Read<float>(povFov);
        }
        catch{return;}
        if(!float.IsFinite(camera.X)||fov<5||fov>170)return;
        var dl=renderer;
        ViewMath.Axes(rotation,out var ax,out var ay,out var az);
        float cx=(int)displaySize.X/2,cy=(int)displaySize.Y/2,focal=cx/MathF.Tan(fov*MathF.PI/360f);
        bool Project(Vector3 p,out Vector2 s)
        {
            var v=p-camera;float z=v.X*ax.X+v.Y*ax.Y+v.Z*ax.Z;
            if(z<=1){s=default;return false;}
            s=new(cx+(v.X*ay.X+v.Y*ay.Y+v.Z*ay.Z)*focal/z,cy-(v.X*az.X+v.Y*az.Y+v.Z*az.Z)*focal/z);
            return true;
        }
        foreach(var (a,b,color) in rec.WorldLines)
            if(Project(a,out var sa)&&Project(b,out var sb))dl.Line(sa,sb,color,1f);
        foreach(var (p,color,text) in rec.WorldTexts)
            if(Project(p,out var s)&&s.X>=0&&s.Y>=0&&s.X<displaySize.X&&s.Y<displaySize.Y)Label(s,color,text);
        foreach(var (a,b,color,t) in rec.Lines)dl.Line(a,b,color,t);
        foreach(var (p,color,text) in rec.Texts)Label(p,color,text);
        foreach(var (p,r,color,_,t) in rec.Circles)dl.Circle(p,r,color,t);
    }
    // Text over the game: a dark offset copy keeps it readable on bright backgrounds.
    void Label(Vector2 p,uint color,string text)
    {
        renderer.Text(p+Vector2.One,0xff000000,text,(color>>24)/255f*0.8f);
        renderer.Text(p,color,text);
    }

    // Precise frame pacing: whole-millisecond sleeps make 180 FPS frames jitter by ±1 ms.
    // Sleep while more than ~0.35 ms remain, then spin to the exact deadline.
    readonly Stopwatch paceClock=Stopwatch.StartNew();
    double paceNext;
    int targetFps=180;
    bool renderPriority;
    const int IdleFps=10;                        // game not focused: nothing is drawn, keep only input alive
    void PaceFrame(bool idle=false)
    {
        if(!renderPriority){renderPriority=true;Thread.CurrentThread.Priority=ThreadPriority.Highest;GameThreadClass();}
        if(targetFps<=0&&!idle)return;
        double period=1000.0/(idle?IdleFps:targetFps),now=paceClock.Elapsed.TotalMilliseconds;
        if(paceNext==0||now-paceNext>period*2)paceNext=now; // first frame / after a stall: resync
        paceNext+=period;
        WaitUntil(paceClock,paceNext);
    }

    [DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);

    // MMCSS "Games" class: the scheduler keeps the render and aim threads responsive while the game
    // saturates the CPU, without hard real-time priority.
    [DllImport("avrt.dll",CharSet=CharSet.Unicode)]static extern nint AvSetMmThreadCharacteristicsW(string task,ref uint index);
    static void GameThreadClass(){uint index=0;try{AvSetMmThreadCharacteristicsW("Games",ref index);}catch{}}

    // Windows 11 treats a process that is never in the foreground as background work (EcoQoS: efficiency
    // cores, coarse timers). Opt out of execution-speed and timer-resolution throttling.
    [StructLayout(LayoutKind.Sequential)]struct PowerThrottlingState{public uint Version,ControlMask,StateMask;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetProcessInformation(nint process,int infoClass,ref PowerThrottlingState info,int size);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
    void DisablePowerThrottling()
    {
        var s=new PowerThrottlingState{Version=1,ControlMask=0x1/*EXECUTION_SPEED*/|0x4/*IGNORE_TIMER_RESOLUTION*/,StateMask=0};
        if(!SetProcessInformation(GetCurrentProcess(),4/*ProcessPowerThrottling*/,ref s,Marshal.SizeOf<PowerThrottlingState>()))
            Log($"Power throttling opt-out failed (error {Marshal.GetLastWin32Error()})");
    }

    // High-resolution waitable timer (Windows 10 1803+): sub-millisecond sleeps without burning a core.
    // Each pacing thread owns one timer; falls back to Sleep(1)+spin when unavailable.
    [ThreadStatic]static nint waitTimer;[ThreadStatic]static bool waitTimerTried;
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint CreateWaitableTimerExW(nint attributes,nint name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetWaitableTimer(nint timer,ref long dueTime,int period,nint routine,nint arg,bool resume);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(nint handle,uint ms);
    static void WaitUntil(Stopwatch clock,double deadlineMs)
    {
        if(!waitTimerTried){waitTimerTried=true;waitTimer=CreateWaitableTimerExW(0,0,0x2/*HIGH_RESOLUTION*/,0x1F0003);}
        while(true)
        {
            double left=deadlineMs-clock.Elapsed.TotalMilliseconds;
            if(left<=0)return;
            if(left>0.35)
            {
                double sleepMs=left-0.3;
                if(waitTimer!=0){long due=-(long)(sleepMs*10000);if(SetWaitableTimer(waitTimer,ref due,0,0,0,false)){WaitForSingleObject(waitTimer,0xFFFFFFFF);continue;}}
                if(sleepMs>1.5){Thread.Sleep(1);continue;}
            }
            Thread.SpinWait(20);
        }
    }
}
