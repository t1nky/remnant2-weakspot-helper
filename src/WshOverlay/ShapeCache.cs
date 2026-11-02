using System.Numerics;

// Resolving an enemy's collision shapes costs ~10-130 ms of cross-process reads, so doing it for
// every enemy every frame dropped the overlay to ~1.4 FPS. Near enemies are resolved every frame;
// farther ones are cached and refreshed on a timer; enemies beyond ResolveRange are skipped.
internal sealed partial class GameOverlay
{
    const float ResolveRange=20000;   // cm
    const float NearRange=3000;       // resolve every frame inside this distance
    const float MidRange=8000;
    const int NearRefreshMs=250,MidRefreshMs=2000,FarRefreshMs=5000,OffscreenRefreshMs=10000,MaxStaleMs=12000; // pose is re-applied every frame; these only refresh structure
    sealed record ShapeCacheEntry(long Next,List<LiveShape> Shapes,string DestroyedState);
    readonly Dictionary<nint,ShapeCacheEntry> shapeCache=new();
    long shapeCachePrune;
    readonly Dictionary<nint,long> slowLogNext=new();
    // Non-near refreshes share a per-frame time budget so far enemies never stall the frame.
    const double FarBudgetMs=4;
    int shapeBudgetFrame=-1;double shapeBudgetUsed;

    // Off-screen enemies keep their cached pose (nothing is drawn or aimed there).
    Vector3 viewOrigin,viewForward;
    void SetView(Vector3 camera,Vector3 rotation)
    {
        float pitch=rotation.X*MathF.PI/180,yaw=rotation.Y*MathF.PI/180;
        viewOrigin=camera;viewForward=new(MathF.Cos(pitch)*MathF.Cos(yaw),MathF.Cos(pitch)*MathF.Sin(yaw),MathF.Sin(pitch));
    }
    bool InView(UObj ch)
    {
        var to=Read<FTransform>(ch.Ptr("Mesh")+0x240).ToMatrixWithScale().Translation-viewOrigin;
        float len=to.Length();
        return len<1500||Vector3.Dot(to/len,viewForward)>0.35f; // ~70° half-angle: covers wide FOVs plus margin
    }
    // Cached structure, current pose: bones come from the per-frame pose snapshot, components from
    // one transform read each. This keeps every cached enemy's shapes where the enemy is now.
    List<LiveShape> Repose(List<LiveShape> shapes)
    {
        var components=reposeComponents;components.Clear();var result=new List<LiveShape>(shapes.Count);
        var bonesSeen=reposeBones;bonesSeen.Clear();
        foreach(var s in shapes)
        {
            Matrix4x4 anchor;
            if(s.Bone>=0)
            {
                if(!bonesSeen.TryGetValue((s.Anchor,s.Bone),out var boneMatrix))
                    bonesSeen[(s.Anchor,s.Bone)]=boneMatrix=BoneWorld(s.Anchor,s.Bone,out var m)?m:(Matrix4x4?)null;
                if(boneMatrix==null){result.Add(s);continue;}
                anchor=boneMatrix.Value;
            }
            else if(s.Anchor!=0)
            {
                if(!components.TryGetValue(s.Anchor,out anchor))
                    components[s.Anchor]=anchor=Read<FTransform>(s.Anchor+0x240).ToMatrixWithScale();
            }
            else{result.Add(s);continue;}
            result.Add(s with{World=s.Geometry.LocalMatrix*anchor});
        }
        return result;
    }
    // Scratch maps reused by Repose (scan thread only).
    readonly Dictionary<nint,Matrix4x4> reposeComponents=new();
    readonly Dictionary<(nint,int),Matrix4x4?> reposeBones=new();

    float EnemyDistance(UObj ch,Vector3 camera)=>
        Vector3.Distance(Read<FTransform>(ch.Ptr("Mesh")+0x240).ToMatrixWithScale().Translation,camera);

    bool InResolveRange(UObj ch,Vector3 camera)
    {
        try{return EnemyDistance(ch,camera)<=ResolveRange;}catch{return false;}
    }

    List<LiveShape> ShapesFor(UObj ch,HitLocation[] hits,Vector3 camera,float weakThreshold,float resistThreshold)
    {
        long now=Environment.TickCount64;
        if(now>=shapeCachePrune)
        {
            shapeCachePrune=now+5000;
            foreach(var stale in shapeCache.Where(p=>p.Value.Next<now-10000).Select(p=>p.Key).ToArray())shapeCache.Remove(stale);
        }
        float distance=EnemyDistance(ch,camera);
        // A destroyed/restored location changes which shapes are weak or active: refresh at once.
        var state=DestroyedState(hits);
        if(shapeBudgetFrame!=frame){shapeBudgetFrame=frame;shapeBudgetUsed=0;}
        bool near=distance<=NearRange;
        bool hasCache=shapeCache.TryGetValue(ch.Address,out var cached);
        // Over budget, an overdue entry may wait - but never longer than MaxStaleMs (no starvation).
        // Near enemies refresh structure every 250 ms regardless of budget; pose is re-applied every frame.
        if(hasCache&&cached.DestroyedState==state&&(now<cached.Next||!near&&shapeBudgetUsed>=FarBudgetMs&&now-cached.Next<MaxStaleMs))
            return near||InView(ch)?Repose(cached.Shapes):cached.Shapes;
        // Far enemies never seen before wait for budget too (they appear over the next frames).
        if(!near&&!hasCache&&shapeBudgetUsed>=FarBudgetMs)return [];
        var resolveTimer=System.Diagnostics.Stopwatch.StartNew();
        var shapes=new List<LiveShape>();
        foreach(var component in Components(ch))
        {
            try
            {
                var cs=PrimitiveShapes(component,hits);
                shapes.AddRange(cs);
                if(weakOnly)OutlineWeakPrimitive(ch,component,cs,hits,weakThreshold,resistThreshold);
                else ClearLeftoverOutline(ch,component,cs,hits,weakThreshold,resistThreshold);
            }
            catch(Exception e){Once("component-error:"+component.Address+":"+e.Message,$"{ObjectName(component.Address)} ({ObjectName(Ptr(component.Address+UE.ObjClass))}): {e.GetType().Name}: {e.Message}");}
        }
        int interval=distance<=NearRange?NearRefreshMs:distance<=MidRange?MidRefreshMs:InView(ch)?FarRefreshMs:OffscreenRefreshMs;
        if(!near)shapeBudgetUsed+=resolveTimer.Elapsed.TotalMilliseconds;
        shapeCache[ch.Address]=new(now+interval,shapes,state);
        if(resolveTimer.ElapsedMilliseconds>=15&&now>=slowLogNext.GetValueOrDefault(ch.Address))
        {slowLogNext[ch.Address]=now+10000;Log($"SLOW shapes {ObjectName(ch.Address)} {resolveTimer.ElapsedMilliseconds} ms dist={distance/100:F0}m shapes={shapes.Count} cachedBefore={shapeCache.ContainsKey(ch.Address)} interval={interval}");}
        return shapes;
    }
}
