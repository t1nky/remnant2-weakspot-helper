using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

// Aim assist + recoil control.
// Remnant 2's view rotation is owned by the player's RemnantStateCameraComponent: mouse input and the
// weapon recoil both add to its rotation at +0x1318 (pitch) / +0x1320 (yaw), and ControlRotation is
// copied from it every frame, so a ControlRotation write would be overwritten. Aim and RCS therefore
// write the camera component's rotation (one 16-byte write per step).
internal sealed partial class GameOverlay
{
    // RemnantStateCameraComponent layout (this build; validated against the POV before every write).
    const int CamPitch=0x1318,CamYaw=0x1320;
    const int RecoilState=0x22E1;       // 0 idle, 1 kicking, 2 recovering
    const int RecoilOffset=0x22E8;      // double[2]: accumulated recoil (pitch, yaw) still on the view
    const int WeaponFireState=0xA18;    // ARangedWeapon: IsFiring() == state in 3..6
    const int CharacterAimBone=0x6B8;   // ACharacterGunfire::AimBone (FName)
    const int HitTraceStart=0x70,HitBlocking=0xAD,HitActor=0xB8,HitComponent=0xD0;

    sealed class AimPoint
    {
        public string Region="",Label="";
        public string Key="";   // unique per point: many shapes share one location label
        public Vector3 Pos;public float Scalar=1,Radius=15;
        public LiveShape Shape;
        // Pose recipe for the fast aim loop: world = Transform(LocalCenter, Local * (bone or component)).
        public nint Anchor;public int Bone=-1;public Matrix4x4 Local=Matrix4x4.Identity;public Vector3 LocalCenter;
    }
    sealed class AimEnemy
    {
        public nint Actor;public string Name="";public float Health;public Vector3 Root;
        public List<AimPoint> Points=new();public List<LiveShape> Shapes;public float WeakT,ResistT,HealthRaw;public Vector3 Shift;
    }
    sealed record AimChoice(AimEnemy Enemy,AimPoint Point,float Angle,float Dist,float Score,float Expected,float Fov);

    AimConfig aimCfg;
    AimConfig AimCfg=>aimCfg;
    readonly List<AimEnemy> aimEnemies=new();
    int aimCollectFrame=-1;
    bool aimEngaged,aimToggled,aimKeyWas,aimDirty;
    long aimSaveAt;
    readonly Random aimRng=new();
    readonly Dictionary<string,long> aimNextLog=new();
    readonly Dictionary<nint,(Vector3 Pos,long T,Vector3 Vel)> aimVel=new();
    readonly Dictionary<string,(bool Visible,long T)> aimLos=new();
    readonly HashSet<string> aimLosLogged=new();
    readonly Dictionary<string,(int Head,string HeadName)> aimHeads=new();
    readonly Dictionary<nint,(List<LiveShape> Shapes,Vector3 Root)> aimShapeRoot=new();
    readonly Dictionary<nint,float> aimHpMax=new();
    // Lock state.
    nint aimTarget;string aimTargetName="",aimLabel="",aimPointKey="";
    long aimLockStart,aimReactUntil,aimSwitchUntil,aimPauseUntil,aimLastUpdate;
    Vector3 aimSpread;float aimCurveSign=1,aimNoisePhase1,aimNoisePhase2,aimNoiseF1=1,aimNoiseF2=1.7f;
    double aimPlanStart;float aimPlanE0;bool aimPlanDone;
    Vector2 aimLastError;bool aimWroteLast,aimHaveLast;double aimLastWrittenP,aimLastWrittenY;Vector2 aimLastRecoil;
    // Feed-forward: angular velocity of the tracked aim point (deg/s), reset on every new lock/part.
    nint aimLostTarget;long aimLostAt;string aimLostName="";
    readonly Dictionary<nint,ulong> aimWeakKeys=new();
    const float LosReachCm=75;
    readonly Dictionary<string,Func<string>> aimLosBlocker=new(); // point key -> what blocked its last trace // enemy actor -> FWeakObjectPtr key (index|serial)  // last target lost while the key was held
    string aimFfKey="";Vector2 aimFfAngles,aimFfVel;double aimFfTime,aimLastUpdateS;
    long aimFrames,aimWrites;
    // Camera component / weapons.
    nint aimCam,aimCamOwner;int aimCamBad;
    nint[] aimWeapons=[];long aimWeaponsAt;nint aimWeaponOwner,aimActiveWeapon;
    (float Primary,float Secondary) aimFalloff;nint aimFalloffWeapon;
    // RCS episode.
    bool rcsActive;Vector2 rcsStart,rcsApplied,rcsPrevOffset;byte rcsPrevState;long rcsLastFire,rcsEpisodeStart;int rcsKicks;
    // Line of sight through the game-thread executor.
    nint aimKslCdo;UFn aimTraceFn;byte aimTraceQuery=0;int aimLosInit;bool aimLosValidated;
    long aimLosSubmitted,aimLosNotReadySince;bool aimLosInFlight;
    readonly Stopwatch aimClock=Stopwatch.StartNew();
    double Now=>aimClock.Elapsed.TotalSeconds;
    long NowMs=>aimClock.ElapsedMilliseconds;

    void AimLog(string key,int everyMs,string text)
    {
        long now=NowMs;
        lock(aimNextLog)
        {
            if(everyMs>0&&aimNextLog.TryGetValue(key,out long next)&&now<next)return;
            aimNextLog[key]=now+everyMs;
        }
        Log(text);
    }

    // ---------------------------------------------------------------- collection (from DrawWorld)

    bool AimWantsTargets=>AimCfg.Enabled&&aimEngaged;

    // Enemies with a weakspot: the overlay already built their shapes.
    // Only enemies that could become (or are) the target: inside the aim cone with margin, the locked
    // one, or very close. Everything else is skipped before any per-enemy work.
    bool AimInCone(UObj ch)
    {
        if(ch.Address==aimTarget||ch.Address==aimLostTarget)return true;
        var to=Read<FTransform>(ch.Ptr("Mesh")+0x240).ToMatrixWithScale().Translation-viewOrigin;
        float len=to.Length();if(len<500)return true;
        if(len>AimCfg.MaxDistance+500)return false;
        float cone=Math.Min(89,AimCfg.FovDeg*Math.Max(1,AimCfg.StickyFovMul)+15);
        return Vector3.Dot(to/len,viewForward)>=MathF.Cos(cone*MathF.PI/180);
    }
    void AimCollect(UObj ch,HitLocation[] hits,List<LiveShape> shapes,float weakThreshold,float resistThreshold)
    {
        if(!AimWantsTargets||!AimInCone(ch))return;
        try{AimAddEnemy(ch,hits,shapes,weakThreshold,resistThreshold);}
        catch(Exception e){AimLog("collect:"+e.Message,5000,$"AIM collect {ObjectName(ch.Address)}: {e.GetType().Name}: {e.Message}");}
    }
    // Enemies the overlay skips (no intact weakspot and armor display off): build their shapes here.
    void AimCollectUnmarked(UObj ch,HitLocation[] hits,Vector3 camera,float weakThreshold,float resistThreshold)
    {
        if(!AimWantsTargets||showArmor||hits.Any(h=>!h.Destroyed&&h.IsWeak(weakThreshold,resistThreshold))||!AimInCone(ch))return;
        try
        {
            if(!InResolveRange(ch,camera)||EnemyDistance(ch,camera)>AimCfg.MaxDistance)return;
            AimAddEnemy(ch,hits,ShapesFor(ch,hits,camera,weakThreshold,resistThreshold),weakThreshold,resistThreshold);
        }
        catch(Exception e){AimLog("collect2:"+e.Message,5000,$"AIM collect {ObjectName(ch.Address)}: {e.GetType().Name}: {e.Message}");}
    }

    void AimAddEnemy(UObj ch,HitLocation[] hits,List<LiveShape> shapes,float weakT,float resistT)
    {
        if(aimCollectFrame!=frame){aimEnemies.Clear();aimCollectFrame=frame;}
        var mesh=ch.Ptr("Mesh");if(mesh==0)return;
        var root=Read<FTransform>(mesh+0x240).ToMatrixWithScale().Translation;
        // Far enemies' shapes come from a timed cache: move them with the actor since they were resolved.
        if(!aimShapeRoot.TryGetValue(ch.Address,out var at)||!ReferenceEquals(at.Shapes,shapes))aimShapeRoot[ch.Address]=at=(shapes,root);
        var shift=root-at.Root;if(shift.Length()>500)shift=Vector3.Zero;
        // HealthNormalized is not 0..1 in this build (1000 at full): normalise by the highest value seen.
        float hpRaw=ch.Get<float>("HealthNormalized");
        float hpMax=Math.Max(hpRaw,aimHpMax.GetValueOrDefault(ch.Address));aimHpMax[ch.Address]=hpMax;
        float hp=hpRaw<=1?hpRaw:hpRaw/Math.Max(hpMax,1);
        var e=new AimEnemy{Actor=ch.Address,Name=$"{ActorName(ch)}#{(long)ch.Address&0xFFFF:X4}",Health=hp,HealthRaw=hpRaw,Root=root,Shapes=shapes,WeakT=weakT,ResistT=resistT,Shift=shift};
        var all=ById(hits);
        var gates=new HashSet<ulong>();
        foreach(var weak in hits.Where(h=>!h.Destroyed&&h.IsWeak(weakT,resistT)).DistinctBy(h=>h.Id))
        {
            var weakShapes=shapes.Where(s=>s.Location.Id==weak.Id&&s.Active).ToList();
            if(weakShapes.Count>0)continue;
            foreach(var id in ProtectionResolver.Frontier(weak,all))if(id!=weak.Id)gates.Add(id);
        }
        var seen=new Dictionary<string,int>();
        foreach(var s in shapes)
        {
            if(!s.Active||s.Location.Destroyed)continue;
            bool weak=s.Location.IsWeak(weakT,resistT);
            string region=weak?"weak":gates.Contains(s.Location.Id)?"gate":s.Location.IsResisted(resistT)?"armor":"part";
            var id=$"{s.Location.Name}@{s.Anchor:X}:{s.Socket:X}";int n=seen.GetValueOrDefault(id);seen[id]=n+1;
            e.Points.Add(new(){Region=region,Label=s.Location.Name,Key=$"{id}#{n}",Pos=s.Center+shift,Scalar=s.Location.Scalar,Radius=ShapeRadius(s),Shape=s,
                Anchor=s.Anchor,Bone=s.Bone,Local=s.Geometry.LocalMatrix,LocalCenter=s.Geometry.LocalCenter});
        }
        // Bones: the game's own aim bone (body) and the head.
        var bonesOf=AimBonesOf(ch.Address,mesh);
        if(bonesOf.Head>=0&&BoneWorld(mesh,bonesOf.Head,out var hw))
            e.Points.Add(new(){Region="head",Label="bone:"+bonesOf.HeadName,Key="bone:"+bonesOf.HeadName,Pos=hw.Translation,Scalar=BodyScalar(shapes,hw.Translation),Radius=12,Anchor=mesh,Bone=bonesOf.Head});
        if(bonesOf.Body>=0&&BoneWorld(mesh,bonesOf.Body,out var bw))
            e.Points.Add(new(){Region="body",Label="bone:"+bonesOf.BodyName,Key="bone:"+bonesOf.BodyName,Pos=bw.Translation,Scalar=BodyScalar(shapes,bw.Translation),Radius=25,Anchor=mesh,Bone=bonesOf.Body});
        if(!e.Points.Any(p=>p.Region=="body"))
        {
            var parts=e.Points.Where(p=>p.Region is "part" or "armor").ToList();
            var c=parts.Count>0?parts.Aggregate(Vector3.Zero,(a,p)=>a+p.Pos)/parts.Count:root+new Vector3(0,0,90);
            e.Points.Add(new(){Region="body",Label="center",Key="center",Pos=c,Scalar=1,Radius=25});
        }
        aimEnemies.Add(e);
    }

    // Head / aim-bone indices per enemy mesh: name lookups are cross-process reads, so resolve them once
    // (refreshed every 5 s in case the mesh or aim bone changes).
    readonly Dictionary<(nint,nint),(int Head,string HeadName,int Body,string BodyName,long Until)> aimBoneCache=new();
    (int Head,string HeadName,int Body,string BodyName,long Until) AimBonesOf(nint actor,nint mesh)
    {
        long now=NowMs;
        if(aimBoneCache.TryGetValue((actor,mesh),out var c)&&now<c.Until)return c;
        c=(-1,"",-1,"",now+5000);
        var meshAsset=Ptr(mesh+0x5a8);if(meshAsset==0)meshAsset=Ptr(mesh+0x5b0);
        if(meshAsset!=0&&bones.TryGetValue(ObjectName(meshAsset),out var boneMap))
        {
            var meshName=ObjectName(meshAsset);
            if(!aimHeads.TryGetValue(meshName,out var head))
            {
                var h=boneMap.Keys.Where(n=>n.Contains("head",StringComparison.OrdinalIgnoreCase)&&!n.Contains("end",StringComparison.OrdinalIgnoreCase)&&!n.Contains("twist",StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n=>n.Equals("head",StringComparison.OrdinalIgnoreCase)?0:1).ThenBy(n=>n.Length).FirstOrDefault();
                head=aimHeads[meshName]=(h==null?-1:boneMap[h],h??"");
            }
            var aimBone=Name(Read<ulong>(actor+CharacterAimBone));
            string body=boneMap.ContainsKey(aimBone)?aimBone:new[]{"spine_03","spine_02","Spine2","Spine1","spine","pelvis","Pelvis"}.FirstOrDefault(boneMap.ContainsKey);
            c=(head.Head,head.HeadName,body==null?-1:boneMap[body],body??"",now+5000);
        }
        if(aimBoneCache.Count>512)aimBoneCache.Clear();
        return aimBoneCache[(actor,mesh)]=c;
    }

    // Damage multiplier of the hit location whose shape contains (or is nearest to) a point.
    static float BodyScalar(List<LiveShape> shapes,Vector3 p)
    {
        LiveShape best=null;float bestD=float.MaxValue;
        foreach(var s in shapes){if(!s.Active)continue;float d=Vector3.Distance(s.Center,p);if(d<bestD){bestD=d;best=s;}}
        return best!=null&&bestD<60?best.Location.Scalar:1;
    }
    static float ShapeRadius(LiveShape s)
    {
        var g=s.Geometry;float scale=MathF.Cbrt(MathF.Abs(s.World.GetDeterminant()));
        float r=g.Kind switch
        {
            "Sphere"=>g.Radius,"Sphyl"=>g.Radius+g.Length*0.25f,"Box"=>(g.X+g.Y+g.Z)/6,
            "Convex" when g.VertexData.Length>0=>ConvexRadius(g),_=>15
        };
        return Math.Clamp(r*(scale>0?scale:1),2,200);
    }
    static float ConvexRadius(ShapeGeometry g)
    {
        var lo=new Vector3(float.MaxValue);var hi=new Vector3(float.MinValue);
        foreach(var v in g.VertexData){lo=Vector3.Min(lo,v.Value);hi=Vector3.Max(hi,v.Value);}
        var e=hi-lo;return (e.X+e.Y+e.Z)/6;
    }

    // ---------------------------------------------------------------- per-frame update (end of DrawWorld)

    static Vector3 Forward(float pitch,float yaw)
    {
        float p=pitch*MathF.PI/180,y=yaw*MathF.PI/180;
        return new(MathF.Cos(p)*MathF.Cos(y),MathF.Cos(p)*MathF.Sin(y),MathF.Sin(p));
    }
    static float NormDeg(double a){a%=360;if(a>180)a-=360;if(a<-180)a+=360;return (float)a;}
    static (float Pitch,float Yaw) LookAt(Vector3 from,Vector3 to)
    {
        var d=to-from;return (MathF.Atan2(d.Z,MathF.Sqrt(d.X*d.X+d.Y*d.Y))*180/MathF.PI,MathF.Atan2(d.Y,d.X)*180/MathF.PI);
    }
    static float AngleTo(Vector3 forward,Vector3 from,Vector3 to)
    {
        var d=Vector3.Normalize(to-from);return MathF.Acos(Math.Clamp(Vector3.Dot(forward,d),-1,1))*180/MathF.PI;
    }

    void AimUpdate(UObj pc,UObj player,Vector3 camera,Vector3 rotation,float fov,DrawRecorder draw,Vector2 size)
    {
        try{AimUpdateCore(pc,player,camera,rotation,fov,draw,size);}
        catch(Exception e){AimLog("update:"+e.Message,3000,$"AIM update error: {e.GetType().Name}: {e.Message}");}
        if(aimCollectFrame!=frame)aimEnemies.Clear();
    }

    // World loop (WorkerHz): weapons, camera component, velocities, target selection and line of sight.
    // The per-step work (camera read, fresh target pose, smoothing, RCS, writes) runs in AimStep on the
    // fast aim loop (AimHz). Shared selection/smoothing state is guarded by aimSync.
    readonly object aimSync=new();
    sealed record AimLockInfo(AimChoice Choice,Vector3 Velocity);
    volatile AimLockInfo aimLockInfo;
    void AimUpdateCore(UObj pc,UObj player,Vector3 camera,Vector3 rotation,float fov,DrawRecorder draw,Vector2 size)
    {
        var c=AimCfg;long now=NowMs;
        if(!aimEngaged&&aimTarget!=0)lock(aimSync){AimDrop("key released",false);aimLostTarget=0;}
        var cx=size.X/2;var cy=size.Y/2;
        float fovPx(float deg)=>MathF.Tan(Math.Min(deg,89)*MathF.PI/180)/MathF.Tan(fov*MathF.PI/360)*cx;
        if(c.Enabled&&c.DrawFov)draw.AddCircle(new(cx,cy),fovPx(c.FovDeg),aimEngaged?0x90ffffffu:0x40ffffffu,64,1.2f);
        AimCamera(player.Address);
        var forward=Forward(rotation.X,rotation.Y);
        AimWeapons(player);
        ChargeTick();
        // Velocity tracking for prediction.
        foreach(var e in aimEnemies)
        {
            if(aimVel.TryGetValue(e.Actor,out var v)&&now>v.T)
            {
                float t=(now-v.T)/1000f;var inst=(e.Root-v.Pos)/t;
                if(inst.Length()>3000)inst=v.Vel; // teleport / ragdoll spike
                aimVel[e.Actor]=(e.Root,now,Vector3.Lerp(v.Vel,inst,Math.Clamp(t*8,0.05f,1)));
            }
            else if(!aimVel.ContainsKey(e.Actor))aimVel[e.Actor]=(e.Root,now,Vector3.Zero);
        }
        if(aimShapeRoot.Count>256)foreach(var k in aimShapeRoot.Keys.Where(k=>!aimVel.ContainsKey(k)).ToList())aimShapeRoot.Remove(k);
        if(aimHpMax.Count>512)foreach(var k in aimHpMax.Keys.Where(k=>!aimVel.ContainsKey(k)).ToList())aimHpMax.Remove(k);
        if(aimVel.Count>256)foreach(var k in aimVel.Where(p=>now-p.Value.T>5000).Select(p=>p.Key).ToList())aimVel.Remove(k);
        if(aimEngaged)
        {
            var choices=AimCandidates(camera,forward,fov);
            AimRequestLos(camera,choices);
            AimChoice cur;
            lock(aimSync)
            {
                AimLock(choices,camera,forward,now);
                cur=aimTarget==0?null:choices.FirstOrDefault(x=>x.Enemy.Actor==aimTarget);
                aimLockInfo=cur==null?null:new(cur,aimVel.TryGetValue(cur.Enemy.Actor,out var vel)?vel.Vel:Vector3.Zero);
            }
            if(cur!=null&&c.DrawTarget)
            {
                var point=cur.Point.Pos;
                var sp=ViewMath.WorldToScreen(point,camera,rotation,fov,(int)cx,(int)cy);
                if(Vector3.Dot(point-camera,forward)>1)
                {
                    bool reacting=now<aimReactUntil,paused=now<aimPauseUntil;
                    uint col=reacting?0xff00ffffu:paused?0xffff8000u:0xff2020ffu;
                    draw.AddCircle(sp,6,col,12,2);draw.AddLine(new(cx,cy),sp,col&0x80ffffff,1);
                    draw.AddText(sp+new Vector2(8,-8),col,$"{cur.Enemy.Name} {cur.Point.Region}:{cur.Point.Label} x{cur.Point.Scalar:F2} {cur.Dist/100:F0}m{(reacting?" [react]":paused?" [override]":"")}");
                }
            }
            if(c.DrawTarget&&aimTarget==0&&now<aimSwitchUntil)draw.AddText(new(cx+12,cy+12),0xff00ffffu,"switch delay");
        }
        else aimLockInfo=null;
    }

    // ---------------------------------------------------------------- fast aim loop (AimHz)
    // Thread-confined pose read for the locked point (no shared caches): leader map, read buffer, one bone.
    readonly Dictionary<nint,(nint Pose,int[] Map,long Until)> fastLinks=new();
    bool FastBoneWorld(nint component,int bone,out Matrix4x4 world)
    {
        world=default;long now=NowMs;
        if(!fastLinks.TryGetValue(component,out var link)||now>=link.Until)
        {
            nint pose=component;int[] map=null;var leader=UE.WeakObject(component+0x5b8);
            if(leader!=0)
            {
                int n=Read<int>(component+0x670);var m=Ptr(component+0x668);
                if(n<=0||n>8192||m==0)pose=0;else{var raw=Bytes(m,n*4);map=new int[n];Buffer.BlockCopy(raw,0,map,0,raw.Length);pose=leader;}
            }
            if(fastLinks.Count>64)fastLinks.Clear();
            fastLinks[component]=link=(pose,map,now+1000);
        }
        if(link.Pose==0)return false;
        int index=bone;
        if(link.Map!=null){if(bone>=link.Map.Length)return false;index=link.Map[bone];}
        int readIndex=Read<int>(link.Pose+0x648);if(readIndex is not (0 or 1))return false;
        var header=Bytes(link.Pose+0x600+readIndex*0x10,16);
        var ptr=(nint)BitConverter.ToInt64(header,0);int count=BitConverter.ToInt32(header,8);
        if(index<0||index>=count||ptr==0)return false;
        world=Read<FTransform>(ptr+index*0x60).ToMatrixWithScale()*Read<FTransform>(component+0x240).ToMatrixWithScale();
        return float.IsFinite(world.M41);
    }
    // The locked point where it is right now (the world loop's copy can be ~10 ms old).
    Vector3 FastPoint(AimPoint p)
    {
        try
        {
            if(p.Anchor==0)return p.Pos;
            Matrix4x4 anchor;
            if(p.Bone>=0){if(!FastBoneWorld(p.Anchor,p.Bone,out anchor))return p.Pos;}
            else anchor=Read<FTransform>(p.Anchor+0x240).ToMatrixWithScale();
            return Vector3.Transform(p.LocalCenter,p.Local*anchor);
        }
        catch{return p.Pos;}
    }

    void AimStep()
    {
        var c=AimCfg;long now=NowMs;
        double nowS=aimClock.Elapsed.TotalSeconds;
        float dt=aimLastUpdate==0?1/500f:Math.Clamp((float)(nowS-aimLastUpdateS),1/5000f,0.25f);
        aimLastUpdateS=nowS;
        bool gap=aimLastUpdate==0||now-aimLastUpdate>1000;
        aimLastUpdate=now;aimFrames++;
        if(gap){aimWroteLast=aimHaveLast=false;rcsActive=false;}
        // Activation (polled at the aim rate: no input latency from the world loop).
        bool key=KeyDown(c.Key)||KeyDown(c.Key2);
        if(c.Toggle&&key&&!aimKeyWas){aimToggled=!aimToggled;Log($"AIM toggled {(aimToggled?"ON":"OFF")}");}
        aimKeyWas=key;
        bool engaged=c.Enabled&&(c.AlwaysOn||(c.Toggle?aimToggled:key));
        if(engaged!=aimEngaged)
        {
            if(!engaged)AimLog("engage",0,$"AIM {(engaged?"engaged":"released")} (key {c.Key:X2}/{c.Key2:X2}, mode {AimNames.TargetModes[c.TargetMode]}, region {AimNames.Regions[c.Region]})");
            aimEngaged=engaged;
            if(!engaged)aimLockInfo=null;
        }
        ChargeStep();
        var cam=aimCam;
        if(cam==0||povLocation==0)return;
        Vector3 camera,rotation;
        try{camera=Read<FVector>(povLocation).ToFloats();rotation=Read<FVector>(povRotation).ToFloats();}
        catch{return;}
        var raw=Bytes(cam+CamPitch,16);var camRot=new Vector2((float)BitConverter.ToDouble(raw,0),(float)BitConverter.ToDouble(raw,8));
        // Layout sanity check. The POV trails the camera by a frame, so fast flicks differ by several degrees.
        bool camOk=MathF.Abs(camRot.X-rotation.X)<20&&MathF.Abs(NormDeg(camRot.Y-rotation.Y))<20;
        if(!camOk){aimCamBad++;AimLog("cam-mismatch",3000,$"AIM camera rotation mismatch: cam={camRot.X:F2},{camRot.Y:F2} pov={rotation.X:F2},{rotation.Y:F2} (writes paused)");}
        else aimCamBad=0;
        Vector2 rcsStep=camOk?RcsUpdate(cam,dt,now):default;

        Vector2 aimStep=default;
        var info=aimEngaged?aimLockInfo:null;
        if(info!=null)
        {
            lock(aimSync)
            {
                var cur=info.Choice;
                if(cur.Enemy.Actor==aimTarget)
                {
                    var point=FastPoint(cur.Point)+aimSpread*cur.Point.Radius;
                    if(c.Prediction)
                    {
                        float lead=c.LatencyMs/1000f+(c.ProjectileSpeed>0?cur.Dist/c.ProjectileSpeed:0);
                        point+=info.Velocity*lead;
                    }
                    var (dp,dy)=LookAt(camera,point);
                    // Error against the camera component's live rotation (the POV is a frame behind it).
                    var baseRot=camOk?camRot:new Vector2(rotation.X,rotation.Y);
                    var err=new Vector2(dp-baseRot.X,NormDeg(dy-baseRot.Y));
                    var ff=AimFeedForward(cur,new Vector2(dp,dy),now);
                    if(c.JitterDeg>0)
                    {
                        float t=(float)Now;
                        err+=new Vector2(MathF.Sin(t*c.JitterHz*2*MathF.PI*aimNoiseF1+aimNoisePhase1),MathF.Sin(t*c.JitterHz*2*MathF.PI*aimNoiseF2+aimNoisePhase2))*c.JitterDeg;
                    }
                    // The RCS step is applied in the same write; aim at what remains after it.
                    err-=rcsStep;
                    bool reacting=now<aimReactUntil;
                    if(c.MouseOverride&&aimHaveLast&&camOk&&!reacting)AimOverrideCheck(camRot,err,dt,now);
                    bool paused=now<aimPauseUntil;
                    if(!reacting&&!paused)
                    {
                        aimStep=AimSmooth(err,cur,dt,now);
                        if(c.FeedForward)aimStep+=ff*(c.FeedForwardGain*dt);
                        float cap=c.MaxSpeed*dt;if(aimStep.Length()>cap)aimStep=Vector2.Normalize(aimStep)*cap;
                    }
                    AimLog("track",2000,$"AIM track {cur.Enemy.Name} {cur.Point.Region}:{cur.Point.Label} err=({err.X:F2},{err.Y:F2})° step=({aimStep.X:F3},{aimStep.Y:F3})° ff=({ff.X:F1},{ff.Y:F1})°/s dist={cur.Dist/100:F1}m {(reacting?"REACT ":"")}{(paused?"PAUSED ":"")}curve={AimNames.Curves[c.Curve]} dt={dt*1000:F2}ms");
                    aimLastError=err;
                }
            }
        }

        // One write for aim + recoil compensation.
        var total=aimStep+rcsStep;
        aimWroteLast=false;
        if(camOk&&(total.X!=0||total.Y!=0))
        {
            // Re-read immediately before writing to keep the game's input of this frame.
            var now2=Bytes(cam+CamPitch,16);double rp=BitConverter.ToDouble(now2,0),ry=BitConverter.ToDouble(now2,8);
            double p=Math.Clamp(rp+total.X,-89,89),y=ry+total.Y;
            var buf=new byte[16];BitConverter.GetBytes(p).CopyTo(buf,0);BitConverter.GetBytes(y).CopyTo(buf,8);
            Mem.Write(cam+CamPitch,buf);
            aimWroteLast=true;aimLastWrittenP=p;aimLastWrittenY=y;aimWrites++;
            AimLog("write",5000,$"AIM write cam {rp:F2},{ry:F2} -> {p:F2},{y:F2} (aim {aimStep.X:F3},{aimStep.Y:F3} rcs {rcsStep.X:F3},{rcsStep.Y:F3}); writes={aimWrites}");
        }
        // Camera rotation as this step leaves it: the baseline for measuring the player's own input.
        if(camOk&&!aimWroteLast){aimLastWrittenP=camRot.X;aimLastWrittenY=camRot.Y;}
        aimHaveLast=camOk;
        aimLastRecoil=AimRecoilOffset(cam);
    }

    // Angular velocity of the aim point as seen from the camera (includes both target and player
    // movement). Adding it to the smoothed step removes the steady-state lag of error/smooth tracking.
    Vector2 AimFeedForward(AimChoice cur,Vector2 angles,long now)
    {
        var key=$"{cur.Enemy.Actor:X}:{cur.Point.Key}";double ts=aimClock.Elapsed.TotalSeconds;
        if(key!=aimFfKey||ts-aimFfTime>0.2){aimFfKey=key;aimFfAngles=angles;aimFfTime=ts;aimFfVel=default;return default;}
        float t=(float)(ts-aimFfTime);if(t<=0.0005f)return aimFfVel;
        // At aim rates above the game's frame rate most steps see an unchanged pose: wait for a change.
        if(MathF.Abs(angles.X-aimFfAngles.X)<1e-5f&&MathF.Abs(NormDeg(angles.Y-aimFfAngles.Y))<1e-5f&&t<0.05f)return aimFfVel;
        var inst=new Vector2((angles.X-aimFfAngles.X)/t,NormDeg(angles.Y-aimFfAngles.Y)/t);
        if(inst.Length()>720)inst=aimFfVel; // part switch / teleport spike
        aimFfVel=Vector2.Lerp(aimFfVel,inst,Math.Clamp(t*20,0.1f,1)); // ~50 ms smoothing
        aimFfAngles=angles;aimFfTime=ts;
        return aimFfVel;
    }

    nint AimCamera(nint player)
    {
        if(player==aimCamOwner&&aimCam!=0&&Ptr(aimCam+UE.ObjOuter)==player)return aimCam;
        aimCamOwner=player;aimCam=OwnedComponents(player).FirstOrDefault(x=>UE.IsType(x,"RemnantStateCameraComponent"));
        if(aimCam!=0)Log($"AIM camera component {ObjectName(aimCam)} @{aimCam:X} (rotation +0x{CamPitch:X}, recoil +0x{RecoilState:X})");
        return aimCam;
    }
    Vector2 AimRecoilOffset(nint cam)
    {
        if(cam==0)return default;var raw=Bytes(cam+RecoilOffset,16);
        return new((float)BitConverter.ToDouble(raw,0),(float)BitConverter.ToDouble(raw,8));
    }

    void AimWeapons(UObj player)
    {
        long now=NowMs;
        if(player.Address!=aimWeaponOwner||now>=aimWeaponsAt)
        {
            aimWeaponOwner=player.Address;aimWeaponsAt=now+3000;
            var list=new List<nint>();
            var inventory=default(UObj);int count=0;
            try
            {
                inventory=player.Obj("CachedInventory");count=inventory.Array("Items").Count;
                for(int i=0,n=Math.Clamp(count,0,4096);i<n;i++)
                {
                    var eq=inventory.Element("Items",i).Ptr("EquippedActor");
                    if(eq!=0&&UE.IsType(eq,"RangedWeapon"))list.Add(eq);
                }
            }
            catch(Exception e){AimLog("weapons",10000,"AIM weapon scan: "+e.Message);}
            if(list.Count!=aimWeapons.Length||list.Count==0)AimLog("weapons-list",list.Count==0?10000:0,$"AIM ranged weapons: {string.Join(", ",list.Select(ObjectName))} (inventory {inventory.Address:X}, items {count})");
            aimWeapons=list.ToArray();
        }
    }
    bool AimFiring(out nint weapon)
    {
        weapon=0;
        foreach(var w in aimWeapons){byte s;try{s=Read<byte>(w+WeaponFireState);}catch{continue;}if(s is >=3 and <=6){weapon=w;return true;}}
        return false;
    }

    // ---------------------------------------------------------------- recoil control

    // The game keeps the recoil still applied to the view in RecoilOffset (pitch, yaw). It grows while
    // kicking (state 1), shrinks toward a residual while recovering (state 2) and is zeroed (residual
    // baked into the view) when the episode ends (state 0). Counter-rotating by -strength*offset makes
    // the view follow base + (1-strength)*recoil exactly, including the game's own recovery.
    Vector2 RcsUpdate(nint cam,float dt,long now)
    {
        var c=AimCfg;
        byte state=Read<byte>(cam+RecoilState);var off=AimRecoilOffset(cam);
        bool firing=AimFiring(out var weapon)||(c.RcsUseLmb&&KeyDown(0x01));
        if(weapon!=0)aimActiveWeapon=weapon;
        if(firing)rcsLastFire=now;
        Vector2 step=default;
        if(rcsActive&&state==1&&rcsPrevState==2)rcsKicks++;
        if(!rcsActive&&c.RcsEnabled&&state!=0&&now-rcsLastFire<300)
        {
            rcsActive=true;rcsStart=rcsPrevState==0?Vector2.Zero:off;rcsApplied=Vector2.Zero;rcsEpisodeStart=now;rcsKicks=1;
            Log($"RCS episode start: state={state} offset=({off.X:F2},{off.Y:F2}) weapon={(weapon==0?"(LMB)":ObjectName(weapon))} strength p={c.RcsPitch:F2} y={c.RcsYaw:F2} smooth={c.RcsSmooth:F1}");
        }
        if(rcsActive)
        {
            if(state==0||!c.RcsEnabled)
            {
                Log($"RCS episode end ({(state==0?"recovered":"disabled")}): {now-rcsEpisodeStart} ms, kicks~{rcsKicks}, last offset=({rcsPrevOffset.X:F2},{rcsPrevOffset.Y:F2})°, compensated=({rcsApplied.X:F2},{rcsApplied.Y:F2})°");
                rcsActive=false;
            }
            else
            {
                var rel=off-rcsStart;
                var target=new Vector2(-c.RcsPitch*rel.X,-c.RcsYaw*rel.Y);
                float alpha=c.RcsSmooth<=1?1:1-MathF.Exp(-dt*60/c.RcsSmooth);
                step=(target-rcsApplied)*alpha;
                if(step.Length()>10)step=Vector2.Normalize(step)*10;
                rcsApplied+=step;
                AimLog("rcs",1000,$"RCS state={state} offset=({off.X:F2},{off.Y:F2}) target=({target.X:F2},{target.Y:F2}) applied=({rcsApplied.X:F2},{rcsApplied.Y:F2}) step=({step.X:F3},{step.Y:F3}) firing={firing}");
            }
        }
        rcsPrevState=state;rcsPrevOffset=off;
        return step;
    }

    // ---------------------------------------------------------------- target selection

    float AimFovFor(float dist)
    {
        var c=AimCfg;if(!c.DynamicFov||c.DynFovFar<=c.DynFovNear)return c.FovDeg;
        float t=Math.Clamp((dist-c.DynFovNear)/(c.DynFovFar-c.DynFovNear),0,1);
        return c.FovDeg*(1+(c.DynFovFarScale-1)*t);
    }
    float AimFalloff(float dist)
    {
        var w=aimActiveWeapon!=0?aimActiveWeapon:aimWeapons.FirstOrDefault();
        if(w!=aimFalloffWeapon)
        {
            aimFalloffWeapon=w;aimFalloff=(0,0);
            try{var mode=UObj.Of(w).Ref("DefaultWeaponMode");if(!mode.IsNull)aimFalloff=(mode.Get<float>("PrimaryFalloff"),mode.Get<float>("SecondaryFalloff"));}
            catch{}
            if(w!=0)Log($"AIM falloff for {ObjectName(w)}: primary {aimFalloff.Primary/100:F1} m, secondary {aimFalloff.Secondary/100:F1} m");
        }
        var (p,s)=aimFalloff;
        if(p<=0||s<=p)return 1;
        return dist<=p?1:dist>=s?0.5f:1-0.5f*(dist-p)/(s-p);
    }
    string LosKey(AimEnemy e,AimPoint p)=>$"{e.Actor:X}:{p.Key}";
    // null: unknown yet.
    bool? AimVisible(AimEnemy e,AimPoint p)
    {
        if(!AimCfg.VisibleOnly||aimLosInit<0)return true;
        // A late trace must not drop a visible target: reuse the last answer while a new one is pending.
        if(!aimLos.TryGetValue(LosKey(e,p),out var r)||NowMs-r.T>Math.Max(400,AimCfg.LosStaleMs))return null;
        return r.Visible;
    }
    bool ArmorOccluded(AimEnemy e,AimPoint p,Vector3 camera)
    {
        if(!AimCfg.SkipArmorOccluded||p.Shape==null||p.Region!="weak")return false;
        var s=p.Shape;var eye=camera-e.Shift; // shapes may be cached at an older actor position
        float weakT=s.Hit(eye,s.Center)??1;
        return e.Shapes.Any(b=>b.Active&&b.Blocks&&b.Exact&&b.Location.Id!=s.Location.Id&&!b.Location.IsWeak(e.WeakT,e.ResistT)
            &&b.Hit(eye,s.Center) is float t&&t<weakT-1e-4f);
    }
    IEnumerable<string[]> RegionTiers()
    {
        switch(AimCfg.Region)
        {
            case 0:yield return ["weak"];yield return ["gate"];yield return ["head"];yield return ["body"];break;
            case 1:yield return ["weak"];yield return ["gate"];yield return ["body"];break;
            case 2:yield return ["head"];yield return ["body"];break;
            case 3:yield return ["body"];break;
            case 4:yield return ["weak"];break;
            default:yield return ["weak","gate","head","body","part","armor"];break;
        }
    }

    // One best point per enemy (by region preference), scored by the target mode. Points still
    // waiting for a line-of-sight answer are returned with Score=NaN so their trace gets requested.
    List<AimChoice> AimCandidates(Vector3 camera,Vector3 forward,float camFov)
    {
        var c=AimCfg;var result=new List<AimChoice>();
        foreach(var e in aimEnemies)
        {
            float dist=Vector3.Distance(e.Root,camera);
            if(dist>c.MaxDistance||e.Health<=0)continue;
            float fovE=AimFovFor(dist);
            float fovLimit=e.Actor==aimTarget?(c.LockUntilRelease?90:c.Sticky?fovE*c.StickyFovMul:fovE):fovE;
            AimChoice best=null;
            if(c.TargetMode==2)
            {
                // Most damage: every intact part competes on expected damage.
                foreach(var p in e.Points)
                {
                    float ang=AngleTo(forward,camera,p.Pos);if(ang>fovLimit)continue;
                    if(ArmorOccluded(e,p,camera))continue;
                    var vis=AimVisible(e,p);
                    float d=Vector3.Distance(camera,p.Pos);
                    float r=MathF.Atan(p.Radius/Math.Max(d,1))*180/MathF.PI;
                    float hit=r*r/(r*r+c.SpreadDeg*c.SpreadDeg);
                    float exp=p.Scalar*AimFalloff(d)*hit*(1+c.LowHealthBonus*(1-Math.Clamp(e.Health,0,1)));
                    float score=exp/(1+c.AnglePenalty*ang/Math.Max(fovE,0.1f));
                    if(vis==false)continue;
                    var ch=new AimChoice(e,p,ang,dist,vis==null?float.NaN:-score,exp,fovE);
                    if(best==null||Better(ch,best))best=ch;
                }
            }
            else
            {
                foreach(var tier in RegionTiers())
                {
                    var pts=e.Points.Where(p=>tier.Contains(p.Region)).ToList();
                    if(pts.Count==0)continue;
                    AimChoice tierBest=null;
                    foreach(var p in pts)
                    {
                        float ang=AngleTo(forward,camera,p.Pos);if(ang>fovLimit)continue;
                        if(ArmorOccluded(e,p,camera))continue;
                        var vis=AimVisible(e,p);if(vis==false)continue;
                        float key=c.Region==5?-p.Scalar:ang;
                        // Prefer keeping the current part while it stays valid (no part flicking).
                        if(e.Actor==aimTarget&&p.Key==aimPointKey)key-=1000;
                        var ch=new AimChoice(e,p,ang,dist,vis==null?float.NaN:key,p.Scalar,fovE);
                        if(tierBest==null||Better(ch,tierBest))tierBest=ch;
                    }
                    if(tierBest!=null){best=tierBest;if(!float.IsNaN(tierBest.Score))break;}
                }
                if(best!=null&&!float.IsNaN(best.Score))
                {
                    float s=c.TargetMode switch
                    {
                        1=>dist,
                        3=>(best.Point.Region is "weak"?0:best.Point.Region is "gate"?1000:2000)+best.Angle,
                        4=>e.Health*1000+best.Angle,
                        _=>best.Angle
                    };
                    best=best with{Score=s};
                }
            }
            if(best!=null)result.Add(best);
        }
        return result;
    }
    static bool Better(AimChoice a,AimChoice b)
    {
        if(float.IsNaN(b.Score))return !float.IsNaN(a.Score)||a.Angle<b.Angle;
        return !float.IsNaN(a.Score)&&a.Score<b.Score;
    }

    void AimLock(List<AimChoice> choices,Vector3 camera,Vector3 forward,long now)
    {
        var c=AimCfg;
        var valid=choices.Where(x=>!float.IsNaN(x.Score)).OrderBy(x=>x.Score).ToList();
        if(aimTarget!=0)
        {
            var cur=valid.FirstOrDefault(x=>x.Enemy.Actor==aimTarget);
            if(cur==null)
            {
                // Out of FOV / not visible / dead: stop aiming at it now, remember it for a quick recall,
                // and fall through so another valid target inside the FOV is picked up this same frame.
                bool gone=!aimEnemies.Any(e=>e.Actor==aimTarget&&e.Health>0);
                bool pending=choices.Any(x=>x.Enemy.Actor==aimTarget);
                var why=gone?"dead/despawned":pending?"visibility pending":"out of FOV / not visible";
                if(!gone){aimLostTarget=aimTarget;aimLostAt=now;aimLostName=aimTargetName;}
                else aimLostTarget=0;
                if(!gone&&!pending)
                {
                    var e=aimEnemies.FirstOrDefault(x=>x.Actor==aimTarget);
                    if(e!=null)Log($"AIM loss detail {aimTargetName}: "+string.Join("; ",e.Points.Select(p=>
                    {
                        var k=LosKey(e,p);string vis=aimLos.TryGetValue(k,out var r)?(r.Visible?"visible":"blocked"):"untraced";
                        string by=aimLosBlocker.TryGetValue(k,out var f)?" by "+f():"";
                        float ang=AngleTo(forward,camera,p.Pos);
                        return $"{p.Region}:{p.Label} {ang:F1}° {vis}{by}";
                    })));
                }
                AimDrop(why,false);
            }
            else
            {
                if(cur.Point.Key!=aimPointKey)
                {
                    AimLog("part",500,$"AIM part {aimTargetName}: {aimPointKey} -> {cur.Point.Region}:{cur.Point.Key} (x{cur.Point.Scalar:F2})");
                    aimLabel=cur.Point.Label;aimPointKey=cur.Point.Key;aimSpread=RandomSpread();aimPlanDone=false;aimPlanE0=0;
                }
                if(!c.Sticky&&!c.LockUntilRelease&&valid.Count>0&&valid[0].Enemy.Actor!=aimTarget)
                {
                    var b=valid[0];
                    bool much=c.TargetMode==2?b.Score<cur.Score*1.15f:b.Score<cur.Score-1;
                    if(much){Log($"AIM switch {aimTargetName} -> {b.Enemy.Name} (non-sticky; score {cur.Score:F3} -> {b.Score:F3})");AimAcquire(b,valid,now,false);}
                }
                return;
            }
        }
        if(valid.Count==0)
        {
            if(choices.Count>0)AimLog("pending",1000,$"AIM {choices.Count} candidate(s) waiting for line of sight");
            return;
        }
        // The target we just lost is back inside the FOV and visible: re-aim at once (no delays).
        bool recall=aimLostTarget!=0&&now-aimLostAt<=c.RecallMs;
        var back=recall?valid.FirstOrDefault(x=>x.Enemy.Actor==aimLostTarget):null;
        if(back!=null)
        {
            Log($"AIM re-acquire {aimLostName} after {now-aimLostAt} ms");
            aimLostTarget=0;AimAcquire(back,valid,now,false);return;
        }
        // A different target: humanization delays apply only here.
        if(now<aimSwitchUntil)return;
        // While the key stays held after a loss, keep the camera where the player left it: during the
        // recall window only the lost target may come back, unless the crosshair is already on another one.
        if(recall)
        {
            var under=valid.FirstOrDefault(x=>x.Angle<=Math.Min(2f,c.FovDeg*0.25f));
            if(under==null)return;
            Log($"AIM switch to {under.Enemy.Name} under the crosshair ({under.Angle:F1}°) after losing {aimLostName}");
            aimLostTarget=0;AimAcquire(under,valid,now,false);return;
        }
        aimLostTarget=0;
        AimAcquire(valid[0],valid,now,true);
    }
    Vector3 RandomSpread()
    {
        float f=AimCfg.PointSpread;if(f<=0)return Vector3.Zero;
        var v=new Vector3((float)aimRng.NextDouble()*2-1,(float)aimRng.NextDouble()*2-1,(float)aimRng.NextDouble()*2-1);
        if(v.Length()>1)v=Vector3.Normalize(v);return v*f;
    }
    void AimAcquire(AimChoice b,List<AimChoice> ranked,long now,bool react)
    {
        var c=AimCfg;
        aimTarget=b.Enemy.Actor;aimTargetName=b.Enemy.Name;aimLabel=b.Point.Label;aimPointKey=b.Point.Key;aimLockStart=now;
        aimReactUntil=react?now+(long)(c.ReactionMinMs+aimRng.NextDouble()*Math.Max(0,c.ReactionMaxMs-c.ReactionMinMs)):now;
        aimSpread=RandomSpread();aimCurveSign=aimRng.Next(2)==0?-1:1;
        aimNoisePhase1=(float)(aimRng.NextDouble()*6.28);aimNoisePhase2=(float)(aimRng.NextDouble()*6.28);
        aimNoiseF1=0.7f+(float)aimRng.NextDouble()*0.6f;aimNoiseF2=1.3f+(float)aimRng.NextDouble()*0.8f;
        aimPlanDone=false;aimPlanE0=0;
        var runner=ranked.FirstOrDefault(x=>x.Enemy.Actor!=b.Enemy.Actor);
        Log($"AIM acquire {b.Enemy.Name} [{AimNames.TargetModes[c.TargetMode]}] {b.Point.Region}:{b.Point.Label} x{b.Point.Scalar:F2} "+
            $"angle={b.Angle:F2}° (fov {b.Fov:F1}) dist={b.Dist/100:F1}m hp={b.Enemy.Health:F2} ({b.Enemy.HealthRaw:F0}) score={b.Score:F3} exp={b.Expected:F3} "+
            $"react={aimReactUntil-now}ms candidates={ranked.Count}"+(runner!=null?$" runner-up {runner.Enemy.Name} {runner.Point.Region}:{runner.Point.Label} score={runner.Score:F3}":""));
    }
    void AimDrop(string why,bool switchDelay)
    {
        if(aimTarget==0)return;
        Log($"AIM lost {aimTargetName} ({why}) after {NowMs-aimLockStart} ms");
        aimTarget=0;aimTargetName=aimLabel=aimPointKey="";
        aimSwitchUntil=switchDelay?NowMs+(long)AimCfg.SwitchDelayMs:0;
    }

    // ---------------------------------------------------------------- smoothing

    // Cubic Bezier timing curve (CSS cubic-bezier semantics): y for progress x.
    static float Bezier(float x,float x1,float y1,float x2,float y2)
    {
        x=Math.Clamp(x,0,1);float t=x;
        for(int i=0;i<8;i++)
        {
            float bx=3*(1-t)*(1-t)*t*x1+3*(1-t)*t*t*x2+t*t*t-x;
            float d=3*(1-t)*(1-t)*x1+6*(1-t)*t*(x2-x1)+3*t*t*(1-x2);
            if(MathF.Abs(d)<1e-5f)break;t=Math.Clamp(t-bx/d,0,1);
        }
        return 3*(1-t)*(1-t)*t*y1+3*(1-t)*t*t*y2+t*t*t;
    }
    float Ease(float u)
    {
        var c=AimCfg;u=Math.Clamp(u,0,1);
        return c.Curve switch
        {
            2=>u*u*(3-2*u),
            3=>Bezier(u,c.BezierX1,c.BezierY1,c.BezierX2,c.BezierY2),
            _=>Bezier(u,0.3f,0.05f,0.2f,1f), // humanized: slow start, fast middle, soft landing
        };
    }

    Vector2 AimSmooth(Vector2 err,AimChoice cur,float dt,long now)
    {
        var c=AimCfg;float mag=err.Length();
        if(mag<c.DeadzoneDeg)return default;
        // Dynamic smoothing multiplier.
        float mul=1;
        if(c.DynamicByAngle)mul*=c.DynNearMul+(c.DynFarMul-c.DynNearMul)*Math.Clamp(mag/Math.Max(cur.Fov,0.1f),0,1);
        if(c.DynamicByDistance)mul*=c.DistNearMul+(c.DistFarMul-c.DistNearMul)*Math.Clamp((cur.Dist-500)/Math.Max(c.MaxDistance-500,1),0,1);
        if(c.DynamicByTime&&c.RampTimeMs>0)mul*=c.RampStartMul+(1-c.RampStartMul)*Math.Clamp((now-aimReactUntil)/c.RampTimeMs,0,1);
        float S=Math.Max(1,c.Smooth*mul);
        float sp=Math.Max(1,S*c.SmoothPitchMul),sy=Math.Max(1,S*c.SmoothYawMul);
        Vector2 step;
        switch(c.Curve)
        {
            case 0: // linear: constant angular speed
            {
                float vp=c.LinearSpeed/sp*dt,vy=c.LinearSpeed/sy*dt;
                var dir=err/mag;
                step=new(MathF.Sign(err.X)*Math.Min(MathF.Abs(err.X),vp*MathF.Abs(dir.X)),MathF.Sign(err.Y)*Math.Min(MathF.Abs(err.Y),vy*MathF.Abs(dir.Y)));
                break;
            }
            case 1: // exponential: classic "error / smooth" per 60 Hz frame, frame-rate independent
                step=new(err.X*(1-MathF.Pow(1-1/sp,dt*60)),err.Y*(1-MathF.Pow(1-1/sy,dt*60)));
                break;
            default: // planned approach along a timing curve, then exponential tracking
            {
                double t=Now;
                if(aimPlanE0<=0||mag>aimPlanE0*1.3f+0.5f){aimPlanStart=t;aimPlanE0=mag;aimPlanDone=false;}
                float T=Math.Max(0.02f,S*c.CurveTimePerSmooth*Math.Clamp(MathF.Sqrt(aimPlanE0/5),0.5f,2f));
                float Frac(float axisMul)
                {
                    float Ta=T*axisMul;float u1=(float)(t-aimPlanStart)/Ta,u0=u1-dt/Ta;
                    if(u1>=1)return -1;
                    float r0=1-Ease(Math.Max(u0,0)),r1=1-Ease(u1);
                    return r0>1e-4f?Math.Clamp(1-r1/r0,0,1):1;
                }
                float fp=Frac(c.SmoothPitchMul),fy=Frac(c.SmoothYawMul);
                if(fp<0&&fy<0)aimPlanDone=true;
                float track(float s)=>1-MathF.Pow(1-1/Math.Max(1,s*0.5f),dt*60);
                step=new(err.X*(fp<0?track(sp):fp),err.Y*(fy<0?track(sy):fy));
                if(c.Curve==4&&!aimPlanDone)
                {
                    // Humanized: bend the path (bend shrinks as the error closes) and vary speed.
                    float th=aimCurveSign*c.Curvature*Math.Clamp(mag/Math.Max(aimPlanE0,0.01f),0,1);
                    float cs=MathF.Cos(th),sn=MathF.Sin(th);
                    step=new(step.X*cs-step.Y*sn,step.X*sn+step.Y*cs);
                    float n=MathF.Sin((float)t*7.3f+aimNoisePhase1)*0.6f+MathF.Sin((float)t*13.1f+aimNoisePhase2)*0.4f;
                    step*=1+c.SpeedNoise*n;
                    if(step.Length()>mag)step=Vector2.Normalize(step)*mag;
                }
                break;
            }
        }
        float max=c.MaxSpeed*dt;
        if(step.Length()>max)step=Vector2.Normalize(step)*max;
        return step;
    }

    // The player moved the camera this frame (beyond our write and the recoil) away from the target.
    // Player input speed is measured over a sliding window of real time, not per aim step: the game
    // applies mouse input once per game frame, so at a 1000 Hz aim rate one frame's movement lands in a
    // single 1 ms step and would read 10-15x too fast (small corrections falsely paused the aim).
    const double OverrideWindowS=0.030;
    readonly Queue<(double T,Vector2 D)> overrideSamples=new();
    void AimOverrideCheck(Vector2 camRot,Vector2 err,float dt,long now)
    {
        var c=AimCfg;double t=aimClock.Elapsed.TotalSeconds;
        var recoilDelta=AimRecoilOffset(aimCam)-aimLastRecoil;
        var user=new Vector2((float)(camRot.X-aimLastWrittenP),NormDeg(camRot.Y-aimLastWrittenY))-recoilDelta;
        overrideSamples.Enqueue((t,user));
        while(overrideSamples.Count>0&&t-overrideSamples.Peek().T>OverrideWindowS)overrideSamples.Dequeue();
        var sum=Vector2.Zero;foreach(var (_,d) in overrideSamples)sum+=d;
        float rate=(float)(sum.Length()/OverrideWindowS);
        if(rate>c.OverrideDegPerSec&&Vector2.Dot(sum,aimLastError)<0)
        {
            if(now>=aimPauseUntil)Log($"AIM override: player input {rate:F0}°/s over {OverrideWindowS*1000:F0} ms away from {aimTargetName} (Δ=({sum.X:F2},{sum.Y:F2})°); paused {c.OverridePauseMs:F0} ms");
            aimPauseUntil=now+(long)c.OverridePauseMs;aimPlanE0=0;
        }
    }

    // ---------------------------------------------------------------- line of sight (game trace)

    bool AimLosSetup()
    {
        if(aimLosInit!=0)return aimLosInit>0;
        aimLosInit=-1;
        try
        {
            var t=Stopwatch.StartNew();
            var cls=UE.FindObjects("Class",p=>p=="/Script/Engine.KismetSystemLibrary",1).Values.FirstOrDefault();
            if(cls==0){Log("AIM LOS unavailable: KismetSystemLibrary not found");return false;}
            aimKslCdo=Ptr(cls+UE.ClassDefaultObject);aimTraceFn=UFn.Get(cls,"LineTraceSingle");
            if(aimTraceFn.Params["OutHit"].Size<0xE8){Log($"AIM LOS unavailable: unexpected FHitResult size {aimTraceFn.Params["OutHit"].Size}");return false;}
            // ETraceTypeQuery -> channel: Visibility, Camera, then custom trace channels in profile order.
            var profile=Ptr(Ptr(Mem.Base+UE.CollisionProfileClassRva)+UE.ClassDefaultObject);
            var a=UObj.Of(profile).At("DefaultChannelResponses");int n=Read<int>(a+8);var data=Ptr(a);
            var traceChannels=new List<(int Channel,string Name)>();
            for(int i=0;i<n&&i<32;i++)if(Read<byte>(data+i*12+2)!=0)traceChannels.Add((Read<byte>(data+i*12),Name(Read<ulong>(data+i*12+4))));
            int weapon=traceChannels.FindIndex(x=>x.Name=="Weapon");
            aimTraceQuery=(byte)(AimCfg.LosChannel==1||weapon<0?0:2+weapon);
            aimLosInit=1;
            Log($"AIM LOS ready ({t.ElapsedMilliseconds} ms): KSL CDO {aimKslCdo:X}, {aimTraceFn.Describe()}; trace query {aimTraceQuery} ({(aimTraceQuery==0?"Visibility":"Weapon")}); custom trace channels: {string.Join(", ",traceChannels.Select((x,i)=>$"{x.Name}=q{2+i}"))}");
            return true;
        }
        catch(Exception e){Log("AIM LOS setup failed: "+e.Message);return false;}
    }

    void AimRequestLos(Vector3 camera,List<AimChoice> choices)
    {
        var c=AimCfg;
        if(!c.VisibleOnly)return;
        if(GameThread==null){if(aimLosInit==0){aimLosInit=-1;Log("AIM LOS unavailable: no game-thread executor; treating targets as visible");}return;}
        if(!AimLosSetup())return;
        long now=NowMs;
        if(markerManager!=0)GameThread.Ensure(markerManager);
        GameThread.Poll();
        if(!GameThread.Ready)
        {
            // No executor in this world (no SilhouetteManager to hook): fall back to "visible".
            if(aimLosNotReadySince==0)aimLosNotReadySince=now;
            else if(now-aimLosNotReadySince>2000){aimLosInit=-1;Log("AIM LOS unavailable: game-thread executor not ready; treating targets as visible");}
            return;
        }
        aimLosNotReadySince=0;
        // One trace batch at a time. Queued behind other game-thread work (e.g. charge polling every few ms)
        // rather than requiring an idle executor: with "only when idle" the traces starved while charging,
        // the last answers went stale and the lock dropped as "visibility pending".
        if(aimLosInFlight&&now-aimLosSubmitted<2000)return;
        if(now-aimLosSubmitted<15)return;
        // Every candidate point in range of the FOV (the selection needs all of them), nearest first.
        var want=new List<(string Key,nint Actor,Vector3 P,string Name)>();
        foreach(var e in aimEnemies)
        {
            float dist=Vector3.Distance(e.Root,camera);if(dist>c.MaxDistance)continue;
            foreach(var p in e.Points)
            {
                var key=LosKey(e,p);
                bool fresh=aimLos.TryGetValue(key,out var r)&&now-r.T<120;
                if(!fresh)want.Add((key,e.Actor,p.Pos,e.Name+":"+p.Label));
            }
        }
        if(want.Count==0)return;
        var pri=new HashSet<string>(choices.Select(x=>LosKey(x.Enemy,x.Point)));
        var batch=want.OrderBy(w=>w.Actor==aimTarget?0:pri.Contains(w.Key)?1:2).ThenBy(w=>Vector3.Distance(w.P,camera)).Take(24).ToList();
        var calls=new List<GameThread.Call>();
        var player=aimWeaponOwner;
        // Start each trace at the player's depth along the camera ray (+50 cm): anything between the
        // third-person camera and the character (e.g. vegetation the game fades out so you can see through
        // it) cannot block the shot and must not count as an occluder.
        var playerPos=player!=0?Read<FTransform>(UObj.Of(player).Ptr("Mesh")+0x240).ToMatrixWithScale().Translation:camera;
        var starts=new Vector3[batch.Count];
        for(int i=0;i<batch.Count;i++)
        {
            var dir=batch[i].P-camera;float len=dir.Length();
            if(len<1){starts[i]=camera;continue;}
            dir/=len;starts[i]=camera+dir*Math.Clamp(Vector3.Dot(playerPos-camera,dir)+50,0,Math.Max(0,len-10));
        }
        int callIndex=0;
        foreach(var w in batch)
        {
            var args=aimTraceFn.New().Ptr("WorldContextObject",player).Byte("TraceChannel",aimTraceQuery).Bool("bTraceComplex",true) /* per-triangle like weapon traces: shoot through gaps in foliage/rocks */.Byte("DrawDebugType",0).Bool("bIgnoreSelf",true);
            void Vec(string name,Vector3 v){int o=aimTraceFn.Params[name].Offset;BitConverter.GetBytes((double)v.X).CopyTo(args.Data,o);BitConverter.GetBytes((double)v.Y).CopyTo(args.Data,o+8);BitConverter.GetBytes((double)v.Z).CopyTo(args.Data,o+16);}
            Vec("Start",starts[callIndex++]);Vec("End",w.P);
            calls.Add(new(aimKslCdo,aimTraceFn,args.Data));
        }
        var fn=aimTraceFn;
        aimLosInFlight=true;aimLosSubmitted=now;
        GameThread.Enqueue(calls,read=>
        {
            aimLosInFlight=false;
            long t=NowMs;int vis=0;
            for(int i=0;i<batch.Count;i++)
            {
                var d=read(i);var w=batch[i];int o=fn.Params["OutHit"].Offset;
                bool hit=d[fn.Params["ReturnValue"].Offset]!=0;
                bool visible=true;Func<string> byLazy=()=>"";
                if(hit)
                {
                    if(!aimLosValidated)
                    {
                        var ts=new Vector3((float)BitConverter.ToDouble(d,o+HitTraceStart),(float)BitConverter.ToDouble(d,o+HitTraceStart+8),(float)BitConverter.ToDouble(d,o+HitTraceStart+16));
                        if(Vector3.Distance(ts,starts[i])>50){Log($"AIM LOS disabled: FHitResult.TraceStart {ts} != start {starts[i]} (layout mismatch)");aimLosInit=-1;return;}
                        aimLosValidated=true;Log("AIM LOS FHitResult layout validated (TraceStart matches)");
                    }
                    float dist=BitConverter.ToSingle(d,o+8);
                    // Hit the target itself? Compare weak-reference keys first (no reads); resolve only if needed.
                    ulong actorKey=BitConverter.ToUInt64(d,o+HitActor),compKey=BitConverter.ToUInt64(d,o+HitComponent);
                    if(!aimWeakKeys.TryGetValue(w.Actor,out var ownKey))aimWeakKeys[w.Actor]=ownKey=UE.WeakKey(w.Actor);
                    bool own=ownKey!=0&&actorKey==ownKey;
                    nint actor=0,comp=0;
                    if(!own)
                    {
                        actor=UE.ResolveWeakKey(actorKey);comp=UE.ResolveWeakKey(compKey);
                        own=actor==w.Actor||comp!=0&&Ptr(comp+UE.ObjOuter)==w.Actor;
                    }
                    float total=Vector3.Distance(starts[i],w.P);
                    visible=own||dist>=total-LosReachCm;
                    if(!visible){var a0=actor;var c0=comp;var d0=dist;var t0=total;byLazy=()=>$"{(a0!=0?ObjectName(a0):"?")}/{(c0!=0?ObjectName(c0):"?")} at {d0/100:F1}m of {t0/100:F1}m";}
                }
                if(visible)vis++;
                bool changed=!aimLos.TryGetValue(w.Key,out var old)||old.Visible!=visible;
                aimLos[w.Key]=(visible,t);
                if(visible)aimLosBlocker.Remove(w.Key);else aimLosBlocker[w.Key]=byLazy;
                if(changed&&aimLosLogged.Add(w.Key))Log($"AIM LOS {w.Name}: {(visible?"visible":"blocked by "+byLazy())}");
            }
            AimLog("los-batch",10000,$"AIM LOS batch {batch.Count} traces, {vis} visible (query {aimTraceQuery})");
        });
        if(aimLos.Count>2048)foreach(var k in aimLos.Where(p=>now-p.Value.T>5000).Select(p=>p.Key).ToList())aimLos.Remove(k);
    }
}
