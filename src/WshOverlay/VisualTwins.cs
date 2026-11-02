
// Weakspot outline in the weakspot's real shape: many enemies render the weak region with its own mesh
// component named after the hit location (range dummy "Head" -> Visual_Head = the bucket; humanoid
// heads -> Visual_Head). When such a "visual twin" exists, F10 outlines that mesh (custom depth on the
// real geometry) instead of attaching a hitbox-shaped marker. Without one, the hitbox marker is used.
internal sealed partial class GameOverlay
{
    bool visualTwinOutline=true;   // Weakspots window: "Outline the weakspot's own mesh when available"
    bool prereqOutline=true;       // Weakspots window: "Outline what must break first (plates, covers)"
    readonly Dictionary<(nint Owner,ulong Location),(nint Component,long Until)> twinCache=new();
    readonly Dictionary<nint,nint> twinOwner=new();     // outlined twin -> owner actor
    readonly HashSet<nint> twinWanted=new();            // twins wanted this world tick

    nint VisualTwin(UObj ch,HitLocation location)
    {
        long now=Environment.TickCount64;
        if(twinCache.TryGetValue((ch.Address,location.Id),out var c)&&now<c.Until)return c.Component;
        nint found=0;
        try
        {
            var mesh=ch.Ptr("Mesh");
            foreach(var comp in OwnedComponents(ch.Address))
            {
                if(comp==mesh||!UE.IsType(comp,"MeshComponent"))continue;
                var name=ObjectName(comp);
                if(name.StartsWith("StaticMeshComponent_"))continue; // runtime-added (e.g. our markers)
                // Token match ("Visual_Head" -> Head, "Visual_Plate_L" -> Plate_L).
                if(!("_"+name+"_").Contains("_"+location.Name+"_",StringComparison.OrdinalIgnoreCase))continue;
                if(UE.IsType(comp,"SkeletalMeshComponent")&&Ptr(comp+0x5a8)==0&&Ptr(comp+0x5b0)==0)continue; // no mesh
                if(!RendersMesh(comp))continue;
                found=comp;break;
            }
        }
        catch{found=0;}
        if(twinCache.Count>1024)twinCache.Clear();
        twinCache[(ch.Address,location.Id)]=(found,now+5000);
        if(found!=0)Once($"twin:{ch.Address:X}:{location.Id:X}",$"OUTLINE {ActorName(ch)}:{location.Name} uses its own mesh {ObjectName(found)} ({ObjectName(Ptr(found+UE.ObjClass))})");
        return found;
    }

    // Called from WantMarkers for each weak (or prerequisite) active location; returns true when the twin covers it.
    bool WantTwin(UObj ch,HitLocation location,int stencil)
    {
        if(!visualTwinOutline)return false;
        var twin=VisualTwin(ch,location);if(twin==0)return false;
        twinWanted.Add(twin);twinOwner[twin]=ch.Address;
        bool enabled=cdEnabled.ContainsKey(twin);
        bool drifted=enabled&&!CdInFlight(twin)&&(!CustomDepthOn(twin)||CustomDepthStencil(twin)!=stencil);
        if(!enabled||drifted)
        {
            try
            {
                if(!enabled)cdEnabled[twin]=(CustomDepthOn(twin),CustomDepthStencil(twin));
                QueueCustomDepth(twin,true,stencil);
            }
            catch(Exception e){Once("twin-set:"+twin,$"OUTLINE twin {ObjectName(twin)}: {e.GetType().Name}: {e.Message}");return false;}
        }
        return true;
    }

    // End of a world tick: twins no longer wanted (weakspot destroyed/inactive, out of range) get their
    // original custom depth back. Owners that vanished took their components with them.
    void SyncTwins()
    {
        foreach(var (twin,owner) in twinOwner.ToArray())
        {
            if(twinWanted.Contains(twin))continue;
            twinOwner.Remove(twin);
            if(!markerOwnersAlive.Contains(owner)){cdEnabled.Remove(twin);continue;}
            try{RestoreOutline(twin);}catch{}
        }
        twinWanted.Clear();
    }
}
