// Aim assist + recoil control menus (drawn inside the main window's tabs) and their hotkeys.
// Every change is saved to aim_config.json shortly after the last edit (see AimUpdate).
internal sealed partial class GameOverlay
{
    void AimChanged(){aimDirty=true;aimSaveAt=NowMs+750;}
    void AimHotkeys(bool toggleAim,bool toggleRcs)
    {
        var c=AimCfg;
        if(toggleAim){c.Enabled=!c.Enabled;AimChanged();Notify("Aim assist",c.Enabled);}
        if(toggleRcs){c.RcsEnabled=!c.RcsEnabled;AimChanged();Notify("Recoil control",c.RcsEnabled);}
    }

    void AimTab()
    {
        var c=AimCfg;bool ch=false;
        ch|=gui.Toggle("Aim assist",ref c.Enabled,"F5","Turns your view toward the best target while the aim key is held.");
        AimStatus();
        gui.BeginDisabled(!c.Enabled);
        gui.SeparatorText("Basics");
        ch|=KeyCombo("Aim key",ref c.Key);
        ch|=KeyCombo("Second key",ref c.Key2);
        ch|=gui.Checkbox("Press to toggle (instead of hold)",ref c.Toggle);
        ch|=gui.Combo("Aim at",ref c.Region,AimNames.Regions);
        ch|=gui.Combo("Pick target",ref c.TargetMode,AimNames.TargetModes);
        ch|=gui.SliderFloat("Field of view",ref c.FovDeg,0.5f,60,"%.1f deg");gui.Tip("Only targets within this angle of your crosshair are considered.");
        ch|=gui.SliderFloat("Smoothness",ref c.Smooth,1,30,"%.1f");gui.Tip("1 snaps instantly. Higher values move the view more gently.");
        ch|=gui.Checkbox("Show field-of-view circle",ref c.DrawFov);
        ch|=gui.Checkbox("Mark the locked target",ref c.DrawTarget);
        ch|=gui.Checkbox("Only visible targets",ref c.VisibleOnly);gui.Tip("Uses the game's own line-of-sight trace, so targets behind walls are ignored.");

        gui.SeparatorText("Advanced");
        if(gui.CollapsingHeader("Targeting"))
        {
            gui.Indent();
            ch|=Meters("Max distance",ref c.MaxDistance,5,200);
            ch|=gui.Checkbox("Shrink FOV for distant targets",ref c.DynamicFov);
            if(c.DynamicFov)
            {
                ch|=Meters("Full FOV up to",ref c.DynFovNear,1,50);
                ch|=Meters("Smallest FOV at",ref c.DynFovFar,5,150);
                ch|=gui.SliderFloat("Smallest FOV scale",ref c.DynFovFarScale,0.1f,1,"%.2f");
            }
            if(c.VisibleOnly&&gui.Combo("Visibility test",ref c.LosChannel,AimNames.LosChannels))
            {ch=true;WorkerDo(()=>{aimLosInit=0;aimLos.Clear();});}
            ch|=gui.Checkbox("Skip weakspots hidden behind armor",ref c.SkipArmorOccluded);
            ch|=gui.Checkbox("Stay on the current target",ref c.Sticky);gui.Tip("Keep the locked target while it stays valid instead of jumping to a better one.");
            if(c.Sticky)
            {
                ch|=gui.SliderFloat("Stay range",ref c.StickyFovMul,1,4,"x%.2f FOV");
                ch|=gui.Checkbox("Hold the target until the key is released",ref c.LockUntilRelease);
            }
            if(c.TargetMode==2)
            {
                ch|=gui.SliderFloat("Low-health bonus",ref c.LowHealthBonus,0,2,"%.2f");
                ch|=gui.SliderFloat("Assumed spread",ref c.SpreadDeg,0.1f,5,"%.2f deg");
                ch|=gui.SliderFloat("Angle penalty",ref c.AnglePenalty,0,5,"%.2f");
            }
            ch|=gui.Checkbox("Always active (no key needed)",ref c.AlwaysOn);
            gui.Unindent();
        }
        if(gui.CollapsingHeader("Movement"))
        {
            gui.Indent();
            ch|=gui.Combo("Curve",ref c.Curve,AimNames.Curves);
            if(c.Curve==0)ch|=gui.SliderFloat("Speed at smoothness 1",ref c.LinearSpeed,30,2000,"%.0f deg/s");
            if(c.Curve>=2)ch|=gui.SliderFloat("Approach time per step",ref c.CurveTimePerSmooth,0.005f,0.2f,"%.3f s");
            if(c.Curve==3)
            {
                ch|=gui.SliderFloat("Bezier x1",ref c.BezierX1,0,1,"%.2f");ch|=gui.SliderFloat("Bezier y1",ref c.BezierY1,-0.5f,1.5f,"%.2f");
                ch|=gui.SliderFloat("Bezier x2",ref c.BezierX2,0,1,"%.2f");ch|=gui.SliderFloat("Bezier y2",ref c.BezierY2,-0.5f,1.5f,"%.2f");
            }
            if(c.Curve==4){ch|=gui.SliderFloat("Path curvature",ref c.Curvature,0,1,"%.2f");ch|=gui.SliderFloat("Speed variation",ref c.SpeedNoise,0,0.5f,"%.2f");}
            ch|=gui.SliderFloat("Vertical smoothness",ref c.SmoothPitchMul,0.25f,4,"x%.2f");
            ch|=gui.SliderFloat("Horizontal smoothness",ref c.SmoothYawMul,0.25f,4,"x%.2f");
            ch|=gui.SliderFloat("Max turn speed",ref c.MaxSpeed,30,3000,"%.0f deg/s");
            ch|=gui.SliderFloat("Deadzone",ref c.DeadzoneDeg,0,1,"%.2f deg");
            ch|=gui.Checkbox("Follow moving targets",ref c.FeedForward);gui.Tip("Adds the target's own motion, so smoothing shapes only the approach and not the tracking.");
            if(c.FeedForward)ch|=gui.SliderFloat("Follow strength",ref c.FeedForwardGain,0,1.5f,"%.2f");
            ch|=gui.Checkbox("Gentler near the crosshair",ref c.DynamicByAngle);
            if(c.DynamicByAngle){ch|=gui.SliderFloat("On target",ref c.DynNearMul,0.25f,4,"x%.2f");ch|=gui.SliderFloat("At FOV edge",ref c.DynFarMul,0.25f,4,"x%.2f");}
            ch|=gui.Checkbox("Scale by distance",ref c.DynamicByDistance);
            if(c.DynamicByDistance){ch|=gui.SliderFloat("Near (5 m)",ref c.DistNearMul,0.25f,4,"x%.2f");ch|=gui.SliderFloat("Far (max distance)",ref c.DistFarMul,0.25f,4,"x%.2f");}
            ch|=gui.Checkbox("Ease in after locking",ref c.DynamicByTime);
            if(c.DynamicByTime){ch|=gui.SliderFloat("Start",ref c.RampStartMul,0.25f,5,"x%.2f");ch|=gui.SliderFloat("Ease-in time",ref c.RampTimeMs,0,2000,"%.0f ms");}
            gui.Unindent();
        }
        if(gui.CollapsingHeader("Feel"))
        {
            gui.Indent();
            ch|=gui.SliderFloat("Reaction (min)",ref c.ReactionMinMs,0,500,"%.0f ms");
            ch|=gui.SliderFloat("Reaction (max)",ref c.ReactionMaxMs,0,800,"%.0f ms");
            if(c.ReactionMaxMs<c.ReactionMinMs)c.ReactionMaxMs=c.ReactionMinMs;
            ch|=gui.SliderFloat("Target switch delay",ref c.SwitchDelayMs,0,1500,"%.0f ms");
            ch|=gui.SliderFloat("Re-acquire lost target within",ref c.RecallMs,0,5000,"%.0f ms");
            ch|=gui.SliderFloat("Aim point spread",ref c.PointSpread,0,1,"%.2f");gui.Tip("Random offset per lock, as a fraction of the targeted part's size.");
            ch|=gui.SliderFloat("Jitter",ref c.JitterDeg,0,0.5f,"%.3f deg");
            if(c.JitterDeg>0)ch|=gui.SliderFloat("Jitter frequency",ref c.JitterHz,0.5f,10,"%.1f Hz");
            ch|=gui.Checkbox("Pause when I flick away",ref c.MouseOverride);gui.Tip("Moving the mouse quickly away from the target pauses the assist briefly.");
            if(c.MouseOverride){ch|=gui.SliderFloat("Flick speed",ref c.OverrideDegPerSec,30,1000,"%.0f deg/s");ch|=gui.SliderFloat("Pause for",ref c.OverridePauseMs,50,1500,"%.0f ms");}
            gui.Unindent();
        }
        if(gui.CollapsingHeader("Prediction"))
        {
            gui.Indent();
            ch|=gui.Checkbox("Lead moving targets",ref c.Prediction);
            if(c.Prediction)
            {
                ch|=gui.SliderFloat("Latency",ref c.LatencyMs,0,150,"%.0f ms");
                float speed=c.ProjectileSpeed/100;
                if(gui.SliderFloat("Projectile speed",ref speed,0,300,speed<0.5f?"hitscan":"%.0f m/s")){c.ProjectileSpeed=speed<0.5f?0:speed*100;ch=true;}
            }
            ch|=gui.SliderFloat("Keep last visibility for",ref c.LosStaleMs,0,3000,"%.0f ms");gui.Tip("How long a previous line-of-sight answer is trusted while a new trace is pending.");
            gui.Unindent();
        }
        gui.EndDisabled();
        gui.AddSpacing();
        if(gui.ConfirmButton("Reset aim settings","Click again to reset every aim and recoil setting"))
        {var path=c.Path;aimCfg=new AimConfig{Path=path};ch=true;Notify("Aim settings reset");}
        if(ch)AimChanged();
    }
    void AimStatus()
    {
        var c=AimCfg;
        if(!c.Enabled)return;
        gui.SameLine();
        if(aimEngaged&&aimTarget!=0)gui.Text($"locked: {aimTargetName} / {aimLabel}",Gui.Good);
        else if(aimEngaged)gui.Text("searching",Gui.Accent);
        else gui.TextDisabled(c.AlwaysOn?"always active":$"hold {KeyName(c.Key)}");
    }

    // Recoil control and charge-weapon release (Combat tab).
    void RecoilSection()
    {
        var c=AimCfg;bool ch=false;
        ch|=gui.Toggle("Recoil control",ref c.RcsEnabled,"F4","Counters your weapon's recoil while you fire.");
        if(c.RcsEnabled&&rcsActive){gui.SameLine();gui.Text("active",Gui.Good);}
        if(c.RcsEnabled)
        {
            gui.Indent();
            ch|=Percent("Vertical",ref c.RcsPitch);
            ch|=Percent("Horizontal",ref c.RcsYaw);
            ch|=gui.SliderFloat("Smoothing",ref c.RcsSmooth,1,20,"%.1f");gui.Tip("1 counters instantly. Higher values lag behind for a softer feel.");
            ch|=gui.Checkbox("Count left mouse as firing",ref c.RcsUseLmb);
            gui.Unindent();
        }
        ch|=gui.Toggle("Auto-release charge weapons",ref c.AutoRelease,null,"Releases fire as soon as a charge weapon (e.g. Monorail) is fully charged.");
        if(c.AutoRelease)
        {
            gui.Indent();
            ch|=gui.SliderFloat("Release after full charge",ref c.AutoReleaseMarginMs,0,150,"%.0f ms");
            gui.Tip("Overcharge can trigger an explosion every 0.2 s, so keep this well below 200 ms.");
            gui.Unindent();
        }
        if(ch)AimChanged();
    }


    bool Meters(string label,ref float centimetres,float min,float max)
    {
        float m=centimetres/100;
        if(!gui.SliderFloat(label,ref m,min,max,"%.0f m"))return false;
        centimetres=m*100;return true;
    }
    bool Percent(string label,ref float fraction)
    {
        float p=fraction*100;
        if(!gui.SliderFloat(label,ref p,0,100,"%.0f%%"))return false;
        fraction=p/100;return true;
    }
    static string KeyName(int vk)
    {
        foreach(var k in AimNames.Keys)if(k.Vk==vk)return k.Name;
        return $"VK 0x{vk:X2}";
    }
    bool KeyCombo(string label,ref int vk)
    {
        int v=vk;int idx=Array.FindIndex(AimNames.Keys,k=>k.Vk==v);
        var names=AimNames.Keys.Select(k=>k.Name).ToArray();
        if(idx<0){names=[..names,$"VK 0x{vk:X2}"];idx=names.Length-1;}
        if(!gui.Combo(label,ref idx,names))return false;
        if(idx<AimNames.Keys.Length)vk=AimNames.Keys[idx].Vk;
        return true;
    }
}
