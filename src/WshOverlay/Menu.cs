using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

// User interface: one tabbed window (Insert shows/hides it, End restores the game and exits) and short
// on-screen notices when a hotkey changes a feature. Preferences persist in ui_config.json.
// Hotkeys only act while the game (or this overlay) has focus.
internal sealed partial class GameOverlay
{
    sealed class UiConfig
    {
        public int FpsLimit{get;set;}=180;   // overlay frames per second; 0 = unlimited
        public int WorldHz{get;set;}=250;    // memory scan / shapes / markers / target selection (cap)
        public int AimHz{get;set;}=1000;     // aim + recoil control steps per second
        public float WindowX{get;set;}=24;
        public float WindowY{get;set;}=24;
        public float WindowW{get;set;}=480;
        public float WindowH{get;set;}=640;
        // Visuals (remembered between sessions). Gameplay changes (force, freeze) always start off.
        public bool ShowWeakspots{get;set;}=true;
        public bool OutlineInGame{get;set;}
        public bool Silhouettes{get;set;}
        public int OutlineStyle{get;set;}
        public bool OutlineOwnMesh{get;set;}=true;
        public bool OutlinePrerequisites{get;set;}=true;
        public bool ShowResisted{get;set;}
        public int Attack{get;set;}
        // Freeze options (the freeze itself always starts off).
        public bool FreezeIncludeIdle{get;set;}
        public float FreezeDelay{get;set;}=3;
        public int Tab{get;set;}
    }
    UiConfig ui=LoadUi();
    static string UiPath=>Path.Combine(AppContext.BaseDirectory,"ui_config.json");
    static UiConfig LoadUi(){try{return File.Exists(UiPath)?JsonSerializer.Deserialize<UiConfig>(File.ReadAllText(UiPath))??new():new();}catch{return new();}}
    void SaveUi(){try{File.WriteAllText(UiPath,JsonSerializer.Serialize(ui,new JsonSerializerOptions{WriteIndented=true}));}catch{}}

    // Copies the remembered visual preferences into the live feature flags (once, at start).
    void ApplyUiPreferences()
    {
        esp=ui.ShowWeakspots;weakOnly=ui.OutlineInGame;hiLite=ui.Silhouettes;
        outlineStencil=254-Math.Clamp(ui.OutlineStyle,0,outlineStyles.Length-1);
        visualTwinOutline=ui.OutlineOwnMesh;prereqOutline=ui.OutlinePrerequisites;
        showArmor=ui.ShowResisted;attackType=Math.Clamp(ui.Attack,0,attackLabels.Length-1);
        freezeIncludeIdle=ui.FreezeIncludeIdle;freezeDelay=Math.Clamp(ui.FreezeDelay,0,10);tab=Math.Clamp(ui.Tab,0,tabs.Length-1);
    }
    // Copies the live feature flags back into the config; changes are written by SaveSettings.
    void RememberUiPreferences()
    {
        var now=(esp,weakOnly,hiLite,254-outlineStencil,visualTwinOutline,prereqOutline,showArmor,attackType,freezeIncludeIdle,freezeDelay,tab);
        if(now==(ui.ShowWeakspots,ui.OutlineInGame,ui.Silhouettes,ui.OutlineStyle,ui.OutlineOwnMesh,ui.OutlinePrerequisites,ui.ShowResisted,ui.Attack,ui.FreezeIncludeIdle,ui.FreezeDelay,ui.Tab))return;
        (ui.ShowWeakspots,ui.OutlineInGame,ui.Silhouettes,ui.OutlineStyle,ui.OutlineOwnMesh,ui.OutlinePrerequisites,ui.ShowResisted,ui.Attack,ui.FreezeIncludeIdle,ui.FreezeDelay,ui.Tab)=now;
        uiDirty=true;
    }
    // Every frame on the render thread (which is where settings change), and once more on exit.
    // Not while the mouse button is held (a slider drag would rewrite the file every frame).
    // remember=false writes what is pending without re-reading the flags (exit cleanup switches some off).
    void SaveSettings(bool now=false,bool remember=true)
    {
        if(remember)RememberUiPreferences();
        bool settled=now||!KeyDown(0x01);
        if(uiDirty&&settled){uiDirty=false;SaveUi();}
        if(aimDirty&&(now||settled&&NowMs>=aimSaveAt)){aimDirty=false;AimCfg.Save(Log);}
    }

    // ---------------------------------------------------------------- hotkeys

    const int VkInsert=0x2D,VkEnd=0x23,VkF3=0x72,VkF4=0x73,VkF5=0x74,VkF6=0x75,VkF7=0x76,VkF9=0x78,VkF10=0x79;
    readonly bool[] keyWas=new bool[256];
    bool Pressed(int vk){bool down=KeyDown(vk),was=keyWas[vk];keyWas[vk]=down;return down&&!was;}

    // Every key is polled each frame (so presses made in other apps are consumed, not replayed later)
    // but only acted on while the game or the overlay is focused.
    void HandleHotkeys(bool focused)
    {
        bool P(int vk)=>Pressed(vk)&focused;
        bool menuKey=P(VkInsert),exitKey=P(VkEnd);
        bool f3=P(VkF3),f4=P(VkF4),f5=P(VkF5),f6=P(VkF6),f7=P(VkF7),f9=P(VkF9),f10=P(VkF10);
        if(menuKey)menu=!menu;
        if(exitKey)exitRequested=true;
        if(f6){esp=!esp;Notify("Weakspot markers",esp);}
        if(f10){weakOnly=!weakOnly;Notify("In-game outlines",weakOnly);}
        if(f9){hiLite=!hiLite;Notify("Enemy silhouettes",hiLite);}
        if(f7){force=!force;Notify("Every hit is a weakspot hit",force);}
        if(f3){freezeEnemies=!freezeEnemies;Notify("Freeze enemies",freezeEnemies);}
        AimHotkeys(f5,f4);
    }

    // ---------------------------------------------------------------- notices

    string noticeText="";bool? noticeState;long noticeUntil;
    void Notify(string text,bool? state=null)
    {
        noticeText=text;noticeState=state;noticeUntil=Environment.TickCount64+1600;
        Log(state==null?text:$"{text}: {(state.Value?"ON":"OFF")}");
    }
    void DrawNotice()
    {
        long left=noticeUntil-Environment.TickCount64;
        if(left<=0)return;
        float alpha=Math.Clamp(left/300f,0,1);
        string state=noticeState==null?"":noticeState.Value?"  ON":"  OFF";
        float textW=renderer.Measure(noticeText),w=textW+renderer.Measure(state),h=renderer.LineHeight;
        var pos=new Vector2((displaySize.X-w)/2,displaySize.Y*0.12f);
        var pad=new Vector2(14,8)*gui.Scale;
        renderer.FillRect(pos-pad,pos+new Vector2(w,h)+pad,Gui.Rgba(0.08f,0.09f,0.11f,0.85f*alpha),8*gui.Scale);
        renderer.Text(pos,0xffffffff,noticeText,alpha);
        if(state.Length>0)renderer.Text(pos+new Vector2(textW,0),noticeState.Value?Gui.Good:Gui.Muted,state,alpha);
    }

    // ---------------------------------------------------------------- main window

    static readonly string[] tabs=["Visuals","Combat","Aim","Settings"];
    int tab;
    void DrawWindows()
    {
        gui.BeginFrame(window.Mouse,KeyDown(0x01),window.WheelDelta);window.WheelDelta=0;
        var pos=new Vector2(ui.WindowX,ui.WindowY);var size=new Vector2(ui.WindowW,ui.WindowH)*gui.Scale;
        gui.BeginWindow("Remnant 2 Weakspot Helper",ref pos,ref size,new Vector2(420,260)*gui.Scale,new Vector2(900,2000)*gui.Scale,displaySize);
        size/=gui.Scale;
        if(pos!=new Vector2(ui.WindowX,ui.WindowY)||size!=new Vector2(ui.WindowW,ui.WindowH))
        {(ui.WindowX,ui.WindowY,ui.WindowW,ui.WindowH)=(pos.X,pos.Y,size.X,size.Y);uiDirty=true;}
        StatusLine();
        tab=gui.Tabs(tabs,tab);
        gui.BeginScroll(tabs[tab]);
        switch(tab){case 0:VisualsTab();break;case 1:CombatTab();break;case 2:AimTab();break;default:SettingsTab();break;}
        gui.EndScroll();
        gui.EndWindow();
    }
    bool uiDirty;

    void StatusLine()
    {
        gui.Dot(inLevel?Gui.Good:Gui.Warn);
        gui.Text(inLevel?(enemies>0?$"{enemies} {(enemies==1?"enemy":"enemies")} with weakspots nearby":"In game, no weakspots nearby"):"Waiting for a level to load...");
        const string hint="Insert: hide   End: exit";
        gui.SameLine(gui.ContentRight-gui.Measure(hint));
        gui.TextDisabled(hint);
        gui.AddSpacing();
    }

    void VisualsTab()
    {
        gui.Toggle("Show weakspots",ref esp,"F6","Draws each enemy's weakspots, and anything that must break first, over the game.");
        if(gui.Toggle("Outline weakspots in game",ref weakOnly,"F10","Uses the game's own outline effect on weakspots. Everything is restored when you turn it off or exit."))
            Log($"In-game outline: {(weakOnly?"ON":"OFF")}");
        if(weakOnly)
        {
            gui.Indent();
            int style=254-outlineStencil;
            if(gui.Combo("Style",ref style,outlineStyles)){outlineStencil=254-style;WorkerDo(()=>{RestoreOutlines();markerRestyle=true;});}
            gui.Checkbox("Outline the weakspot's own mesh",ref visualTwinOutline);gui.Tip("When a weakspot has a visible mesh (e.g. a glowing core), outline that mesh instead of a marker.");
            gui.Checkbox("Outline plates and covers that break first",ref prereqOutline);
            gui.Unindent();
        }
        if(gui.Toggle("Enemy silhouettes",ref hiLite,"F9","Shows every enemy through walls using the game's silhouette effect."))
            Log($"Silhouettes: {(hiLite?"ON":"OFF")}");

        gui.SeparatorText("Detection");
        gui.Combo("Attack type",ref attackType,attackLabels);
        gui.Tip("Which hit detection to show. Some weakspots only react to bullets, others to melee or thrown projectiles.");
        gui.Checkbox("Also show resisted regions",ref showArmor);

        gui.SeparatorText("Legend");
        gui.Swatch(0xffffff00,"Weakspot you can hit now");
        gui.Swatch(0xff00a5ff,"Must break first, or blocks the weakspot");
        gui.Swatch(0xff8f8f8f,"Weakspot that is currently inactive");
        if(showArmor)gui.Swatch(0xff8080ff,"Resisted region");
    }

    void CombatTab()
    {
        gui.Toggle("Every hit is a weakspot hit",ref force,"F7","Your hits on enemies count as weakspot hits. Starts off every launch and switches off automatically if this app stops.");
        if(force&&Hook.Installed){gui.Indent();gui.TextDisabled($"{lastCount} hits converted");gui.Unindent();}
        gui.Toggle("Freeze enemies",ref freezeEnemies,"F3","Stops enemies in place. Starts off every launch; everything is released when you turn it off or exit.");
        if(freezeEnemies)
        {
            gui.Indent();
            gui.Checkbox("Include enemies that have not noticed you",ref freezeIncludeIdle);
            if(!freezeIncludeIdle)gui.SliderFloat("Freeze after they notice you",ref freezeDelay,0,10,"%.1f s");
            gui.TextDisabled($"{frozenCount} frozen");
            gui.Unindent();
        }
        gui.SeparatorText("Weapon");
        RecoilSection();
    }

    void SettingsTab()
    {
        gui.SeparatorText("Hotkeys");
        foreach(var (key,what) in new[]{("Insert","Show / hide this window"),("End","Restore the game and exit"),("F6","Show weakspots"),("F10","Outline weakspots in game"),
            ("F9","Enemy silhouettes"),("F7","Every hit is a weakspot hit"),("F3","Freeze enemies"),("F5","Aim assist"),("F4","Recoil control")})
        {gui.Text(key,Gui.Accent);gui.SameLineAt(gui.Measure("Insert")+gui.LineHeight*1.5f);gui.Text(what);}
        gui.TextDisabled("Hotkeys only work while the game is focused.");

        gui.SeparatorText("Performance");
        int fpsIndex=Array.IndexOf(fpsChoices,ui.FpsLimit);if(fpsIndex<0)fpsIndex=Array.IndexOf(fpsChoices,180);
        if(gui.Combo("Overlay FPS",ref fpsIndex,fpsLabels)){ui.FpsLimit=fpsChoices[fpsIndex];targetFps=ui.FpsLimit;uiDirty=true;}
        int worldHz=ui.WorldHz;
        if(gui.SliderInt("Scan rate",ref worldHz,30,500,"%d Hz")){ui.WorldHz=worldHz;uiDirty=true;}
        gui.Tip("How often enemies and weakspots are re-read from the game. Lower it if the game stutters.");
        int aimIndex=Array.IndexOf(aimChoices,ui.AimHz);if(aimIndex<0)aimIndex=2;
        if(gui.Combo("Aim update rate",ref aimIndex,aimLabels)){ui.AimHz=aimChoices[aimIndex];uiDirty=true;}
        gui.TextDisabled($"Measured: overlay {fpsMeasured:F0} FPS, scan {workerRate} Hz, aim {aimRate} Hz");

        gui.SeparatorText("Troubleshooting");
        if(gui.Button("Open logs folder"))OpenFolder(Path.Combine(AppContext.BaseDirectory,"logs"));
        gui.AddSpacing();
        if(gui.Button("Exit and restore the game"))exitRequested=true;
    }
    static void OpenFolder(string path)
    {
        try{Directory.CreateDirectory(path);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",$"\"{path}\""){UseShellExecute=true});}catch{}
    }

    static readonly int[] fpsChoices=[30,45,60,90,120,144,180,240,0];
    static readonly string[] fpsLabels=["30","45","60","90","120","144","180","240","Unlimited"];
    static readonly int[] aimChoices=[250,500,1000,2000];
    static readonly string[] aimLabels=["250 Hz","500 Hz","1000 Hz","2000 Hz"];
    readonly System.Diagnostics.Stopwatch fpsClock=System.Diagnostics.Stopwatch.StartNew();
    double fpsMeasured,frameMs,fpsLast;
    void MeasureFps()
    {
        double now=fpsClock.Elapsed.TotalMilliseconds,dt=now-fpsLast;fpsLast=now;
        if(dt<=0||dt>1000)return;
        frameMs=frameMs==0?dt:frameMs*0.95+dt*0.05;fpsMeasured=1000/frameMs;
    }

    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int vk);
    static bool KeyDown(int vk)=>vk!=0&&(GetAsyncKeyState(vk)&0x8000)!=0;
}
