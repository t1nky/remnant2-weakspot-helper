using System.Numerics;
using System.Runtime.InteropServices;

// Scene scan, hit locations, collision shapes, weakspot drawing, in-game outlines and silhouettes.
// Collision shapes are read from the running game the first time an enemy type is seen and cached.
internal sealed partial class GameOverlay
{
    public WeakspotHook Hook{get;set;}
    readonly Dictionary<string,Dictionary<string,int>> bones=new();
    readonly Dictionary<(string Asset,string Body),BodyGeometry> bodies=new();
    readonly Dictionary<(string Asset,string Body),(ShapeGeometry[] Shapes,bool Exact)> staticBodies=new();
    readonly HashSet<string> reported=new();
    readonly StreamWriter log;
    bool esp=true,force=false,menu=true,showArmor=false;
    bool hiLite=false;
    bool weakOnly=false;
    // Custom-depth stencil read by the game's silhouette post-process.
    int outlineStencil=254;
    readonly string[] outlineStyles=["Orange outline","White outline","Purple fill"];
    readonly Dictionary<nint,long> silNext=new();
    readonly HashSet<nint> silLogged=new();
    // Primitive -> original native (bRenderCustomDepth, CustomDepthStencilValue) for restore.
    readonly Dictionary<nint,(bool On,int Stencil)> cdEnabled=new();
    int frame;
    volatile int enemies; // enemies with weakspots, published once per scan (read by the menu)
    volatile bool inLevel;long inLevelUntil; // a local player pawn was seen recently (status line)
    ulong lastCount;
    long nextTelemetry;
    int attackType;
    readonly string[] attackLabels=["Ranged","Melee","Thrown projectile"];
    readonly string[] attackChannels=["Weapon","WeaponPhantom","ThrownProjectile"];
    readonly Dictionary<string,int> channels=new();
    long nextChannelRead;
    // Scene scan: tens of thousands of actors' class pointers, so it runs on a background task and the
    // last result stays in use.
    volatile nint[] characterCandidates=[];
    int sceneSignature;
    long nextSceneScan;
    Task sceneScan;
    readonly Dictionary<nint,bool> sceneClassCache=new(); // touched only by the (single) scan in flight
    volatile bool sceneClassReset;                          // set on world change; the next scan clears the cache
    nint[] Characters(UObj world)
    {
        var arrays=new List<(nint Data,int Count)>();var hash=new HashCode();
        var (levels,levelCount)=world.Array("Levels");
        if(levelCount<0||levelCount>4096||levelCount>0&&levels==0)return characterCandidates;
        var levelPointers=Bytes(levels,levelCount*8);
        for(int l=0;l<levelCount;l++)
        {
            var level=(nint)BitConverter.ToInt64(levelPointers,l*8);if(level==0)continue;
            var a=level+UE.LevelActors;
            var data=Ptr(a);int n=Read<int>(a+8);
            if(n<0||n>200000||n>0&&data==0)continue;
            hash.Add(level);hash.Add(data);hash.Add(n);arrays.Add((data,n));
        }
        int signature=hash.ToHashCode();
        bool due=signature!=sceneSignature||Environment.TickCount64>=nextSceneScan;
        if(due&&(sceneScan==null||sceneScan.IsCompleted))
        {
            sceneSignature=signature;nextSceneScan=Environment.TickCount64+1000;
            var snapshot=arrays.ToArray();
            if(characterCandidates.Length==0)ScanScene(snapshot,signature); // nothing to show yet: scan inline once
            else sceneScan=Task.Run(()=>ScanScene(snapshot,signature));
        }
        return characterCandidates;
    }
    void ScanScene((nint Data,int Count)[] arrays,int signature)
    {
        try
        {
            var candidates=new HashSet<nint>();
            if(sceneClassReset||sceneClassCache.Count>20000){sceneClassReset=false;sceneClassCache.Clear();}
            foreach(var (data,count) in arrays)
            {
                var raw=count==0?[]:Bytes(data,count*8);
                for(int i=0;i<count;i++)
                {
                    var p=(nint)BitConverter.ToInt64(raw,i*8);if(p==0)continue;
                    var cls=Mem.Peek<nint>(p+UE.ObjClass);if(cls==0)continue;
                    if(!sceneClassCache.TryGetValue(cls,out bool isCharacter))sceneClassCache[cls]=isCharacter=UE.ClassIs(cls,"CharacterGunfire");
                    if(isCharacter)candidates.Add(p);
                }
            }
            characterCandidates=candidates.ToArray();
        }
        catch(Exception e){Once("scene-error:"+e.Message,"SCENE scan failed: "+e.Message);}
    }

    public GameOverlay()
    {
        var dir=Path.Combine(AppContext.BaseDirectory,"logs");Directory.CreateDirectory(dir);
        // Keep the 20 most recent session logs.
        foreach(var old in Directory.GetFiles(dir,"weakspots_*.log").OrderByDescending(f=>f).Skip(19))try{File.Delete(old);}catch{}
        log=new StreamWriter(Path.Combine(dir,$"weakspots_{DateTime.Now:yyyyMMdd_HHmmss}.log")){AutoFlush=true};
        // Loaded here, before the worker and aim threads start using it.
        aimCfg=AimConfig.Load(Path.Combine(AppContext.BaseDirectory,"aim_config.json"),Log);
        ApplyUiPreferences();
    }

    public void Log(string text)
    {
        var line=$"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        lock(log)try{log.WriteLine(line);}catch(IOException){}
    }
    void Once(string key,string message){bool first;lock(reported)first=reported.Add(key);if(first)Log(message);}
    [DllImport("user32.dll")]static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint window,out uint process);
    static T Read<T>(nint p) where T:unmanaged=>Mem.Read<T>(p);
    static byte[] Bytes(nint p,int n)=>Mem.Bytes(p,n);
    static nint Ptr(nint p)=>Mem.Read<nint>(p);
    static string Name(ulong key)=>UE.Name(key);
    static string ObjectName(nint p)=>UE.ObjectName(p);
    static string ObjectPath(nint p)=>UE.ObjectPath(p);
    static bool GameFocused()
    {
        var foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out uint foregroundProcess);
        return foreground==Mem.Window||foregroundProcess==(uint)Environment.ProcessId;
    }

    // GEngine->DefaultPhysMaterial: what a body without its own physical material uses.
    static nint DefaultMaterial()
    {
        var engine=Ptr(Mem.Base+UE.EngineRva);
        return engine==0?0:Ptr(engine+0x830);
    }
    void ReadChannels()
    {
        if(channels.Count>0||Environment.TickCount64<nextChannelRead)return;
        nextChannelRead=Environment.TickCount64+5000;
        // UCollisionProfile::StaticClass singleton and its class default object.
        var cls=Ptr(Mem.Base+UE.CollisionProfileClassRva);
        if(cls==0)return;var cdo=Ptr(cls+UE.ClassDefaultObject);
        if(cdo==0||ObjectName(cdo)!="Default__CollisionProfile")return;
        var a=UObj.Of(cdo).At("DefaultChannelResponses");
        int count=Read<int>(a+8);var data=Ptr(a);
        // FCustomChannelSetup: sizeof=12, Channel byte at 0, FName at 4.
        if(count<=0||count>32||data==0)return;
        for(int i=0;i<count;i++)
        {
            int channel=Read<byte>(data+i*12);var name=Name(Read<ulong>(data+i*12+4));
            if(channel<32)channels[name]=channel;
        }
        Log("Collision channels: "+string.Join(", ",channels.Select(p=>$"{p.Key}={p.Value}")));
    }
    // FBodyInstance collision response to the selected attack's trace channel (2 = block).
    byte Response(nint body)=>channels.TryGetValue(attackChannels[attackType],out int channel)?Read<byte>(body+0x88+channel):(byte)0;
    bool QueryEnabled(nint body,int enabled)=>(enabled is 1 or 3)&&Response(body)>0;
    static ulong[] Names(nint array)
    {
        int n=Read<int>(array+8);var data=Ptr(array);
        if(n<0||n>256||n>0&&data==0)throw new InvalidDataException("Invalid FName array.");
        var raw=n==0?[]:Bytes(data,n*8);
        return Enumerable.Range(0,n).Select(i=>BitConverter.ToUInt64(raw,i*8)).ToArray();
    }
    // One bulk read per enemy per frame; the parse (names, prerequisite arrays) only reruns when the
    // raw entries change (e.g. a location gets destroyed), and unchanged results are reused as-is.
    // FHitLocationInfo (0x80 bytes): Name @0, PhysMaterial @8, InitiallyEnabled @0x10, DamageScalar @0x14,
    // Resist @0x48, Weak @0x49, Destroyed @0x4A, prerequisite FName array @0x58.
    static readonly Dictionary<nint,(byte[] Raw,HitLocation[] Parsed)> hitCache=new();
    static readonly Dictionary<ulong,string> hitNames=new();
    static HitLocation[] Locations(UObj hitlog)
    {
        var array=hitlog.At("HitLocations");var header=Bytes(array,16);
        var data=(nint)BitConverter.ToInt64(header,0);int n=BitConverter.ToInt32(header,8);
        if(n<0||n>256||n>0&&data==0)throw new InvalidDataException("Invalid hit-location array.");
        var raw=n==0?[]:Bytes(data,n*0x80);
        if(hitCache.TryGetValue(array,out var cached)&&cached.Raw.AsSpan().SequenceEqual(raw))return cached.Parsed;
        if(hitCache.Count>512)hitCache.Clear();
        var result=new HitLocation[n];
        for(int i=0;i<n;i++)
        {
            int o=i*0x80;ulong id=BitConverter.ToUInt64(raw,o);
            if(!hitNames.TryGetValue(id,out var name))hitNames[id]=name=Name(id);
            // Prerequisite list: reuse the previous parse when its TArray header did not change.
            ulong[] deps=null;
            if(cached.Parsed!=null&&i<cached.Parsed.Length&&cached.Raw.Length>=o+0x68&&cached.Raw.AsSpan(o+0x58,0x10).SequenceEqual(raw.AsSpan(o+0x58,0x10)))
                deps=cached.Parsed[i].Dependencies;
            deps??=Names(data+o+0x58);
            result[i]=new(id,name,(nint)BitConverter.ToInt64(raw,o+8),BitConverter.ToSingle(raw,o+0x14),raw[o+0x49]!=0,
                raw[o+0x48]!=0,raw[o+0x4a]!=0,raw[o+0x10]!=0,deps);
        }
        hitCache[array]=(raw,result);
        return result;
    }
    // Derived per hit-location array. Locations() returns the same array until the game data changes,
    // so these are built once per change instead of for every enemy on every scan.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HitLocation[],Dictionary<ulong,HitLocation>> hitsById=new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HitLocation[],string> hitsDestroyed=new();
    static Dictionary<ulong,HitLocation> ById(HitLocation[] hits)=>hitsById.GetValue(hits,h=>h.GroupBy(x=>x.Id).ToDictionary(g=>g.Key,g=>g.First()));
    static string DestroyedState(HitLocation[] hits)=>hitsDestroyed.GetValue(hits,h=>string.Concat(h.Select(x=>x.Destroyed?'1':'0')));
    // One bulk read of a mesh's pose per frame (cleared by DrawWorld).
    // USkinnedMeshComponent: LeaderPoseComponent (weak) @0x5B8, leader bone map @0x668, component-space
    // transform buffers @0x600 (two, selected by @0x648); component transform @0x240 on every scene component.
    sealed record PoseSnapshot(int[] Map,byte[] Bones,int Count,Matrix4x4 Component);
    static readonly Dictionary<nint,PoseSnapshot> poseCache=new();
    // Leader-pose links rarely change: cache them briefly. Bone buffers are read once per frame per
    // pose source, so follower meshes share one leader read.
    sealed record PoseLink(nint Pose,int[] Map,long Until);
    static readonly Dictionary<nint,PoseLink> poseLinks=new();
    static readonly Dictionary<nint,(byte[] Bones,int Count)> poseBones=new();
    static PoseSnapshot Pose(nint component)
    {
        if(poseCache.TryGetValue(component,out var cached))return cached;
        long now=Environment.TickCount64;
        if(!poseLinks.TryGetValue(component,out var link)||now>=link.Until)
        {
            nint pose=component;int[] map=null;
            var leader=UE.WeakObject(component+0x5b8);
            if(leader!=0)
            {
                int n=Read<int>(component+0x670);var m=Ptr(component+0x668);
                if(n<=0||n>8192||m==0)pose=0;
                else{var raw=Bytes(m,n*4);map=new int[n];Buffer.BlockCopy(raw,0,map,0,raw.Length);pose=leader;}
            }
            poseLinks[component]=link=new(pose,map,now+2000);
        }
        PoseSnapshot snapshot=null;
        if(link.Pose!=0)
        {
            if(!poseBones.TryGetValue(link.Pose,out var boneData))
            {
                int readIndex=Read<int>(link.Pose+0x648);
                if(readIndex is 0 or 1)
                {
                    var header=Bytes(link.Pose+0x600+readIndex*0x10,16);
                    var ptr=(nint)BitConverter.ToInt64(header,0);int count=BitConverter.ToInt32(header,8);
                    if(count>0&&count<=8192&&ptr!=0)boneData=(Bytes(ptr,count*0x60),count);
                }
                poseBones[link.Pose]=boneData;
            }
            if(boneData.Bones!=null)snapshot=new(link.Map,boneData.Bones,boneData.Count,Read<FTransform>(component+0x240).ToMatrixWithScale());
        }
        return poseCache[component]=snapshot;
    }
    static bool BoneWorld(nint component,int bone,out Matrix4x4 world)
    {
        world=default;if(bone<0)return false;
        var pose=Pose(component);if(pose==null)return false;
        int index=bone;
        if(pose.Map!=null){if(bone>=pose.Map.Length)return false;index=pose.Map[bone];}
        if(index<0||index>=pose.Count)return false;
        var bt=MemoryMarshal.Read<FTransform>(pose.Bones.AsSpan(index*0x60,0x60));
        world=bt.ToMatrixWithScale()*pose.Component;
        return float.IsFinite(world.M41)&&float.IsFinite(world.M42)&&float.IsFinite(world.M43)&&MathF.Abs(world.GetDeterminant())>1e-12;
    }
    // Local-space outline polylines per shape geometry, generated once (they never change).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShapeGeometry,Vector3[][]> localLines=new();
    static Vector3[][] LocalLines(ShapeGeometry g)=>localLines.GetValue(g,x=>x.Lines().ToArray());
    sealed record LiveShape(HitLocation Location,ShapeGeometry Geometry,Matrix4x4 World,bool Active,bool Exact=true,bool Blocks=true)
    {
        // Attachment point for an in-game marker: a shape component, or a skeletal mesh + bone FName.
        public nint Parent{get;init;}
        public ulong Socket{get;init;}
        // Pose source for cheap per-frame re-posing of cached shapes: World = LocalMatrix * (bone or component).
        public nint Anchor{get;init;}
        public int Bone{get;init;}=-1;
        public Vector3 Center=>Vector3.Transform(Geometry.LocalCenter,World);
        // Segment hit test with the inverse world matrix kept until the pose changes.
        readonly InverseCache inverse=new();
        public float? Hit(Vector3 from,Vector3 to)
        {
            var c=inverse;
            if(!c.Valid||c.For!=World){c.For=World;c.Ok=Matrix4x4.Invert(World,out c.Inverse);c.Valid=true;}
            return c.Ok?Geometry.IntersectLocal(Vector3.Transform(from,c.Inverse),Vector3.Transform(to,c.Inverse)):null;
        }
    }
    // Mutable cache inside an immutable record: excluded from the record's value equality and hash
    // (shapes are dictionary keys), so it compares equal to any other cache.
    sealed class InverseCache
    {
        public Matrix4x4 For,Inverse;public bool Valid,Ok;
        public override bool Equals(object o)=>o is InverseCache;
        public override int GetHashCode()=>0;
    }
    IEnumerable<UObj> Components(UObj actor)
    {
        // AActor::OwnedComponents is a TSet: sparse elements (16 bytes) @0x210 + allocation bitmap @0x220/0x230.
        var seen=new HashSet<nint>();
        var primary=actor.Obj("Mesh");
        if(primary.Address!=0){seen.Add(primary.Address);yield return primary;}
        var start=actor.Address;int n=Read<int>(start+0x218),bits=Read<int>(start+0x238);
        var elements=Ptr(start+0x210);
        if(n<0||n>65536||bits!=n||n>0&&elements==0)yield break;
        var allocation=Ptr(start+0x230);if(allocation==0)allocation=start+0x220;
        var flags=n==0?[]:Bytes(allocation,((n+31)/32)*4);
        var entries=n==0?[]:Bytes(elements,n*16);
        if(Environment.TickCount64>=componentKindsPurge){componentKinds.Clear();componentKindsPurge=Environment.TickCount64+20000;}
        for(int i=0;i<n;i++)
        {
            if((flags[i>>3]&(1<<(i&7)))==0)continue;
            var ptr=(nint)BitConverter.ToInt64(entries,i*16);
            if(ptr==0||!seen.Add(ptr))continue;
            // Classified once (cleared every 20 s in case addresses get reused): effect components such as
            // the thousands of Niagara systems some enemies accumulate never carry hit collision.
            if(!componentKinds.TryGetValue(ptr,out var cls))
            {
                bool relevant=UE.IsType(ptr,"PrimitiveComponent")&&!UE.IsType(ptr,"FXSystemComponent");
                componentKinds[ptr]=cls=relevant?Ptr(ptr+UE.ObjClass):0;
            }
            if(cls!=0)yield return new UObj(ptr,cls);
        }
    }
    readonly Dictionary<nint,nint> componentKinds=new(); // component -> class (0 = not a hit-collision primitive)
    long componentKindsPurge;
    HitLocation MatchLocation(HitLocation[] locations,ulong[] tags,nint material,string diagnostic)
    {
        var tagged=locations.Where(h=>tags.Contains(h.Id)).ToArray();
        if(tagged.Length==1)return tagged[0];
        var matching=locations.Where(h=>h.Material!=0&&h.Material==material).ToArray();
        if(matching.Length==1)return matching[0];
        // Material alone cannot distinguish two separately gated logical regions.
        // Do not silently give the right core the left core's prerequisite chain.
        if(matching.Length>1)Once("ambiguous:"+diagnostic,$"AMBIGUOUS {diagnostic}: shared physical material without a unique hit-location tag");
        return null;
    }
    List<LiveShape> PrimitiveShapes(UObj component,HitLocation[] hits)
    {
        if(UE.IsType(component.Address,"SkeletalMeshComponent"))return Shapes(component,hits);
        var result=new List<LiveShape>();var bi=component.Struct("BodyInstance");
        var tags=Names(component.At("ComponentTags"));
        nint material=Ptr(bi.Address+0x140);
        UObj setup=default;
        if(UE.IsType(component.Address,"StaticMeshComponent"))
        {var mesh=component.Obj("StaticMesh");if(!mesh.IsNull)setup=mesh.Obj("BodySetup");}
        else if(UE.IsType(component.Address,"ShapeComponent"))setup=component.Obj("ShapeBodySetup");
        if(material==0&&!setup.IsNull)material=Ptr(setup.Address+0xa8);
        if(material==0)material=DefaultMaterial();
        var location=MatchLocation(hits,tags,material,ObjectName(component.Address));
        if(location==null)return result;
        ShapeGeometry[] shapes=null;bool exact=true;
        if(UE.IsType(component.Address,"SphereComponent"))shapes=[new(){Kind="Sphere",Radius=component.Get<float>("SphereRadius")}];
        else if(UE.IsType(component.Address,"CapsuleComponent"))
        {
            float radius=component.Get<float>("CapsuleRadius"),half=component.Get<float>("CapsuleHalfHeight");
            shapes=[new(){Kind="Sphyl",Radius=radius,Length=2*MathF.Max(0,half-radius)}];
        }
        else if(UE.IsType(component.Address,"BoxComponent"))
        {
            var e=Read<FVector>(component.At("BoxExtent")).ToFloats()*2;
            shapes=[new(){Kind="Box",X=e.X,Y=e.Y,Z=e.Z}];
        }
        else if(!setup.IsNull)
        {
            var key=(ObjectPath(UE.OuterOf(setup.Address)),ObjectName(setup.Address));
            if(!staticBodies.TryGetValue(key,out var cachedBody))
            {
                // Static meshes trace their simple collision exactly only with CTF_UseSimpleAsComplex (2);
                // otherwise the game hits the render triangles and the simple hull is an approximation.
                cachedBody=(LiveGeometry.Shapes(setup.Address),UE.Field(setup.Type,"CollisionTraceFlag") is {} trace&&Mem.Peek<byte>(setup.Address+trace.Offset)==2);
                staticBodies[key]=cachedBody;
            }
            (shapes,exact)=cachedBody;
        }
        if(shapes==null||shapes.Length==0)
        {Once("primitive:"+ObjectName(component.Address),$"UNMAPPED primitive collision {ObjectName(component.Address)} ({UE.ClassName(component.Address)})");return result;}
        var matrix=Read<FTransform>(component.Address+0x240).ToMatrixWithScale();
        bool active=QueryEnabled(bi.Address,bi.Get<byte>("CollisionEnabled"))&&!location.Destroyed;
        foreach(var shape in shapes)
        {
            if(shape.Kind is not ("Sphere" or "Box" or "Sphyl" or "Convex"))
            {Once("shape:"+shape.Kind,$"UNMAPPED shape kind {shape.Kind}");continue;}
            if(shape.HasQuery)result.Add(new(location,shape,shape.LocalMatrix*matrix,active,exact,Response(bi.Address)==2)
                {Parent=UE.IsType(component.Address,"ShapeComponent")||!RendersMesh(component.Address)?component.Address:0,Anchor=component.Address});
        }
        return result;
    }
    // USkeletalMeshComponent: SkeletalMesh asset @0x5A8 (or @0x5B0), live FBodyInstance array @0xB80.
    List<LiveShape> Shapes(UObj mesh,HitLocation[] locations)
    {
        var result=new List<LiveShape>();
        var meshAsset=Ptr(mesh.Address+0x5a8);if(meshAsset==0)meshAsset=Ptr(mesh.Address+0x5b0);
        if(meshAsset==0)return result;
        var meshName=ObjectName(meshAsset);
        if(!bones.TryGetValue(meshName,out var boneMap))
        {
            try{boneMap=LiveGeometry.Bones(meshAsset);bones[meshName]=boneMap;}
            catch(Exception e){Once("mesh:"+meshName,$"UNMAPPED mesh {meshName}: {e.Message}");return result;}
        }
        int count=Read<int>(mesh.Address+0xb88);var array=Ptr(mesh.Address+0xb80);
        if(count<0||count>2048||count>0&&array==0)throw new InvalidDataException("Invalid skeletal physics body array.");
        var tags=Names(mesh.At("ComponentTags"));
        var bodyTemplate=mesh.Struct("BodyInstance");
        int collisionOffset=UE.Field(bodyTemplate.Type,"CollisionEnabled")?.Offset??-1;
        if(collisionOffset<=0||collisionOffset>0x200)throw new InvalidDataException("Unrecognized BodyInstance layout.");
        if(count==0)
        {
            // A non-main mesh with collision off (visual layers such as Visual_Plate_L / Visual_Head) never
            // gets bodies: its physics asset is only shared for posing, so none of it can be hit.
            var owner=UE.OuterOf(mesh.Address);
            bool main=UE.IsType(owner,"Character")&&UObj.Of(owner).Ptr("Mesh")==mesh.Address;
            bool hittable=main||Read<byte>(bodyTemplate.Address+collisionOffset)!=0;
            return DormantShapes(mesh,meshAsset,meshName,boneMap,locations,tags,hittable);
        }
        var bodyPointers=Bytes(array,count*8);
        for(int i=0;i<count;i++)
        {
            var body=(nint)BitConverter.ToInt64(bodyPointers,i*8);if(body==0)continue;
            var setup=UE.WeakObject(body+8);if(setup==0)continue;
            var asset=UE.OuterOf(setup);
            var key=(ObjectName(asset),ObjectName(setup));
            if(!bodies.TryGetValue(key,out var geometry))
            {
                geometry=new(){Name=key.Item2,Bone=Name(Read<ulong>(setup+0x28)),Shapes=LiveGeometry.Shapes(setup)};
                bodies[key]=geometry;
            }
            // Native FBodyInstance::GetSimplePhysicalMaterial precedence.
            var material=Ptr(body+0x140);
            if(material==0)material=Ptr(mesh.Address+0x4d8);
            if(material==0)material=Ptr(setup+0xa8);
            if(material==0)material=DefaultMaterial();
            var location=MatchLocation(locations,tags,material,meshName+":"+geometry.Bone);
            if(location==null)continue;
            if(!boneMap.TryGetValue(geometry.Bone,out int bone)||!BoneWorld(mesh.Address,bone,out var world))
            {Once("bone:"+meshName+":"+geometry.Bone,$"UNMAPPED pose {meshName}:{geometry.Bone}");continue;}
            byte enabled=Read<byte>(body+collisionOffset);
            bool active=QueryEnabled(body,enabled)&&!location.Destroyed;
            ulong boneName=Read<ulong>(setup+0x28);
            foreach(var s in geometry.Shapes)if(s.HasQuery)result.Add(new(location,s,s.LocalMatrix*world,active,true,Response(body)==2){Parent=mesh.Address,Socket=boneName,Anchor=mesh.Address,Bone=bone});
        }
        return result;
    }
    // Dormant enemies have no live FBodyInstances until the game creates them on demand (their
    // weapon-phantom volume is hit first). Their physics asset still defines every body, so use
    // the BodySetup defaults with the same material precedence the live body would get.
    sealed record DormantBody(nint Setup,BodyGeometry Geometry,ulong BoneName,nint DefaultMaterial,nint SetupMaterial);
    readonly Dictionary<nint,DormantBody[]> dormantAssets=new();
    List<LiveShape> DormantShapes(UObj mesh,nint meshAsset,string meshName,Dictionary<string,int> boneMap,HitLocation[] locations,ulong[] tags,bool hittable)
    {
        var result=new List<LiveShape>();
        var physics=mesh.Ptr("PhysicsAssetOverride");
        if(physics==0)physics=UObj.Of(meshAsset).Ptr("PhysicsAsset");
        if(physics==0)return result;
        if(!dormantAssets.TryGetValue(physics,out var setups))
        {
            // Physics assets and their body setups are immutable cooked data: read them once.
            var (data,n)=UObj.Of(physics).Array("SkeletalBodySetups");
            if(n<=0||n>2048||data==0)return result;
            var pointers=Bytes(data,n*8);var assetName=ObjectName(physics);var list=new List<DormantBody>();
            for(int i=0;i<n;i++)
            {
                var setup=(nint)BitConverter.ToInt64(pointers,i*8);if(setup==0)continue;
                var key=(assetName,ObjectName(setup));
                if(!bodies.TryGetValue(key,out var g))
                {g=new(){Name=key.Item2,Bone=Name(Read<ulong>(setup+0x28)),Shapes=LiveGeometry.Shapes(setup)};bodies[key]=g;}
                var defaults=UObj.Of(setup).At("DefaultInstance");
                list.Add(new(setup,g,Read<ulong>(setup+0x28),defaults==0?0:Ptr(defaults+0x140),Ptr(setup+0xa8)));
            }
            dormantAssets[physics]=setups=list.ToArray();
        }
        var componentMaterial=Ptr(mesh.Address+0x4d8);
        foreach(var d in setups)
        {
            var geometry=d.Geometry;
            var material=d.DefaultMaterial;
            if(material==0)material=componentMaterial;
            if(material==0)material=d.SetupMaterial;
            if(material==0)material=DefaultMaterial();
            var location=MatchLocation(locations,tags,material,meshName+":"+geometry.Bone);
            if(location==null)continue;
            if(!boneMap.TryGetValue(geometry.Bone,out int bone)||!BoneWorld(mesh.Address,bone,out var world))
            {Once("bone:"+meshName+":"+geometry.Bone,$"UNMAPPED pose {meshName}:{geometry.Bone}");continue;}
            // Collision responses are applied when the game creates the body; treat intact regions as hittable.
            foreach(var s in geometry.Shapes)if(s.HasQuery)result.Add(new(location,s,s.LocalMatrix*world,hittable&&!location.Destroyed,true,hittable){Parent=mesh.Address,Socket=d.BoneName,Anchor=mesh.Address,Bone=bone});
        }
        return result;
    }

    // Local player: world -> game instance -> first local player -> controller -> pawn.
    static bool LocalPlayer(UObj world,out UObj controller,out UObj pawn)
    {
        controller=pawn=default;
        var game=world.Obj("OwningGameInstance");
        if(!UE.IsType(game.Address,"GameInstance"))return false;
        var (players,count)=game.Array("LocalPlayers");
        if(count<=0||players==0)return false;
        var local=UObj.Of(Mem.Peek<nint>(players));if(local.IsNull)return false;
        controller=local.Obj("PlayerController");if(controller.IsNull)return false;
        pawn=controller.Obj("AcknowledgedPawn");
        return true;
    }

    // The game's frame clock: FCameraCacheEntry.Timestamp is the world time of the latest camera update.
    // Scanning faster than the game renders re-reads identical memory, so a scan without a new game frame
    // stops right there (but runs fully at least every FullScanMs, e.g. while the game is paused).
    float lastFrameStamp=float.NaN;long lastFullScan;
    const int FullScanMs=100;
    // Returns false when the game has not produced a new frame since the last full scan.
    bool DrawWorld()
    {
        if(Environment.TickCount64>inLevelUntil)inLevel=false;
        var world=UObj.Of(UE.World);
        if(world.Address==0||!UE.IsType(world.Address,"World")){Hook.SetEnabled(false,0);return true;}
        if(UE.BeginSnapshot(world.Address,Mem.Peek<nint>(world.At("Levels"))))WorldChanged();
        ReadChannels();
        if(!LocalPlayer(world,out var pc,out var player)||player.Address==0||!UE.IsType(player.Address,"CharacterGunfire")){Hook.SetEnabled(false,0);return true;}
        inLevel=true;inLevelUntil=Environment.TickCount64+1000;
        try{Hook.SetEnabled(force,player.Address);}
        catch(Exception e){force=false;Notify("Weakspot hits unavailable: "+e.Message);}
        if(hiLite)
        {
            SilhouetteScan(pc.Address);
            SilhouetteScan(pc.Ptr("PlayerState"));
        }
        var cameraCache=pc.Obj("PlayerCameraManager").Struct("CameraCachePrivate");
        var pov=cameraCache.Struct("POV");
        povLocation=pov.At("Location");povRotation=pov.At("Rotation");povFov=pov.At("FOV");
        float stamp=cameraCache.Get<float>("Timestamp");long nowMs=Environment.TickCount64;
        if(stamp==lastFrameStamp&&nowMs-lastFullScan<FullScanMs)return false;
        lastFrameStamp=stamp;lastFullScan=nowMs;
        var camera=Read<FVector>(povLocation).ToFloats();
        var rotation=Read<FVector>(povRotation).ToFloats();
        float fov=Read<float>(povFov);var size=displaySize;
        SetView(camera,rotation);
        var draw=recorder;
        ViewMath.Axes(rotation,out var viewDir,out _,out _);
        bool Front(Vector3 point)=>Vector3.Dot(point-camera,viewDir)>1;
        // Recorded in world space; the render thread projects with its own (current) camera.
        void Draw(LiveShape s,uint color,string label)
        {
            if(!Front(s.Center))return;
            foreach(var line in LocalLines(s.Geometry))
            {
                var a=Vector3.Transform(line[0],s.World);
                for(int i=1;i<line.Length;i++){var b=Vector3.Transform(line[i],s.World);draw.WorldLine(a,b,color);a=b;}
            }
            draw.WorldText(s.Center,color,s.Exact?label:label+" [simple hull]");
        }
        int weakEnemies=0;
        poseCache.Clear();poseBones.Clear();
        BeginMarkers(world);
        var playerFaction=player.Get<nint>("Faction");
        foreach(var candidate in Characters(world))
        {
                if(candidate==player.Address||!UE.IsType(candidate,"CharacterGunfire"))continue;
                markerOwnersAlive.Add(candidate);
                var ch=UObj.Of(candidate);
                var hitlog=ch.Obj("HitLogComp");
                if(ch.Get<float>("HealthNormalized")<=0||ch.Get<nint>("Faction")==playerFaction||hitlog.IsNull)continue;
                if(!InResolveRange(ch,camera))continue;
                if(hiLite)SilhouetteScan(ch.Address);
                try
                {
                    var hits=Locations(hitlog);
                    if(hits.Length==0)continue;
                    var all=ById(hits);
                    float weakThreshold=hitlog.Get<float>("WeakSpotDamageScalar"),resistThreshold=hitlog.Get<float>("ResistSpotDamageScalar");
                    AimCollectUnmarked(ch,hits,camera,weakThreshold,resistThreshold);
                    if(!showArmor&&!hits.Any(h=>!h.Destroyed&&h.IsWeak(weakThreshold,resistThreshold)))continue;
                    weakEnemies++;
                    var shapes=ShapesFor(ch,hits,camera,weakThreshold,resistThreshold);
                    var markers=new Dictionary<LiveShape,(uint Color,string Label)>();
                    foreach(var weak in hits.Where(h=>!h.Destroyed&&h.IsWeak(weakThreshold,resistThreshold)).DistinctBy(h=>h.Id))
                    {
                        var frontier=ProtectionResolver.Frontier(weak,all);
                        foreach(var id in frontier)
                        {
                            bool protection=id!=weak.Id;
                            foreach(var s in shapes.Where(s=>s.Location.Id==id))
                            {
                                if(protection&&!s.Active)continue;
                                markers[s]=(protection?0xff00a5ff:s.Active?0xffffff00:0x80909090,
                                    protection?$"{s.Location.Name} -> {weak.Name}":s.Active?weak.Name:weak.Name+" [inactive]");
                                if(!protection&&s.Active)
                                {
                                    float weakT=s.Hit(camera,s.Center)??1;
                                    var blocker=shapes.Where(b=>b.Active&&b.Blocks&&b.Exact&&b.Location.Id!=id&&!b.Location.IsWeak(weakThreshold,resistThreshold))
                                        .Select(b=>(Shape:b,T:b.Hit(camera,s.Center)))
                                        .Where(b=>b.T.HasValue&&b.T.Value<weakT-1e-4).OrderBy(b=>b.T.Value).FirstOrDefault();
                                    if(blocker.Shape!=null)markers[blocker.Shape]=(0xff00a5ff,blocker.Shape.Location.Name+" [occludes]");
                                }
                            }
                        }
                    }
                    if(showArmor)foreach(var s in shapes.Where(s=>s.Active&&s.Location.IsResisted(resistThreshold)))markers.TryAdd(s,(0xff8080ff,s.Location.Name+" [resist]"));
                    if(esp)foreach(var (shape,style) in markers)Draw(shape,style.Color,style.Label);
                    if(weakOnly)WantMarkers(ch,shapes,all,weakThreshold,resistThreshold,camera);
                    AimCollect(ch,hits,shapes,weakThreshold,resistThreshold);
                    Once("actor:"+ch.Address,$"{ObjectName(ch.Address)}: {hits.Length} locations; {shapes.Count} collision shapes; thresholds weak={weakThreshold}, resist={resistThreshold}");
                }
                catch(Exception e){Once("error:"+ObjectName(ch.Address)+":"+e.Message,$"{ObjectName(ch.Address)}: {e}");}
        }
        enemies=weakEnemies;
        FreezeTick(player);
        SyncMarkers();SyncTwins();
        AimUpdate(pc,player,camera,rotation,fov,draw,size);
        if(Environment.TickCount64>=nextTelemetry)
        {
            nextTelemetry=Environment.TickCount64+1000;ulong count=Hook.Count;
            if(count!=lastCount)Log($"FORCE gate hits={count}");
            lastCount=count;
        }
        return true;
    }

    // Levels unload their classes and assets; anything cached by their address may now be something else.
    void WorldChanged()
    {
        UFn.ClearCache();
        dormantAssets.Clear();componentKinds.Clear();chargeClassOk.Clear();
        sceneClassReset=true;
    }

    internal nint SilhouetteManagerOf(UObj world)
    {
        var gs=world.Ptr("GameState");if(gs==0)return 0;
        return OwnedComponents(gs).FirstOrDefault(c=>UE.IsType(c,"SilhouetteManager"));
    }
    static bool TokenIn(string haystack,string needle)
    {
        if(haystack.Length==0||needle.Length==0)return false;
        return needle.Split('_').Where(t=>t.Length>=4).Any(t=>haystack.Contains(t,StringComparison.OrdinalIgnoreCase));
    }
    // UPrimitiveComponent: bRenderCustomDepth = bit 3 of +0x2D0, CustomDepthStencilValue @+0x2FC.
    static bool CustomDepthOn(nint prim)=>(Read<byte>(prim+0x2D0)&8)!=0;
    // Custom depth changes run on the game thread (a remote-thread call raced the game destroying the
    // component). Re-application is held off while a change is still in flight.
    readonly Dictionary<nint,long> cdInFlight=new();
    bool CdInFlight(nint prim)=>cdInFlight.TryGetValue(prim,out long t)&&NowMs-t<500;
    void QueueCustomDepth(nint prim,bool on,int stencil)
    {
        if(GameThread==null)return;
        if(markerManager!=0)GameThread.Ensure(markerManager);
        if(!GameThread.Ready){Once("cd-no-gamethread","OUTLINE skipped: game-thread executor not ready");return;}
        var cls=Ptr(prim+UE.ObjClass);
        UFn s=UFn.Get(cls,"SetCustomDepthStencilValue"),r=UFn.Get(cls,"SetRenderCustomDepth");
        cdInFlight[prim]=NowMs;if(cdInFlight.Count>4096)cdInFlight.Clear();
        GameThread.Enqueue([new(prim,s,s.New().Int("Value",stencil).Data),new(prim,r,r.New().Bool("bValue",on).Data)],_=>{});
    }
    static bool RendersMesh(nint prim)=>UE.Bool(prim,"bHiddenInGame")!=true&&UE.Bool(prim,"bVisible")!=false;
    static int CustomDepthStencil(nint prim)=>Read<int>(prim+0x2FC);
    // The silhouette outline pass draws any primitive with custom depth on and an outline stencil.
    // Only primitives that render a mesh can be outlined; collision-only shapes and the body mesh are skipped.
    void OutlineWeakPrimitive(UObj ch,UObj component,List<LiveShape> shapes,HitLocation[] hits,float weakThreshold,float resistThreshold)
    {
        var address=component.Address;
        // Skeletal weakspots get bone markers instead (outlining a skeletal mesh outlines all of it).
        if(address==ch.Ptr("Mesh")||!UE.IsType(address,"StaticMeshComponent"))return;
        var name=ObjectName(address);
        bool weak=!RendersMesh(address)?false:shapes.Count>0
            ?shapes.Any(s=>!s.Location.Destroyed&&s.Location.IsWeak(weakThreshold,resistThreshold))
            :name.Contains("weakspot",StringComparison.OrdinalIgnoreCase)
             ||hits.Where(h=>!h.Destroyed&&h.IsWeak(weakThreshold,resistThreshold)).Any(h=>TokenIn(name,h.Name));
        bool enabled=cdEnabled.ContainsKey(address);
        // The game's own silhouette (e.g. Hunter's Mark ending) rewrites every owner primitive; re-apply after it.
        bool drifted=enabled&&!CdInFlight(address)&&(!CustomDepthOn(address)||CustomDepthStencil(address)!=outlineStencil);
        if(weak==enabled&&!drifted)return;
        try
        {
            if(weak)
            {
                if(!enabled)cdEnabled[address]=(CustomDepthOn(address),CustomDepthStencil(address));
                QueueCustomDepth(address,true,outlineStencil);
                Log($"OUTLINE {ObjectName(ch.Address)}:{name} stencil={outlineStencil}");
            }
            else RestoreOutline(address);
        }
        catch(Exception e){Once("outline:"+address,$"OUTLINE {name}: {e.GetType().Name}: {e.Message}");}
    }
    void RestoreOutline(nint address)
    {
        if(!cdEnabled.Remove(address,out var original))return;
        // Skip components the game destroyed; their memory may be reused.
        if(!UE.IsType(address,"PrimitiveComponent"))return;
        QueueCustomDepth(address,original.On,original.Stencil);
    }
    void RestoreOutlines()
    {
        foreach(var address in cdEnabled.Keys.ToArray())
            try{RestoreOutline(address);}catch(Exception e){Log($"OUTLINE restore {address:X}: {e.Message}");}
        cdEnabled.Clear();
    }
    IEnumerable<nint> OwnedComponents(nint actor)
    {
        int n=Read<int>(actor+0x218),bits=Read<int>(actor+0x238);
        var elements=Ptr(actor+0x210);
        if(n<0||n>4096||bits!=n||n>0&&elements==0)yield break;
        var allocation=Ptr(actor+0x230);if(allocation==0)allocation=actor+0x220;
        var flags=n==0?[]:Bytes(allocation,((n+31)/32)*4);
        var entries=n==0?[]:Bytes(elements,n*16);
        for(int i=0;i<n;i++)
        {
            if((flags[i>>3]&(1<<(i&7)))==0)continue;
            var ptr=(nint)BitConverter.ToInt64(entries,i*16);
            if(ptr!=0)yield return ptr;
        }
    }
    void SilhouetteScan(nint actor)
    {
        if(actor==0)return;
        if(silNext.TryGetValue(actor,out long next)&&Environment.TickCount64<next)return;
        silNext[actor]=Environment.TickCount64+5000;
        try
        {
            foreach(var ptr in OwnedComponents(actor))
            {
                if(UE.IsType(ptr,"SilhouetteManager"))
                {
                    var (data,n)=UObj.Of(ptr).Array("SilhouetteComponents");
                    if(n<0||n>4096)continue;
                    var raw=n==0?[]:Bytes(data,n*8);
                    for(int i=0;i<n;i++){var c=(nint)BitConverter.ToInt64(raw,i*8);if(c!=0)SilhouetteState(c,ObjectName(actor)+"[mgr]");}
                    continue;
                }
                if(UE.IsType(ptr,"SilhouetteComponent"))SilhouetteState(ptr,ObjectName(actor));
            }
        }
        catch(Exception e){Once("sil-err:"+actor,$"SILHOUETTE scan {ObjectName(actor)}: {e.GetType().Name}: {e.Message}");}
    }
    // USilhouetteComponent: Visibility (2 = always), MaxDistance, and RequiredTag / ViewerTag FNames that
    // restrict who sees it. Showing every enemy: always visible, any distance, no tag requirement.
    void SilhouetteState(nint ptr,string owner)
    {
        try
        {
            var sc=UObj.Of(ptr);
            int vis=sc.Get<int>("Visibility");float max=sc.Get<float>("MaxDistance");
            var (req,reqOff)=TagOf(ptr,"RequiredTag");var (view,viewOff)=TagOf(ptr,"ViewerTag");
            if(silLogged.Add(ptr))Log($"SIL {owner} {ObjectName(ptr)} @{ptr:X} vis={vis} max={max:F0} req='{req}' viewer='{view}'");
            if(!hiLite)return;
            RememberSilhouette(ptr,vis,max,reqOff,viewOff);
            if(vis!=2||max<90000){sc.Set("Visibility",2);sc.Set("MaxDistance",999999f);}
            foreach(var (tag,off) in new[]{(req,reqOff),(view,viewOff)})
            {
                if(off<0||tag=="None")continue;
                Mem.Write(ptr+off,0UL);
            }
        }
        catch(Exception e){Once("sil-comp:"+ptr,$"SILHOUETTE {owner} @{ptr:X}: {e.GetType().Name}: {e.Message}");}
    }
    static (string,int) TagOf(nint component,string field)
    {
        try
        {
            int off=UE.Offset(component,field);
            if(off<=0||off>0x400)return ("<off"+off+">",-1);
            return (Name(Read<ulong>(component+off)),off);
        }
        catch(Exception e){return ("<"+e.GetType().Name+">",-1);}
    }

}
