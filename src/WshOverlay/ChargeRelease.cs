using System.Runtime.InteropServices;

// Auto-release for draw/overdraw charge weapons (Monorail). From Weapon_Monorail's blueprint:
//   draw progress = clamp(GetWindupTime()*GetWindupProgress() / GetScaledBaseDrawTime(), 0, 1)
//   full charge at elapsed == GetScaledBaseDrawTime() (3.0 s base, scaled by charge-speed stats/mods);
//   past that the weapon overdraws: more damage, and every ExplodeCheckInterval (0.2 s) TryExplode rolls
//   ExplodeChanceCurve(GetOverdrawProgress()). Releasing within a few tens of ms after full charge gives
//   full damage with no explosion roll.
// The world loop polls those functions on the game thread and publishes a release deadline; the fast
// aim loop sends a synthetic left-button release at the deadline (the game sees a normal release and
// fires). One release per charge: re-armed once the weapon reports no windup (the shot went off).
internal sealed partial class GameOverlay
{
    long chargeDeadlineUs;                 // aimClock microseconds; 0 = none (written by world, read by aim loop)
    volatile bool chargeReleased;          // synthetic release sent for the current charge
    bool chargePollInFlight;long chargeNextPollMs;
    readonly Dictionary<nint,bool> chargeClassOk=new();
    string chargeLogged;

    long AimMicros=>aimClock.Elapsed.Ticks/10;

    static double ReturnValue(UFn fn,byte[] data)
    {
        var p=fn.Params.FirstOrDefault(x=>(x.Value.Flags&0x400)!=0);
        if(p.Key==null)p=fn.Params.FirstOrDefault(x=>(x.Value.Flags&0x100)!=0);
        if(p.Key==null)return double.NaN;
        return p.Value.Size==8?BitConverter.ToDouble(data,p.Value.Offset):BitConverter.ToSingle(data,p.Value.Offset);
    }

    // World loop: called every tick from AimUpdateCore.
    void ChargeTick()
    {
        var c=AimCfg;long now=NowMs;
        if(!c.AutoRelease||GameThread==null||!GameThread.Ready){Volatile.Write(ref chargeDeadlineUs,0);return;}
        var weapons=aimWeapons.Where(IsChargeWeapon).ToArray(); // class check is cached
        if(weapons.Length==0)return;
        bool lmb=KeyDown(0x01);
        if(!lmb&&!chargeReleased){Volatile.Write(ref chargeDeadlineUs,0);chargeLogged=null;return;}
        if(chargePollInFlight||now<chargeNextPollMs)return;
        var calls=new List<GameThread.Call>();var fns=new List<(nint W,UFn P,UFn T,UFn B)>();
        foreach(var w in weapons)
        {
            var cls=Ptr(w+UE.ObjClass);
            UFn progress=UFn.Get(cls,"GetWindupProgress"),time=UFn.Get(cls,"GetWindupTime"),draw=UFn.Get(cls,"GetScaledBaseDrawTime");
            calls.Add(new(w,progress,progress.New().Data));calls.Add(new(w,time,time.New().Data));calls.Add(new(w,draw,draw.New().Data));
            fns.Add((w,progress,time,draw));
        }
        chargePollInFlight=true;chargeNextPollMs=now+5;
        GameThread.Enqueue(calls,read=>
        {
            chargePollInFlight=false;
            double sampleS=aimClock.Elapsed.TotalSeconds;
            for(int i=0;i<fns.Count;i++)
            {
                var (w,fp,ft,fb)=fns[i];
                double progress=ReturnValue(fp,read(i*3)),windup=ReturnValue(ft,read(i*3+1)),full=ReturnValue(fb,read(i*3+2));
                if(!(progress>0)){if(chargeReleased){chargeReleased=false;chargeLogged=null;}continue;}
                if(chargeReleased||!(windup>0)||!(full>0))continue;
                double remaining=full-windup*progress;
                long deadline=(long)((sampleS+remaining+AimCfg.AutoReleaseMarginMs/1000.0)*1e6);
                Volatile.Write(ref chargeDeadlineUs,Math.Max(1,deadline));
                var tag=$"{w:X}";
                if(chargeLogged!=tag)
                {
                    chargeLogged=tag;
                    Log($"CHARGE {ObjectName(Ptr(w+UE.ObjClass))}: full charge at {full:F2} s (windup {windup:F2} s total), {remaining*1000:F0} ms left -> auto-release at +{AimCfg.AutoReleaseMarginMs:F0} ms");
                }
            }
        });
    }

    bool IsChargeWeapon(nint weapon)
    {
        var cls=Ptr(weapon+UE.ObjClass);if(cls==0)return false;
        if(chargeClassOk.TryGetValue(cls,out bool ok))return ok;
        try{UFn.Get(cls,"GetScaledBaseDrawTime");UFn.Get(cls,"GetWindupProgress");UFn.Get(cls,"GetWindupTime");ok=true;}
        catch{ok=false;}
        chargeClassOk[cls]=ok;
        if(ok)Log($"CHARGE weapon with draw/overdraw found: {ObjectName(cls)} | {UFn.Get(cls,"GetWindupProgress").Describe()} | {UFn.Get(cls,"GetWindupTime").Describe()} | {UFn.Get(cls,"GetScaledBaseDrawTime").Describe()}");
        return ok;
    }

    // Fast aim loop: send the release at the deadline (only while the button is actually held).
    void ChargeStep()
    {
        long deadline=Volatile.Read(ref chargeDeadlineUs);
        if(deadline==0||chargeReleased)return;
        long now=AimMicros;
        if(now<deadline)return;
        Volatile.Write(ref chargeDeadlineUs,0);
        if(!KeyDown(0x01))return;
        chargeReleased=true;
        mouse_event(0x0004/*MOUSEEVENTF_LEFTUP*/,0,0,0,0);
        Log($"CHARGE auto-release ({(now-deadline)/1000.0:F1} ms after the deadline)");
    }
    [DllImport("user32.dll")]static extern void mouse_event(uint flags,int dx,int dy,uint data,nint extra);
}
