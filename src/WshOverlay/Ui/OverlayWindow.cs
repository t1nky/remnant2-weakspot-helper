using System.Numerics;
using System.Runtime.InteropServices;

// A borderless, topmost, transparent window laid over the game's client area. It ignores the mouse
// (clicks go to the game) except while the pointer is over the menu, and never takes focus.
internal sealed unsafe class OverlayWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct WndClassEx{public uint Size,Style;public nint WndProc;public int ClsExtra,WndExtra;public nint Instance,Icon,Cursor,Background;public nint MenuName,ClassName;public nint IconSm;}
    [StructLayout(LayoutKind.Sequential)] struct Msg{public nint Hwnd;public uint Message;public nint WParam,LParam;public uint Time;public int X,Y,Private;}
    [StructLayout(LayoutKind.Sequential)] struct Margins{public int Left,Right,Top,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct Point{public int X,Y;}
    delegate nint WndProcFn(nint hwnd,uint msg,nint wParam,nint lParam);

    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd,uint msg,nint wParam,nint lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out Msg msg,nint hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd,ref Point point);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance,nint name);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(nint hwnd,ref Margins margins);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern nint GetModuleHandleW(string name);

    const uint WS_POPUP=0x80000000,WS_VISIBLE=0x10000000;
    const uint WS_EX_TOPMOST=0x8,WS_EX_TRANSPARENT=0x20,WS_EX_TOOLWINDOW=0x80,WS_EX_LAYERED=0x80000,WS_EX_NOACTIVATE=0x8000000;
    const int GWL_EXSTYLE=-20;
    const uint WM_MOUSEWHEEL=0x020A,WM_DESTROY=0x0002,WM_QUIT=0x0012,WM_MOUSEACTIVATE=0x0021;

    readonly WndProcFn proc;   // kept alive: the native window holds a pointer to it
    public nint Handle{get;}
    public Vector2 Position{get;private set;}
    public Vector2 Size{get;private set;}
    public float WheelDelta;    // accumulated wheel notches since the last read
    public bool Closed{get;private set;}
    bool clickThrough=true;

    public OverlayWindow(string title)
    {
        proc=WndProc;
        var name=Marshal.StringToHGlobalUni("WshOverlayWindow");
        var wc=new WndClassEx{Size=(uint)sizeof(WndClassEx),WndProc=Marshal.GetFunctionPointerForDelegate(proc),Instance=GetModuleHandleW(null),
            Cursor=LoadCursorW(0,32512/*IDC_ARROW*/),ClassName=name};
        RegisterClassExW(ref wc);
        Handle=CreateWindowExW(WS_EX_TOPMOST|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_LAYERED|WS_EX_NOACTIVATE,"WshOverlayWindow",title,
            WS_POPUP|WS_VISIBLE,0,0,800,600,0,0,wc.Instance,0);
        if(Handle==0)throw new System.ComponentModel.Win32Exception();
        SetLayeredWindowAttributes(Handle,0,255,0x2/*LWA_ALPHA*/);
        var m=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};
        DwmExtendFrameIntoClientArea(Handle,ref m);
        Size=new(800,600);
    }

    nint WndProc(nint hwnd,uint msg,nint wParam,nint lParam)
    {
        switch(msg)
        {
            case WM_MOUSEWHEEL:WheelDelta+=(short)((long)wParam>>16)/120f;return 0;
            case WM_MOUSEACTIVATE:return 3; // MA_NOACTIVATE: clicking the menu keeps the game focused
            case WM_DESTROY:Closed=true;return 0;
        }
        return DefWindowProcW(hwnd,msg,wParam,lParam);
    }

    public void Pump()
    {
        while(PeekMessageW(out var m,0,0,0,1/*PM_REMOVE*/))
        {
            if(m.Message==WM_QUIT){Closed=true;continue;}
            TranslateMessage(ref m);DispatchMessageW(ref m);
        }
    }

    // Cover exactly the target window's client area. Returns true when the size changed.
    public bool FitTo(nint target)
    {
        if(target==0||!GetClientRect(target,out var r)||r.Right<=0||r.Bottom<=0)return false;
        var origin=new Point();ClientToScreen(target,ref origin);
        var pos=new Vector2(origin.X,origin.Y);var size=new Vector2(r.Right,r.Bottom);
        if(pos==Position&&size==Size)return false;
        bool resized=size!=Size;
        Position=pos;Size=size;
        SetWindowPos(Handle,-1/*HWND_TOPMOST*/,origin.X,origin.Y,r.Right,r.Bottom,0x10/*SWP_NOACTIVATE*/);
        return resized;
    }

    public static float DpiScale(nint window)
    {
        uint dpi=window==0?96:GetDpiForWindow(window);
        return dpi==0?1:dpi/96f;
    }

    // Mouse position in window coordinates.
    public Vector2 Mouse
    {
        get{GetCursorPos(out var p);return new Vector2(p.X,p.Y)-Position;}
    }

    // Clicks reach the overlay only while it wants them (pointer over the menu); otherwise they pass through.
    public void SetClickThrough(bool through)
    {
        if(through==clickThrough)return;
        clickThrough=through;
        var ex=(long)GetWindowLongPtr(Handle,GWL_EXSTYLE);
        ex=through?ex|WS_EX_TRANSPARENT:ex&~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(Handle,GWL_EXSTYLE,(nint)ex);
    }

    public void Dispose()
    {
        if(!Closed){Closed=true;DestroyWindow(Handle);}
    }
}
