using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed class WeakspotHook : IDisposable
{
    public const string ExeHash="0f4783f75ec178724ddbe10baee55a38ce4311bebf1a8bacd0626b9d8f7d0265";
    public const long GateRva=0x22C2F99;
    public static readonly byte[] Original=Convert.FromHexString("8B879C010000A820");
    readonly Process process;
    readonly Action<string> log;
    nint handle,site,page,data;
    byte[] installed;
    bool lastEnabled;
    nint lastPawn;
    int disposed;
    public bool Installed => installed!=null;
    public WeakspotHook(Process process,Action<string> log) {this.process=process;this.log=log;}
    public static void VerifyBuild(string path)
    {
        using var f=File.OpenRead(path);
        if(!Convert.ToHexString(SHA256.HashData(f)).Equals(ExeHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsupported game version.");
    }

    // Replay the two stolen instructions on EVERY path. Only a resolved hit whose
    // DamageInfo.Owner weak reference matches the current local pawn's index and
    // serial number gets its gate inputs changed. Owner/Target are NOT pointers.
    public static byte[] BuildCode(long codeAddress,long dataAddress,long returnAddress)
    {
        var b=new List<byte>();var skips=new List<int>();
        void Emit(string hex)=>b.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        void Skip(string op){Emit(op);skips.Add(b.Count);b.AddRange(new byte[4]);}
        Emit("4153 49BB");b.AddRange(BitConverter.GetBytes(dataAddress)); // push r11; mov r11,data
        Emit("F049FF4358 488B4744 49894360 488B4754 49894368 8B879C010000 41894370"); // passive gate-call telemetry
        Emit("41807B0800");Skip("0F84"); // enabled?
        // Shared Windows tick counter. Reject a heartbeat older than two seconds
        // so a killed or stalled overlay cannot leave forced damage enabled.
        Emit("A12003FE7F00000000 412B4348 413B434C");Skip("0F87");
        Emit("498B03 4885C0");Skip("0F84"); // local pawn must be non-null
        Emit("48394744");Skip("0F85"); // Owner, exact index+serial
        Emit("48394754");Skip("0F84"); // never self damage
        Emit("837F5800");Skip("0F84"); // target weak-reference serial must be nonzero
        Emit("8B879C010000 41894320 257FFFFFFF 83C860 89879C010000 41894324");
        Emit("488B4754 49894328 488B4744 49894330");
        Emit("B301 B101 F049FF4310"); // pass both eligibility inputs; lock inc counter
        int end=b.Count;
        foreach(int pos in skips)BitConverter.GetBytes(end-pos-4).CopyTo(CollectionsMarshal.AsSpan(b).Slice(pos,4));
        Emit("415B"); // pop r11
        b.AddRange(Original); // mov eax,[rdi+19c]; test al,20
        Emit("E9");b.AddRange(BitConverter.GetBytes(checked((int)(returnAddress-(codeAddress+b.Count+4)))));
        return b.ToArray();
    }

    public void SetEnabled(bool enabled,nint localPawn)
    {
        if(enabled&&!Installed)Install();
        if(!Installed)return;
        if(enabled)
        {
            uint tick=unchecked((uint)Marshal.ReadInt32((nint)0x7ffe0320));
            uint multiplier=unchecked((uint)Marshal.ReadInt32((nint)0x7ffe0004));
            if(multiplier==0)throw new InvalidOperationException("Cannot read Windows shared tick multiplier.");
            uint timeout=checked((uint)Math.Max(1,(2000UL<<24)/multiplier));
            Write(data+0x48,BitConverter.GetBytes(((ulong)timeout<<32)|tick));
        }
        if(enabled==lastEnabled&&localPawn==lastPawn)return;
        // Disable first while refreshing the identity. An aligned 8-byte pointer
        // write is atomic; the flag is enabled only after the write succeeds.
        Write(data+8,[0]);ulong ownerKey=UE.WeakKey(localPawn);Write(data,BitConverter.GetBytes(ownerKey));
        if(enabled&&ownerKey!=0)Write(data+8,[1]);
        lastEnabled=enabled;lastPawn=localPawn;
    }
    public ulong Count => Installed?BitConverter.ToUInt64(Read(data+0x10,8)):0;

    void Install()
    {
        VerifyBuild(process.MainModule.FileName);
        if(TryAdoptExisting())return;
        if(handle==0)handle=OpenProcess(0x38|0x400|0x1000,false,process.Id);
        if(handle==0)throw new Win32Exception();
        site=process.MainModule.BaseAddress+(nint)GateRva;
        if(!Read(site,8).SequenceEqual(Original))throw new InvalidOperationException("Weakspot gate bytes differ; no patch applied.");
        long aligned=(long)site&~0xffffL;
        for(long delta=0x10000;delta<0x70000000&&page==0;delta+=0x10000)
        {
            page=VirtualAllocEx(handle,(nint)(aligned+delta),0x2000,0x3000,0x04);
            if(page==0&&aligned>delta)page=VirtualAllocEx(handle,(nint)(aligned-delta),0x2000,0x3000,0x04);
        }
        if(page==0)throw new Win32Exception("Cannot allocate a nearby trampoline.");
        data=page+0x1000;
        var code=BuildCode(page,data,site+8);
        Check(VirtualProtectEx(handle,page,0x1000,0x04,out _));
        Write(page,code);Check(VirtualProtectEx(handle,page,0x1000,0x20,out _));
        Check(FlushInstructionCache(handle,page,(nuint)code.Length));
        var patch=new List<byte>{0xe9};patch.AddRange(BitConverter.GetBytes(checked((int)((long)page-(long)site-5))));patch.AddRange(new byte[]{0x90,0x90,0x90});
        try{Replace(Original,patch.ToArray());installed=patch.ToArray();}
        catch
        {
            // If restoring page protection failed after a successful write,
            // retain ownership so shutdown can still disable/restore the patch.
            if(Read(site,8).SequenceEqual(patch)){installed=patch.ToArray();Write(data+8,[0]);}
            throw;
        }
        log($"Installed verified weakspot gate at {site:X}; trampoline {page:X}. F7 controls its enabled flag.");
    }
    public bool TryAdoptExisting()
    {
        if(Installed)return true;
        if(handle==0)handle=OpenProcess(0x38|0x400|0x1000,false,process.Id);
        if(handle==0)throw new Win32Exception();
        site=process.MainModule.BaseAddress+(nint)GateRva;
        var bytes=Read(site,8);
        if(bytes.SequenceEqual(Original))return false;
        if(bytes[0]!=0xe9)throw new InvalidOperationException("Unknown patch at weakspot gate; refusing to overwrite it.");
        var candidate=site+5+BitConverter.ToInt32(bytes,1);var config=candidate+0x1000;
        var expected=BuildCode(candidate,config,site+8);
        if(!Read(candidate,expected.Length).SequenceEqual(expected))throw new InvalidOperationException("Existing gate patch is not this overlay's verified trampoline.");
        page=candidate;data=config;installed=bytes;
        Write(data+8,[0]);Write(data,BitConverter.GetBytes((nint)0));lastEnabled=false;lastPawn=0;
        log("Adopted the previous overlay's verified hook; force reset OFF.");
        return true;
    }

    void Replace(byte[] expected,byte[] replacement)
    {
        var threads=new List<nint>();var seen=new HashSet<int>();
        try
        {
            // Freeze the target briefly; reject a thread stopped within stolen
            // instructions. No remote calls, allocations, or loader work here.
            for(int pass=0;pass<4;pass++)
            {
                process.Refresh();bool added=false;
                foreach(ProcessThread t in process.Threads)
                {
                    if(!seen.Add(t.Id))continue;
                    var h=OpenThread(0x0002|0x0008|0x0040,false,t.Id);
                    if(h==0)throw new Win32Exception();
                    if(SuspendThread(h)==uint.MaxValue){CloseHandle(h);throw new Win32Exception();}
                    threads.Add(h);added=true;
                    var block=Marshal.AllocHGlobal(1248);
                    try
                    {
                        nint ctx=(nint)(((long)block+15)&~15L);
                        Marshal.Copy(new byte[1232],0,ctx,1232);Marshal.WriteInt32(ctx,0x30,0x100001);
                        Check(GetThreadContext(h,ctx));long rip=Marshal.ReadInt64(ctx,0xf8);
                        if(rip>=(long)site&&rip<(long)site+8)throw new InvalidOperationException("A game thread is inside the patch site; retry F7.");
                    }
                    finally{Marshal.FreeHGlobal(block);}
                }
                if(!added)break;
                if(pass==3)throw new InvalidOperationException("Game thread list did not stabilize; no patch applied.");
            }
            if(!Read(site,8).SequenceEqual(expected))throw new InvalidOperationException("Patch site changed; refusing to overwrite another modification.");
            Check(VirtualProtectEx(handle,site,8,0x40,out uint old));
            try {Write(site,replacement);Check(FlushInstructionCache(handle,site,8));}
            catch {Write(site,expected);FlushInstructionCache(handle,site,8);throw;}
            finally{Check(VirtualProtectEx(handle,site,8,old,out _));}
        }
        finally{foreach(var h in threads){ResumeThread(h);CloseHandle(h);}}
    }
    byte[] Read(nint address,int count)
    {
        var b=new byte[count];Check(ReadProcessMemory(handle,address,b,(nuint)count,out var n));
        if(n!=(nuint)count)throw new IOException("Short process read.");return b;
    }
    void Write(nint address,byte[] bytes)
    {
        Check(WriteProcessMemory(handle,address,bytes,(nuint)bytes.Length,out var n));
        if(n!=(nuint)bytes.Length)throw new IOException("Short process write.");
    }
    static void Check(bool ok){if(!ok)throw new Win32Exception();}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try
        {
            if(Installed&&!process.HasExited)
            {
                Write(data+8,[0]);
                try{Replace(installed,Original);log("Original weakspot gate restored.");}
                catch(Exception e){log("Hook disabled; code retained until game exit: "+e.Message);}
            }
        }
        finally
        {
            // Retain the disabled allocation until game exit: a paused thread may
            // still have its instruction pointer in the trampoline during detach.
            if(handle!=0){CloseHandle(handle);handle=0;}
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint OpenThread(uint access,bool inherit,int tid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint SuspendThread(nint h);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(nint h);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetThreadContext(nint h,nint context);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint VirtualAllocEx(nint h,nint address,nuint size,uint type,uint protect);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool VirtualProtectEx(nint h,nint address,nuint size,uint protect,out uint old);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool FlushInstructionCache(nint h,nint address,nuint size);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool ReadProcessMemory(nint h,nint address,byte[] buffer,nuint size,out nuint read);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool WriteProcessMemory(nint h,nint address,byte[] buffer,nuint size,out nuint written);
}
