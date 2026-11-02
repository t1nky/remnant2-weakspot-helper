using System.Diagnostics;
using System.Runtime.InteropServices;

// Runs UObject::ProcessEvent calls on the game thread.
// The live SilhouetteManager (an ActorComponent on GameState that ticks every frame) gets a
// copied vtable whose TickComponent slot points at a stub. The stub drains a call queue, then
// tail-jumps to the original TickComponent. No game code bytes are modified; installing and
// removing is a single aligned pointer write on that one object.
internal sealed class GameThread : IDisposable
{
    const ulong Magic=0x3130305447485357; // "WSHGT001"
    const long TickRva=0x21F36F0;          // USilhouetteManager::TickComponent (vtable slot 120)
    const int TickSlot=120;
    const int VtableBytes=0x500;
    // Allocation layout (one RWX block, never freed: a game thread may be inside the stub).
    const int Size=0x20000,Entries=0x40,EntryStride=0x30,MaxEntries=500,Code=0x7000,FakeVtable=0x7808,Scratch=0x8000;
    const int SkipStub=0x7400;   // a single `ret`: entries whose object died before submission call this instead
    // Header: +0 magic, +8 tick heartbeat, +0x10 pending count, +0x18 completed batches,
    // +0x20 original TickComponent, +0x28 original vtable.

    readonly Process process;
    readonly Action<string> log;
    nint handle,block,originalVtable,processEvent;
    nint hookedManager;
    public bool Ready => block!=0;
    public nint ProcessEvent => processEvent;

    sealed record Pending(int Count,Action Done);
    Pending pending;
    long pendingSince;

    public GameThread(Process process,Action<string> log)
    {
        this.process=process;this.log=log;
        handle=OpenProcess(0x38|0x400|0x1000,false,process.Id);
        if(handle==0)throw new System.ComponentModel.Win32Exception();
    }

    public ulong Heartbeat => Ready?Read<ulong>(block+8):0;
    public bool Busy => pending!=null;

    // Call each frame with the current SilhouetteManager. Re-hooks after level transitions.
    public void Ensure(nint manager)
    {
        if(manager==0||manager==hookedManager&&Read<nint>(manager)==block+FakeVtable)return;
        var vt=Read<nint>(manager);
        var module=process.MainModule.BaseAddress;
        if(block==0)
        {
            if(vt==0)return;
            bool inModule=(long)vt>=(long)module&&(long)vt<(long)module+process.MainModule.ModuleMemorySize;
            if(!inModule)Adopt(vt-FakeVtable);
            else Create(vt,module);
        }
        if(vt==originalVtable)
        {
            Write(manager,BitConverter.GetBytes((long)(block+FakeVtable)));
            log($"GAMETHREAD hooked SilhouetteManager @{manager:X} (vtable {vt:X} -> {block+FakeVtable:X})");
        }
        else if(vt!=block+FakeVtable){log($"GAMETHREAD refused: SilhouetteManager vtable {vt:X} is unknown");return;}
        hookedManager=manager;
    }

    void Create(nint vt,nint module)
    {
        var tick=Read<nint>(vt+TickSlot*8);
        if(tick!=module+(nint)TickRva)throw new InvalidOperationException($"SilhouetteManager tick slot is {tick:X}, expected {module+(nint)TickRva:X}.");
        processEvent=Read<nint>(vt+UE.ProcessEventSlot*8);
        var b=VirtualAllocEx(handle,0,Size,0x3000,0x40);
        if(b==0)throw new System.ComponentModel.Win32Exception();
        var header=new byte[0x40];
        BitConverter.GetBytes(Magic).CopyTo(header,0);
        BitConverter.GetBytes((long)tick).CopyTo(header,0x20);
        BitConverter.GetBytes((long)vt).CopyTo(header,0x28);
        Write(b,header);
        Write(b+Code,BuildStub(b,tick));
        Write(b+SkipStub,[0xC3]);
        var table=ReadBytes(vt-8,VtableBytes+8);
        BitConverter.GetBytes((long)(b+Code)).CopyTo(table,8+TickSlot*8);
        Write(b+FakeVtable-8,table);
        FlushInstructionCache(handle,b+Code,0x100);
        block=b;originalVtable=vt;
        log($"GAMETHREAD stub at {b+Code:X}; ProcessEvent {processEvent:X}");
    }

    void Adopt(nint b)
    {
        if(Read<ulong>(b)!=Magic)throw new InvalidOperationException($"SilhouetteManager vtable is patched by something else ({b+FakeVtable:X}).");
        block=b;originalVtable=Read<nint>(b+0x28);
        processEvent=Read<nint>(originalVtable+UE.ProcessEventSlot*8);
        // A previous overlay may have died with a batch queued; its scratch memory is ours now.
        Write(b+0x10,new byte[4]);
        Write(b+SkipStub,[0xC3]);
        log($"GAMETHREAD adopted existing stub at {b+Code:X}");
    }

    // Stub: preserve TickComponent(this=rcx, DeltaTime=xmm1, TickType=r8, TickFunction=r9),
    // run queued fn(a1,a2,a3,a4) entries, publish completion, jump to the original.
    internal static byte[] BuildStub(nint data,nint original)
    {
        var b=new List<byte>();
        void E(string hex)=>b.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        E("53 56 51 52 4150 4151");      // push rbx,rsi,rcx,rdx,r8,r9
        E("4883EC38");                     // sub rsp,38h   (rsp 16-aligned at calls)
        E("F30F7F4C2420");                 // movdqu [rsp+20h],xmm1
        E("48BB");b.AddRange(BitConverter.GetBytes((long)data)); // mov rbx,data
        E("F048FF4308");                   // lock inc qword [rbx+8]
        E("8B7310 85F6");                  // mov esi,[rbx+10h]; test esi,esi
        E("74");int jzDone=b.Count;b.Add(0);
        E("488D5B40");                     // lea rbx,[rbx+40h]
        int loop=b.Count;
        E("488B4B08 488B5310 4C8B4318 4C8B4B20"); // rcx,rdx,r8,r9 <- entry
        E("FF13");                         // call [rbx]
        E("48894328 4883C330 FFCE");       // [rbx+28h]=rax; rbx+=30h; dec esi
        E("75");b.Add((byte)(loop-(b.Count+1)));
        E("48BB");b.AddRange(BitConverter.GetBytes((long)data));
        E("F048FF4318");                   // lock inc qword [rbx+18h]
        E("C7431000000000");               // mov dword [rbx+10h],0  (batch done)
        b[jzDone]=(byte)(b.Count-(jzDone+1));
        E("F30F6F4C2420 4883C438");        // movdqu xmm1,[rsp+20h]; add rsp,38h
        E("4159 4158 5A 59 5E 5B");        // pop r9,r8,rdx,rcx,rsi,rbx
        E("FF2500000000");b.AddRange(BitConverter.GetBytes((long)original)); // jmp [rip]
        return b.ToArray();
    }

    // A call is ProcessEvent(Object, Function, Params). Params are copied into scratch memory;
    // Done receives a reader for each call's parameter block after the game thread ran them.
    public sealed record Call(nint Object,UFn Function,byte[] Params);
    public bool Submit(IReadOnlyList<Call> calls,Action<Func<int,byte[]>> done)
    {
        Poll();
        if(!Ready||pending!=null||calls.Count==0)return false;
        if(calls.Count>MaxEntries)throw new ArgumentException("Too many game-thread calls in one batch.");
        var entries=new byte[calls.Count*EntryStride];var addresses=new nint[calls.Count];
        int offset=0;var scratch=new byte[Size-Scratch];
        // ProcessEvent on a destroyed object calls through a dead vtable and crashes the game (e.g. a broken
        // armor plate's mesh). Calls whose object is no longer live (or is being destroyed) run a bare
        // `ret` instead; queued calls can sit a few frames, so check at submission.
        var live=new Dictionary<nint,bool>();int skipped=0;
        for(int i=0;i<calls.Count;i++)
        {
            var c=calls[i];int len=Math.Max(16,(c.Params.Length+15)&~15);
            if(offset+len>scratch.Length)throw new ArgumentException("Game-thread parameter scratch exhausted.");
            c.Params.CopyTo(scratch,offset);addresses[i]=block+Scratch+offset;
            if(!live.TryGetValue(c.Object,out bool ok))live[c.Object]=ok=UE.LiveInSlot(c.Object,UE.SlotKey(c.Object));
            if(!ok)skipped++;
            BitConverter.GetBytes((long)(ok?processEvent:block+SkipStub)).CopyTo(entries,i*EntryStride);
            BitConverter.GetBytes((long)c.Object).CopyTo(entries,i*EntryStride+8);
            BitConverter.GetBytes((long)c.Function.Address).CopyTo(entries,i*EntryStride+0x10);
            BitConverter.GetBytes((long)addresses[i]).CopyTo(entries,i*EntryStride+0x18);
            offset+=len;
        }
        if(skipped>0)log($"GAMETHREAD skipped {skipped} call(s) on destroyed objects");
        Write(block+Scratch,scratch[..offset]);
        Write(block+Entries,entries);
        var lengths=calls.Select(c=>c.Params.Length).ToArray();
        // One cross-process read for the whole batch's parameter blocks, then slice per call.
        int used=offset;
        pending=new(calls.Count,()=>
        {
            var all=ReadBytes(block+Scratch,used);
            done(i=>all.AsSpan((int)(addresses[i]-(block+Scratch)),lengths[i]).ToArray());
        });
        pendingSince=Environment.TickCount64;
        Write(block+0x10,BitConverter.GetBytes(calls.Count)); // publish last
        return true;
    }

    public void Poll()
    {
        if(pending==null)return;
        if(Read<int>(block+0x10)!=0)
        {
            if(Environment.TickCount64-pendingSince>5000&&Environment.TickCount64%5000<20)log("GAMETHREAD batch still waiting for a game tick (paused or loading?)");
            return;
        }
        var p=pending;pending=null;
        try{p.Done();}catch(Exception e){log("GAMETHREAD completion: "+e.Message);}
        FlushWaiting();
    }

    // Small, time-sensitive requests (e.g. charge polling) must not be refused just because another
    // batch is in flight: they wait here and go out merged into the next submission.
    readonly List<(IReadOnlyList<Call> Calls,Action<Func<int,byte[]>> Done)> waiting=new();
    public void Enqueue(IReadOnlyList<Call> calls,Action<Func<int,byte[]>> done)
    {
        if(calls.Count==0)return;
        Poll();
        if(pending==null&&waiting.Count==0&&Submit(calls,done))return;
        if(waiting.Count<64)waiting.Add((calls,done));
        if(pending==null)FlushWaiting();
    }
    // Exit / cleanup: run everything queued before returning.
    public void Drain(int timeoutMs=1500)
    {
        var until=Environment.TickCount64+timeoutMs;
        while(Environment.TickCount64<until)
        {
            Poll();FlushWaiting();
            if(pending==null&&waiting.Count==0)return;
            Thread.Sleep(10);
        }
    }
    void FlushWaiting()
    {
        if(pending!=null||waiting.Count==0||!Ready)return;
        var merged=new List<Call>();var parts=new List<(int Start,Action<Func<int,byte[]>> Done)>();
        int taken=0;
        foreach(var (calls,done) in waiting)
        {
            if(merged.Count+calls.Count>MaxEntries)break;
            parts.Add((merged.Count,done));merged.AddRange(calls);taken++;
        }
        waiting.RemoveRange(0,taken);
        if(merged.Count==0)return;
        Submit(merged,read=>{foreach(var (start,done) in parts)try{done(i=>read(start+i));}catch(Exception e){log("GAMETHREAD completion: "+e.Message);}});
    }

    int disposed;
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try
        {
            if(hookedManager!=0&&!process.HasExited&&Read<nint>(hookedManager)==block+FakeVtable)
            {Write(hookedManager,BitConverter.GetBytes((long)originalVtable));log("GAMETHREAD unhooked SilhouetteManager");}
        }
        catch(Exception e){log("GAMETHREAD unhook: "+e.Message);}
        if(handle!=0){CloseHandle(handle);handle=0;}
    }

    T Read<T>(nint a) where T:unmanaged
    {
        var b=ReadBytes(a,Marshal.SizeOf<T>());return MemoryMarshal.Read<T>(b);
    }
    byte[] ReadBytes(nint a,int n)
    {
        var b=new byte[n];
        if(!ReadProcessMemory(handle,a,b,(nuint)n,out var r)||r!=(nuint)n)throw new IOException($"GameThread read failed at {a:X}");
        return b;
    }
    void Write(nint a,byte[] bytes)
    {
        if(!WriteProcessMemory(handle,a,bytes,(nuint)bytes.Length,out var w)||w!=(nuint)bytes.Length)throw new IOException($"GameThread write failed at {a:X}");
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint VirtualAllocEx(nint h,nint address,nuint size,uint type,uint protect);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool FlushInstructionCache(nint h,nint address,nuint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool ReadProcessMemory(nint h,nint address,byte[] buffer,nuint size,out nuint read);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool WriteProcessMemory(nint h,nint address,byte[] buffer,nuint size,out nuint written);
}

// A reflected UFunction with its real parameter layout (FProperty::Offset_Internal / ElementSize).
internal sealed class UFn
{
    const ulong ReturnParm=0x400;
    public nint Address{get;init;}
    public string Name{get;init;}
    public Dictionary<string,(int Offset,int Size,ulong Flags)> Params{get;init;}=new();
    public int Size=>Params.Count==0?0:Params.Values.Max(p=>p.Offset+p.Size);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(nint,string),UFn> cache=new();
    // Keyed by class address: a class unloaded with a level can be replaced by another at the same
    // address, and calling a stale UFunction crashes the game. Cleared on every world change.
    public static void ClearCache()=>cache.Clear();

    public static UFn Get(nint cls,string name)
    {
        if(cache.TryGetValue((cls,name),out var f))return f;
        var func=UE.Function(cls,name);
        if(func==0)throw new MissingMethodException(name);
        return cache[(cls,name)]=new UFn{Address=func,Name=name,Params=UE.Parameters(func)};
    }
    public string Describe()=>$"{Name}@{Address:X} size={Size}: "+string.Join(", ",Params.Select(p=>$"{p.Key}+{p.Value.Offset:X}/{p.Value.Size}{((p.Value.Flags&ReturnParm)!=0?" ret":"")}"));

    public Args New()=>new(this);
    internal sealed class Args(UFn fn)
    {
        public readonly byte[] Data=new byte[Math.Max(fn.Size,8)];
        int Off(string n)=>fn.Params.TryGetValue(n,out var p)?p.Offset:throw new KeyNotFoundException($"{fn.Name} has no parameter {n}");
        public Args Ptr(string n,nint v){BitConverter.GetBytes((long)v).CopyTo(Data,Off(n));return this;}
        public Args Bool(string n,bool v){Data[Off(n)]=(byte)(v?1:0);return this;}
        public Args Byte(string n,byte v){Data[Off(n)]=v;return this;}
        public Args Int(string n,int v){BitConverter.GetBytes(v).CopyTo(Data,Off(n));return this;}
        public Args Name(string n,ulong fname){BitConverter.GetBytes(fname).CopyTo(Data,Off(n));return this;}
        // FTransform (double precision): Rotation quat @0, Translation @0x20, Scale3D @0x40.
        public Args Transform(string n,System.Numerics.Quaternion r,System.Numerics.Vector3 t,System.Numerics.Vector3 s)
        {
            int o=Off(n);double[] v=[r.X,r.Y,r.Z,r.W,t.X,t.Y,t.Z,0,s.X,s.Y,s.Z,0];
            for(int i=0;i<v.Length;i++)BitConverter.GetBytes(v[i]).CopyTo(Data,o+i*8);
            return this;
        }
    }
    public static nint ReturnPtr(UFn fn,byte[] data)=>(nint)BitConverter.ToInt64(data,fn.Params["ReturnValue"].Offset);
}
