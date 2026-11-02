using System.Diagnostics;
using System.Runtime.InteropServices;

// Start Remnant 2, then run WshOverlay.exe.
Native.SetProcessDpiAwarenessContext(-4); // per-monitor DPI aware: overlay pixels match the game's

// One overlay at a time: two instances would share the game-thread call queue and the hooks.
using var instance=new Mutex(false,@"Local\WshOverlayLive");
if(!instance.WaitOne(0)){Ui.Info("Remnant 2 Weakspot Helper is already running.\n\nPress Insert in game to show it, or End to close it.");return;}

Process process;
while(true)
{
    process=Process.GetProcessesByName("Remnant2-Win64-Shipping").FirstOrDefault();
    if(process==null)
    {
        if(!Ui.RetryCancel("Remnant 2 is not running.\n\nStart the game, then press Retry."))return;
        continue;
    }
    try{WeakspotHook.VerifyBuild(process.MainModule.FileName);Mem.Open(process);UE.Initialize();break;}
    catch(InvalidOperationException)
    {
        Ui.Error("This version of Remnant 2 is not supported.\n\nThe game was probably updated. This tool reads game memory at "
            +"version-specific locations, so it has to be updated for the new game version before it can be used safely.");
        return;
    }
    catch(InvalidDataException)
    {
        if(!Ui.RetryCancel("Remnant 2 is still starting.\n\nWait for the main menu, then press Retry."))return;
    }
    catch(Exception e){Ui.Error("Could not access the Remnant 2 process:\n\n"+e.Message+"\n\nIf the game runs as administrator, run this tool as administrator too.");return;}
}

var overlay=new GameOverlay();
using var hook=new WeakspotHook(process,overlay.Log);
overlay.Hook=hook;
using var gameThread=new GameThread(process,overlay.Log);
overlay.GameThread=gameThread;
AppDomain.CurrentDomain.ProcessExit+=(_,_)=>gameThread.Dispose();
AppDomain.CurrentDomain.ProcessExit+=(_,_)=>hook.Dispose();
overlay.Log($"Attached to Remnant 2 (pid {process.Id}).");
overlay.Run();

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
}

// Native message boxes: the app has no console window, so startup problems must be shown explicitly.
static class Ui
{
    const string Title="Remnant 2 Weakspot Helper";
    [DllImport("user32.dll",EntryPoint="MessageBoxW",CharSet=CharSet.Unicode)]
    static extern int MessageBox(nint owner,string text,string caption,uint type);
    public static void Info(string text)=>MessageBox(0,text,Title,0x40);
    public static void Error(string text)=>MessageBox(0,text,Title,0x10);
    public static bool RetryCancel(string text)=>MessageBox(0,text,Title,0x05|0x30)==4;
}
