
// Freeze hostile enemies for calm weakspot testing (F3). Uses AActor::CustomTimeDilation on the
// enemy pawn and its AI controller: movement, animation, actions/attacks and AI decisions all run
// on scaled delta time, so a near-zero dilation pauses them in place while their collision (and
// therefore hits and weakspots) stays where the pose froze.
// Dormant enemies are not touched until their AI controller has a CurrentTarget (aggro), plus a
// delay so emerge/spawn animations finish first. Dying enemies are released so death plays out.
internal sealed partial class GameOverlay
{
    bool freezeEnemies,freezeIncludeIdle,prevFreeze;
    float freezeDelay=3f;
    // Recognisable sentinel: leftovers from a killed overlay are found and restored on next start.
    const float FrozenDilation=0.000123f;
    int offDilation=-1,offController=-1,offCurrentTarget=-1;
    sealed record FrozenActor(ulong Slot,float Original,string Label);
    readonly Dictionary<nint,FrozenActor> frozen=new();
    readonly Dictionary<nint,long> aggroSince=new();
    readonly HashSet<nint> freezeLeftoverChecked=new();
    long nextFreezeScan;

    void FreezeTick(UObj playerPawn)
    {
        nint player=playerPawn.Address;var playerFaction=playerPawn.Get<nint>("Faction");
        long now=Environment.TickCount64;
        if(prevFreeze&&!freezeEnemies)UnfreezeAll("freeze OFF");
        prevFreeze=freezeEnemies;
        if(now<nextFreezeScan)return;
        nextFreezeScan=now+100;
        try
        {
            foreach(var enemy in characterCandidates)
            {
                if(enemy==player||!UE.IsType(enemy,"CharacterGunfire"))continue;
                if(!ResolveFreezeOffsets(enemy))return;
                var ctrl=Ptr(enemy+offController);
                if(!freezeEnemies){ClearLeftoverFreeze(enemy,ctrl);continue;}
                var ch=UObj.Of(enemy);
                // Neutral NPCs (vendors, Ward 13 residents) have their own faction but must stay interactive.
                if(ch.Get<nint>("Faction")==playerFaction||ActorLabel(enemy).Contains("_NPC_"))continue;
                if(ch.Get<float>("HealthNormalized")<=0){Unfreeze(enemy);Unfreeze(ctrl);aggroSince.Remove(enemy);continue;}
                bool aggro=freezeIncludeIdle||ctrl!=0&&offCurrentTarget>0&&UE.IsType(ctrl,"AIControllerGunfire")&&Read<ulong>(ctrl+offCurrentTarget)!=0;
                if(!aggro&&!frozen.ContainsKey(enemy)){aggroSince.Remove(enemy);continue;}
                if(!aggroSince.TryGetValue(enemy,out long since))aggroSince[enemy]=since=now;
                if(!freezeIncludeIdle&&now-since<freezeDelay*1000)continue;
                Freeze(enemy,ActorLabel(enemy));
                if(ctrl!=0)Freeze(ctrl,ActorLabel(enemy)+" AI");
            }
        }
        catch(Exception e){Once("freeze:"+e.Message,"FREEZE error: "+e.GetType().Name+": "+e.Message);}
        frozenCount=frozen.Count(f=>!f.Value.Label.EndsWith(" AI"));
        if(freezeEnemies&&now>=nextFreezeStatus)
        {
            nextFreezeStatus=now+5000;
            Log($"FREEZE status: {aggroSince.Count} aggroed, {frozen.Count(f=>!f.Value.Label.EndsWith(" AI"))} enemies frozen (delay {freezeDelay:F1} s{(freezeIncludeIdle?", idle included":"")})");
        }
    }
    long nextFreezeStatus;
    volatile int frozenCount; // published for the menu (render thread)

    string ActorLabel(nint actor)=>ObjectName(Ptr(actor+UE.ObjClass));

    bool ResolveFreezeOffsets(nint enemy)
    {
        if(offDilation>0&&offController>0)return true;
        offDilation=UE.Offset(enemy,"CustomTimeDilation");
        offController=UE.Offset(enemy,"Controller");
        // CurrentTarget lives on AIControllerGunfire; resolve it from the first such controller seen.
        foreach(var e in characterCandidates)
        {
            var c=Ptr(e+offController);
            if(c==0||!UE.IsType(c,"AIControllerGunfire"))continue;
            offCurrentTarget=UE.Offset(c,"CurrentTarget");
            break;
        }
        Log($"FREEZE offsets: CustomTimeDilation +{offDilation:X}, Controller +{offController:X}, CurrentTarget +{offCurrentTarget:X}");
        if(offDilation<=0||offDilation>0x1000||offController<=0||offController>0x1000){Once("freeze-offsets","FREEZE unavailable: unexpected actor layout");offDilation=offController=-1;return false;}
        return true;
    }

    void Freeze(nint actor,string label)
    {
        if(actor==0)return;
        float current=Read<float>(actor+offDilation);
        if(!frozen.ContainsKey(actor))
        {
            frozen[actor]=new(UE.SlotKey(actor),current==FrozenDilation?1f:current,label);
            Log($"FREEZE {label} @{actor:X} (dilation {current} -> {FrozenDilation})");
        }
        // Re-apply if the game reset it (e.g. a scripted slow-motion ending).
        if(current!=FrozenDilation)Mem.Write(actor+offDilation,FrozenDilation);
    }

    void Unfreeze(nint actor)
    {
        if(actor==0||!frozen.Remove(actor,out var f))return;
        if(!UE.LiveInSlot(actor,f.Slot))return;
        Mem.Write(actor+offDilation,f.Original);
    }

    void UnfreezeAll(string reason)
    {
        int restored=0,total=frozen.Count;
        foreach(var (actor,f) in frozen.ToArray())
        {
            try{if(UE.LiveInSlot(actor,f.Slot)){Mem.Write(actor+offDilation,f.Original);restored++;}}
            catch(Exception e){Log($"FREEZE restore {f.Label}: {e.Message}");}
        }
        frozen.Clear();aggroSince.Clear();
        if(total>0)Log($"FREEZE released ({reason}): {restored}/{total} actors restored");
    }

    // An enemy still at the sentinel dilation while freezing is off was frozen by an overlay that died.
    void ClearLeftoverFreeze(nint enemy,nint ctrl)
    {
        if(!freezeLeftoverChecked.Add(enemy))return;
        foreach(var a in new[]{enemy,ctrl})
        {
            if(a==0||Read<float>(a+offDilation)!=FrozenDilation)continue;
            Mem.Write(a+offDilation,1f);
            Log($"CLEANUP leftover freeze {ActorLabel(a)} @{a:X}");
        }
    }
}
