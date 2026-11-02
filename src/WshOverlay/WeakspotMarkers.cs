using System.Numerics;

// In-game weakspot outlines for weakspots that have no mesh of their own (skeletal bones,
// collision-only shape components). Each active weakspot shape gets an engine sphere/cube
// StaticMeshComponent attached to its bone or shape component, with collision off and drawn
// only into custom depth, so the game's silhouette post-process outlines exactly that volume.
// All UObject calls run on the game thread through GameThread.
internal sealed partial class GameOverlay
{
    public GameThread GameThread { get; set; }
    const float MarkerRange=15000; // cm; markers are created only for nearby weakspots
    const int MarkerCreatesPerBatch=12;
    const float MarkerInflate=1.04f;
    sealed record MarkerSpec(nint Owner,nint Parent,ulong Socket,Quaternion Rotation,Vector3 Translation,Vector3 Scale,bool Cube,string Label,int Stencil);
    sealed record Marker(nint Owner,nint Component,ulong WeakKey,string Label);
    readonly Dictionary<string,MarkerSpec> markerWanted=new();
    readonly Dictionary<string,Marker> markerLive=new();
    readonly Dictionary<string,(MarkerSpec Spec,nint Component)> markerUnconfigured=new();
    readonly HashSet<string> markerInFlight=new();
    readonly Dictionary<string,int> markerAttempts=new(); // guards against a create/reject loop
    readonly HashSet<nint> markerOwnersAlive=new();
    nint markerSphere,markerCube,markerClass,markerManager,markerGameState;
    bool markerAssetsSearched,markerRestyle;
    readonly HashSet<nint> markerOrphanScanned=new();

    // Markers left by an overlay that exited without cleanup: auto-named StaticMeshComponent_N
    // using an engine BasicShapes sphere/cube (authored hurtboxes have their own names).
    void CollectOrphans(List<GameThread.Call> calls,ref int destroyed)
    {
        foreach(var owner in markerOwnersAlive)
        {
            if(!markerOrphanScanned.Add(owner))continue;
            foreach(var c in OwnedComponents(owner))
            {
                var cls=Ptr(c+UE.ObjClass);
                if(!ObjectName(c).StartsWith("StaticMeshComponent_")||ObjectName(cls)!="StaticMeshComponent")continue;
                var mesh=new UObj(c,cls).Ptr("StaticMesh");
                if(ObjectPath(mesh) is not ("/Engine/BasicShapes/Sphere.Sphere" or "/Engine/BasicShapes/Cube.Cube"))continue;
                if(markerLive.Values.Any(m=>m.Component==c))continue;
                Destroy(calls,c);destroyed++;
            }
        }
    }

    // Labels only need a stable name; refreshed every few seconds in case an address gets reused.
    readonly Dictionary<nint,(string Name,long Until)> actorNames=new();
    string ActorName(UObj ch)
    {
        long now=Environment.TickCount64;
        if(actorNames.TryGetValue(ch.Address,out var n)&&now<n.Until)return n.Name;
        var name=ObjectName(ch.Address);actorNames[ch.Address]=(name,now+5000);return name;
    }
    void BeginMarkers(UObj world)
    {
        markerWanted.Clear();markerOwnersAlive.Clear();
        var gs=world.Ptr("GameState");
        if(gs!=markerGameState){markerGameState=gs;markerManager=SilhouetteManagerOf(world);}
    }

    // Prerequisites (armor plates etc. that must break before a covered weakspot becomes hittable) use the
    // other outline color: white next to the default orange, orange when white is the weakspot style.
    int PrereqStencil=>outlineStencil==253?254:253;

    void WantMarkers(UObj ch,List<LiveShape> shapes,IReadOnlyDictionary<ulong,HitLocation> all,float weakThreshold,float resistThreshold,Vector3 camera)
    {
        bool Near(LiveShape s)=>Vector3.Distance(s.Center,camera)<=MarkerRange;
        // What to break first: for each covered weakspot, the active prerequisites on its protection frontier.
        var prereq=new HashSet<ulong>();
        if(prereqOutline)
            foreach(var weak in all.Values.Where(h=>!h.Destroyed&&h.IsWeak(weakThreshold,resistThreshold)))
                foreach(var id in ProtectionResolver.Frontier(weak,all))
                    if(id!=weak.Id&&!all[id].IsWeak(weakThreshold,resistThreshold)&&shapes.Any(s=>s.Location.Id==id&&s.Active))prereq.Add(id);
        // Locations whose own mesh is outlined (VisualTwins) need no hitbox marker.
        var twinned=new HashSet<ulong>();
        foreach(var loc in shapes.Where(s=>s.Active&&!s.Location.Destroyed&&Near(s)&&(prereq.Contains(s.Location.Id)||s.Location.IsWeak(weakThreshold,resistThreshold)))
                    .Select(s=>s.Location).DistinctBy(l=>l.Id))
            if(WantTwin(ch,loc,prereq.Contains(loc.Id)?PrereqStencil:outlineStencil))twinned.Add(loc.Id);
        foreach(var s in shapes)
        {
            if(s.Parent==0||!s.Active||s.Location.Destroyed)continue;
            bool isPrereq=prereq.Contains(s.Location.Id);
            if(!isPrereq&&!s.Location.IsWeak(weakThreshold,resistThreshold))continue;
            // Prerequisites without a mesh of their own: only component hurtboxes, not whole physics assets.
            if(isPrereq&&s.Socket!=0)continue;
            if(twinned.Contains(s.Location.Id))continue;
            if(!Near(s))continue;
            if(!MarkerTransform(s.Geometry,out var r,out var t,out var scale,out bool cube))continue;
            var g=s.Geometry;int stencil=isPrereq?PrereqStencil:outlineStencil;
            // Keyed without the parent: follower meshes (e.g. Visual_Head) share the leader's physics
            // asset, so the same body would otherwise get one marker per mesh.
            var key=$"{ch.Address:X}:{s.Socket:X}:{s.Location.Id:X}:{g.Kind}:{t.X:F0},{t.Y:F0},{t.Z:F0}:{scale.X:F2},{scale.Y:F2},{scale.Z:F2}:{stencil}";
            markerWanted.TryAdd(key,new(ch.Address,s.Parent,s.Socket,r,t,scale,cube,$"{ActorName(ch)}:{s.Location.Name}{(isPrereq?" [prerequisite]":"")}",stencil));
        }
    }

    // Engine BasicShapes: Sphere has radius 50, Cube has edge 100, both centred at the origin.
    static bool MarkerTransform(ShapeGeometry g,out Quaternion rotation,out Vector3 translation,out Vector3 scale,out bool cube)
    {
        Matrix4x4 local;cube=false;
        switch(g.Kind)
        {
            case "Sphere":local=Matrix4x4.CreateScale(g.Radius/50);break;
            case "Sphyl":local=Matrix4x4.CreateScale(g.Radius/50,g.Radius/50,(g.Length/2+g.Radius)/50);break;
            case "Box":local=Matrix4x4.CreateScale(g.X/100,g.Y/100,g.Z/100);cube=true;break;
            case "Convex" when g.VertexData.Length>0:
            {
                var lo=new Vector3(float.MaxValue);var hi=new Vector3(float.MinValue);
                foreach(var v in g.VertexData){lo=Vector3.Min(lo,v.Value);hi=Vector3.Max(hi,v.Value);}
                local=Matrix4x4.CreateScale(Vector3.Max((hi-lo)/100,new(0.01f)))*Matrix4x4.CreateTranslation((lo+hi)/2);cube=true;break;
            }
            default:rotation=default;translation=default;scale=default;return false;
        }
        var m=Matrix4x4.CreateScale(MarkerInflate)*local*g.LocalMatrix;
        if(!Matrix4x4.Decompose(m,out scale,out rotation,out translation)||scale.X<=0||scale.Y<=0||scale.Z<=0)return false;
        return true;
    }

    bool EnsureMarkerAssets()
    {
        if(markerClass!=0&&markerSphere!=0&&markerCube!=0)return true;
        if(markerAssetsSearched)return false;
        markerAssetsSearched=true;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var meshes=UE.FindObjects("StaticMesh",p=>p is "/Engine/BasicShapes/Sphere.Sphere" or "/Engine/BasicShapes/Cube.Cube");
        var classes=UE.FindObjects("Class",p=>p=="/Script/Engine.StaticMeshComponent",1);
        meshes.TryGetValue("/Engine/BasicShapes/Sphere.Sphere",out markerSphere);
        meshes.TryGetValue("/Engine/BasicShapes/Cube.Cube",out markerCube);
        classes.TryGetValue("/Script/Engine.StaticMeshComponent",out markerClass);
        Log($"MARKERS assets sphere={markerSphere:X} cube={markerCube:X} class={markerClass:X} ({timer.ElapsedMilliseconds} ms)");
        return markerClass!=0&&markerSphere!=0&&markerCube!=0;
    }

    // A marker component is safe to call only while it is still the same live object owned by its actor.
    bool MarkerValid(nint owner,nint component,ulong weakKey)=>
        markerOwnersAlive.Contains(owner)&&UE.LiveInSlot(component,weakKey)&&Ptr(component+UE.ObjOuter)==owner;

    // One marker batch at a time, queued behind other game-thread work (charge polling, traces) instead of
    // waiting for an idle executor, which starved marker updates while a charge weapon was drawn.
    bool markerBatchInFlight;long markerBatchSince;
    void SyncMarkers()
    {
        if(GameThread==null)return;
        try
        {
            GameThread.Poll();
            if(markerBatchInFlight)
            {
                if(NowMs-markerBatchSince<3000)return;
                markerBatchInFlight=false;markerInFlight.Clear();Log("MARKERS batch got no answer from the game; retrying");
            }
            if(markerRestyle){markerWanted.Clear();}
            bool orphanScan=markerOwnersAlive.Any(o=>!markerOrphanScanned.Contains(o));
            if(markerWanted.Count==0&&markerLive.Count==0&&markerUnconfigured.Count==0&&!orphanScan){markerRestyle=false;return;}
            if(markerManager==0){Once("marker-manager","MARKERS unavailable: no SilhouetteManager in this world");return;}
            var calls=new List<GameThread.Call>();var completions=new List<Action<Func<int,byte[]>>>();
            int destroyed=0,configured=0,created=0;
            CollectOrphans(calls,ref destroyed);
            bool creating=markerWanted.Count>0||markerUnconfigured.Count>0;
            if(calls.Count==0&&markerLive.Count==0&&!creating){markerRestyle=false;return;}
            GameThread.Ensure(markerManager);
            if(!GameThread.Ready)return;
            if(creating&&!EnsureMarkerAssets()){markerWanted.Clear();markerUnconfigured.Clear();}

            // Owners that vanished take their components with them; just forget those.
            foreach(var (key,m) in markerLive.ToArray())
            {
                if(!markerOwnersAlive.Contains(m.Owner)){markerLive.Remove(key);continue;}
                if(markerWanted.ContainsKey(key))continue;
                markerLive.Remove(key);
                if(!MarkerValid(m.Owner,m.Component,m.WeakKey))continue;
                Destroy(calls,m.Component);destroyed++;
            }
            // Created in the previous batch (deferred, unregistered): configure, attach, register.
            foreach(var (key,(spec,comp)) in markerUnconfigured.ToArray())
            {
                markerUnconfigured.Remove(key);
                ulong weak=UE.SlotKey(comp);
                if(!MarkerValid(spec.Owner,comp,weak))
                {
                    Once("marker-invalid:"+spec.Label,$"MARKER new component rejected {spec.Label}: comp={comp:X} class={ObjectName(Ptr(comp+UE.ObjClass))} outer={Ptr(comp+UE.ObjOuter):X} owner={spec.Owner:X} alive={markerOwnersAlive.Contains(spec.Owner)} weak={weak:X} live={UE.LiveInSlot(comp,weak)}");
                    continue;
                }
                if(!markerWanted.ContainsKey(key)){Destroy(calls,comp);destroyed++;continue;}
                int attach=Configure(calls,spec,comp);configured++;
                completions.Add(read=>
                {
                    var fn=UFn.Get(markerClass,"K2_AttachToComponent");
                    bool ok=read(attach)[fn.Params["ReturnValue"].Offset]!=0;
                    if(ok){markerLive[key]=new(spec.Owner,comp,weak,spec.Label);markerAttempts.Remove(key);}
                    Once("marker-attach:"+key,$"MARKER {(ok?"on":"ATTACH FAILED")} {spec.Label} {(spec.Cube?"box":"sphere")} scale={spec.Scale.X:F2},{spec.Scale.Y:F2},{spec.Scale.Z:F2}");
                });
            }
            foreach(var (key,spec) in markerWanted)
            {
                if(created>=MarkerCreatesPerBatch)break;
                if(markerLive.ContainsKey(key)||markerUnconfigured.ContainsKey(key)||markerInFlight.Contains(key))continue;
                if(!markerOwnersAlive.Contains(spec.Owner))continue;
                int attempts=markerAttempts.GetValueOrDefault(key);
                if(attempts>=3){Once("marker-giveup:"+key,$"MARKER giving up on {spec.Label} after {attempts} attempts");continue;}
                markerAttempts[key]=attempts+1;
                var fn=UFn.Get(Ptr(spec.Owner+UE.ObjClass),"AddComponentByClass");
                int index=calls.Count;markerInFlight.Add(key);created++;
                calls.Add(new(spec.Owner,fn,fn.New().Ptr("Class",markerClass).Bool("bManualAttachment",true)
                    .Transform("RelativeTransform",Quaternion.Identity,Vector3.Zero,Vector3.One).Bool("bDeferredFinish",true).Data));
                completions.Add(read=>
                {
                    markerInFlight.Remove(key);
                    var comp=UFn.ReturnPtr(fn,read(index));
                    if(comp!=0)markerUnconfigured[key]=(spec,comp);else Once("marker-create:"+key,$"MARKER create failed {spec.Label}");
                });
            }
            markerRestyle=markerRestyle&&(markerLive.Count>0||markerUnconfigured.Count>0);
            if(calls.Count==0)return;
            var batch=completions.ToArray();
            markerBatchInFlight=true;markerBatchSince=NowMs;
            GameThread.Enqueue(calls,read=>{markerBatchInFlight=false;foreach(var c in batch)c(read);});
            if(destroyed+configured+created>0)Log($"MARKERS batch: +{created} create, {configured} configure, -{destroyed} destroy ({calls.Count} calls; live {markerLive.Count})");
        }
        catch(Exception e){Once("markers:"+e.Message,"MARKERS error: "+e);}
    }

    void Destroy(List<GameThread.Call> calls,nint component)
    {
        var fn=UFn.Get(Ptr(component+UE.ObjClass),"K2_DestroyComponent");
        calls.Add(new(component,fn,fn.New().Ptr("Object",component).Data));
    }

    // Returns the call index of K2_AttachToComponent (its ReturnValue reports success).
    int Configure(List<GameThread.Call> calls,MarkerSpec spec,nint comp)
    {
        GameThread.Call C(string name,Func<UFn.Args,UFn.Args> set){var fn=UFn.Get(markerClass,name);return new(comp,fn,set(fn.New()).Data);}
        calls.Add(C("SetStaticMesh",a=>a.Ptr("NewMesh",spec.Cube?markerCube:markerSphere)));
        calls.Add(C("SetCollisionEnabled",a=>a.Byte("NewType",0)));          // NoCollision: never affects hits
        calls.Add(C("SetCastShadow",a=>a.Bool("NewCastShadow",false)));
        calls.Add(C("SetRenderInMainPass",a=>a.Bool("bValue",false)));        // invisible in the scene...
        calls.Add(C("SetRenderInDepthPass",a=>a.Bool("bValue",false)));
        calls.Add(C("SetRenderCustomDepth",a=>a.Bool("bValue",true)));        // ...but drawn into custom depth
        calls.Add(C("SetCustomDepthStencilValue",a=>a.Int("Value",spec.Stencil)));
        int attach=calls.Count;
        calls.Add(C("K2_AttachToComponent",a=>a.Ptr("Parent",spec.Parent).Name("SocketName",spec.Socket)
            .Byte("LocationRule",0).Byte("RotationRule",0).Byte("ScaleRule",0).Bool("bWeldSimulatedBodies",false)));
        var finish=UFn.Get(Ptr(spec.Owner+UE.ObjClass),"FinishAddComponent");
        calls.Add(new(spec.Owner,finish,finish.New().Ptr("Component",comp).Bool("bManualAttachment",true)
            .Transform("RelativeTransform",spec.Rotation,spec.Translation,spec.Scale).Data));
        return attach;
    }

    // Used on exit / F10 off: synchronously remove every marker we created.
    void ClearMarkers(int timeoutMs=1500)
    {
        if(GameThread==null)return;
        var until=Environment.TickCount64+timeoutMs;
        while(Environment.TickCount64<until&&(markerLive.Count>0||markerUnconfigured.Count>0||markerBatchInFlight))
        {
            markerWanted.Clear();
            if(!markerBatchInFlight)SyncMarkers();
            Thread.Sleep(15);GameThread.Poll();
        }
    }
}
