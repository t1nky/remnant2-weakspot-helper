using System.Text.Json;
using System.Text.Json.Serialization;

// Aim assist + recoil control settings, persisted as aim_config.json next to the exe.
// Angles are degrees, distances centimetres, times milliseconds unless the name says otherwise.
internal sealed class AimConfig
{
    // --- Aim assist: activation ---
    public bool Enabled=false;           // off until the user turns it on (F5)
    public int Key=0x02;              // VK_RBUTTON (aim down sights)
    public int Key2=0x05;             // VK_XBUTTON1 (mouse 4); 0 = none
    public bool Toggle=false;         // false: hold key; true: key toggles engagement
    public bool AlwaysOn=false;       // engaged without a key (testing / controller play)

    // --- Targeting ---
    public int TargetMode=0;          // AimTargetModes
    public int Region=0;              // AimRegions
    public float FovDeg=8;            // angular radius around the crosshair
    public bool DrawFov=true;
    public bool DynamicFov=false;     // shrink the FOV for distant targets
    public float DynFovNear=800,DynFovFar=4000,DynFovFarScale=0.5f;
    public float MaxDistance=6000;
    public bool VisibleOnly=true;     // game line trace (Weapon channel) to the aim point
    public int LosChannel=0;          // 0 Weapon (bullets), 1 Visibility
    public bool SkipArmorOccluded=true; // weakspot hidden behind the same enemy's armor plate
    public bool Sticky=true;          // keep the locked target while it stays valid
    public float StickyFovMul=1.6f;   // locked target may drift this far outside the FOV
    public bool LockUntilRelease=false;
    public float LowHealthBonus=0.5f; // "most damage": up to +50% score for nearly dead enemies
    public float SpreadDeg=1.0f;      // "most damage": assumed spread for hit probability
    public float AnglePenalty=1.0f;   // "most damage": score / (1 + penalty*angle/fov)

    // --- Smoothing ---
    public int Curve=1;               // AimCurves
    public float Smooth=6;            // >=1; 1 = instant
    public float SmoothPitchMul=1.0f,SmoothYawMul=1.0f; // per-axis smoothing multipliers
    public float LinearSpeed=360;     // deg/s at Smooth=1 for the linear curve
    public float CurveTimePerSmooth=0.05f; // seconds of planned approach per unit Smooth (ease/bezier/human)
    public float BezierX1=0.25f,BezierY1=0.1f,BezierX2=0.25f,BezierY2=1f;
    public float MaxSpeed=720;        // deg/s cap on aim movement
    public bool FeedForward=true;     // add the target's own angular velocity: smoothing shapes the approach, not the tracking
    public float FeedForwardGain=1.0f; // 1 = match the target's motion exactly
    public float DeadzoneDeg=0.05f;
    public bool DynamicByAngle=true;
    public float DynNearMul=1.5f,DynFarMul=0.75f;   // smoothing multiplier on target / at FOV edge
    public bool DynamicByDistance=false;
    public float DistNearMul=1.0f,DistFarMul=1.5f;  // at 5 m / at MaxDistance
    public bool DynamicByTime=true;
    public float RampStartMul=2.0f,RampTimeMs=250;  // starts slower, reaches 1x after RampTime

    // --- Humanization ---
    public float ReactionMinMs=60,ReactionMaxMs=140;
    public float SwitchDelayMs=200;
    public float RecallMs=1500;       // a lost target (left FOV / lost sight) returning within this is re-acquired instantly
    public float LosStaleMs=1000;     // keep the last line-of-sight answer this long while a fresh trace is pending
    public float JitterDeg=0.0f,JitterHz=3;
    public float PointSpread=0.25f;   // random aim offset per lock, fraction of the part radius
    public float Curvature=0.25f;     // humanized curve: path bend (radians at lock start)
    public float SpeedNoise=0.15f;    // humanized curve: per-frame speed variation
    public bool MouseOverride=true;   // pause when the player flicks away from the target
    public float OverrideDegPerSec=200,OverridePauseMs=250;

    // --- Prediction ---
    public bool Prediction=true;
    public float LatencyMs=30;        // extrapolate for overlay/game latency
    public float ProjectileSpeed=0;   // cm/s; 0 = hitscan

    // --- Recoil control ---
    public bool RcsEnabled=false;        // off until the user turns it on (F4)
    public float RcsPitch=0.8f,RcsYaw=0.6f; // fraction of the game's recoil removed (0..1)
    public float RcsSmooth=2;         // 1 = instant counter, higher = lagging/softer
    public bool RcsUseLmb=true;
    public bool AutoRelease=false;     // charge weapons (Monorail): release fire at full charge + margin
    public float AutoReleaseMarginMs=50;       // firing = weapon fire state OR left mouse held

    // --- Display ---
    public bool DrawTarget=true;      // mark the locked target and aim point

    [JsonIgnore] public string Path="";
    static readonly JsonSerializerOptions json=new(){IncludeFields=true,WriteIndented=true};

    public static AimConfig Load(string path,Action<string> log)
    {
        try
        {
            if(File.Exists(path))
            {
                var c=JsonSerializer.Deserialize<AimConfig>(File.ReadAllText(path),json)??new();
                c.Path=path;log($"AIM config loaded from {path}");return c;
            }
        }
        catch(Exception e){log($"AIM config unreadable ({e.Message}); using defaults");}
        var d=new AimConfig{Path=path};d.Save(log);return d;
    }
    public void Save(Action<string> log)
    {
        try{File.WriteAllText(Path,JsonSerializer.Serialize(this,json));}
        catch(Exception e){log("AIM config save failed: "+e.Message);}
    }
}

internal static class AimNames
{
    public static readonly string[] TargetModes=["Closest to crosshair","Closest distance","Most damage (expected)","Weakspot priority","Lowest health"];
    public static readonly string[] Regions=["Weakspot > Head > Body","Weakspot > Body","Head > Body","Body (aim bone)","Weakspot only","Highest multiplier part","Part closest to crosshair"];
    public static readonly string[] Curves=["Linear (constant speed)","Exponential (classic)","Ease in-out","Bezier (custom)","Humanized (curved Bezier)"];
    public static readonly string[] LosChannels=["Weapon (bullets)","Visibility"];
    public static readonly (string Name,int Vk)[] Keys=
    [
        ("None",0),("Right mouse",0x02),("Left mouse",0x01),("Middle mouse",0x04),("Mouse 4",0x05),("Mouse 5",0x06),
        ("Left Shift",0xA0),("Left Ctrl",0xA2),("Left Alt",0xA4),("Caps Lock",0x14),("E",0x45),("Q",0x51),("V",0x56),("X",0x58),("Z",0x5A)
    ];
}
