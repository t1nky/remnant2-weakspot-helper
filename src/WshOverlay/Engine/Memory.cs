using System.Diagnostics;
using System.Runtime.InteropServices;

// Cross-process memory access to the game.
//  Read / Bytes throw when the memory is unavailable.
//  Peek returns default instead (for probing pointers that may already be stale).
internal static unsafe class Mem
{
    public static Process Process{get;private set;}
    public static nint Handle{get;private set;}
    public static nint Base{get;private set;}
    public static int ModuleSize{get;private set;}
    static nint window;static long windowCheckedAt;
    // The game's main window. Re-checked once a second, in case the game recreated it.
    public static nint Window
    {
        get
        {
            long now=Environment.TickCount64;
            if(window!=0&&now<windowCheckedAt)return window;
            lock(Process)
            {
                if(window!=0&&now<windowCheckedAt)return window;
                windowCheckedAt=now+1000;
                if(window!=0&&IsWindow(window)&&GetWindowThreadProcessId(window,out uint pid)!=0&&pid==(uint)Process.Id)return window;
                try{Process.Refresh();window=Process.MainWindowHandle;}catch{window=0;}
                if(window==0)windowCheckedAt=0;
            }
            return window;
        }
    }
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);

    public static void Open(Process process)
    {
        Process=process;
        Handle=OpenProcess(0x0008|0x0010|0x0020|0x0400/*VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION*/,false,process.Id);
        if(Handle==0)throw new System.ComponentModel.Win32Exception();
        Base=process.MainModule.BaseAddress;ModuleSize=process.MainModule.ModuleMemorySize;
    }

    public static T Read<T>(nint address) where T:unmanaged
    {
        T value;nuint n;
        if(!ReadProcessMemory(Handle,address,&value,(nuint)sizeof(T),&n)||n!=(nuint)sizeof(T))throw new IOException($"Read failed at {address:X}");
        return value;
    }
    public static T Peek<T>(nint address) where T:unmanaged
    {
        T value;nuint n;
        if(!ReadProcessMemory(Handle,address,&value,(nuint)sizeof(T),&n)||n!=(nuint)sizeof(T))return default;
        return value;
    }
    public static nint Ptr(nint address)=>Read<nint>(address);
    public static byte[] Bytes(nint address,int count)
    {
        if(count==0)return [];
        if(count<0||count>64*1024*1024)throw new InvalidDataException("Invalid memory span length.");
        var data=new byte[count];
        if(!TryRead(address,data))throw new IOException($"Process memory span unavailable at {address:X} ({count} bytes).");
        return data;
    }
    public static bool TryRead(nint address,byte[] buffer)
    {
        nuint n;
        fixed(byte* p=buffer)return ReadProcessMemory(Handle,address,p,(nuint)buffer.Length,&n)&&n==(nuint)buffer.Length;
    }
    public static bool Write(nint address,byte[] data)
    {
        nuint n;
        fixed(byte* p=data)return WriteProcessMemory(Handle,address,p,(nuint)data.Length,&n)&&n==(nuint)data.Length;
    }
    public static bool Write<T>(nint address,T value) where T:unmanaged
    {
        nuint n;
        return WriteProcessMemory(Handle,address,&value,(nuint)sizeof(T),&n)&&n==(nuint)sizeof(T);
    }

    [DllImport("kernel32.dll",SetLastError=true)] static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint process,nint address,void* buffer,nuint size,nuint* read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(nint process,nint address,void* buffer,nuint size,nuint* written);
}
