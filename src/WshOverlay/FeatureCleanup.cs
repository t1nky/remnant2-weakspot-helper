
// Undo for every in-game modification. Toggles are tracked by state (not by hotkey) so turning a
// feature off from the menu checkbox cleans up exactly like the hotkey does.
//  - F9  silhouettes: restore Visibility, MaxDistance and the RequiredTag/ViewerTag FNames.
//  - F10 outlines:    restore custom depth on weakspot meshes; destroy every marker component.
//  - Exit (End):      all of the above, synchronously.
// Leftovers from an overlay that was killed are removed on the next start (markers always,
// weakspot-mesh outlines when no game silhouette is active on that enemy).
internal sealed partial class GameOverlay
{
    bool prevHiLite,prevWeakOnly;
    sealed record SilhouetteOriginal(ulong Slot,int Visibility,float MaxDistance,int RequiredOffset,ulong RequiredTag,int ViewerOffset,ulong ViewerTag);
    readonly Dictionary<nint,SilhouetteOriginal> silOriginals=new();

    void ApplyToggleCleanup()
    {
        if(prevHiLite&&!hiLite)RestoreSilhouettes();
        if(prevWeakOnly&&!weakOnly){RestoreOutlines();Log("In-game weakspot outline OFF: outlines restored; markers are being removed");}
        prevHiLite=hiLite;prevWeakOnly=weakOnly;
    }

    void RestoreAll()
    {
        UnfreezeAll("exit");freezeEnemies=prevFreeze=false;
        RestoreSilhouettes();
        RestoreOutlines();
        weakOnly=false;
        ClearMarkers();
        GameThread?.Drain();
    }

    // Marker removal normally runs inside DrawWorld (game focused); finish it while unfocused too.
    void ReleaseMarkersUnfocused()
    {
        if(weakOnly||(markerLive.Count==0&&markerUnconfigured.Count==0))return;
        markerWanted.Clear();
        SyncMarkers();
    }

    void RememberSilhouette(nint ptr,int visibility,float maxDistance,int requiredOffset,int viewerOffset)
    {
        if(silOriginals.ContainsKey(ptr))return;
        silOriginals[ptr]=new(UE.SlotKey(ptr),visibility,maxDistance,
            requiredOffset,requiredOffset>0?Read<ulong>(ptr+requiredOffset):0,viewerOffset,viewerOffset>0?Read<ulong>(ptr+viewerOffset):0);
    }

    void RestoreSilhouettes()
    {
        int restored=0;
        foreach(var (ptr,o) in silOriginals)
        {
            try
            {
                // Skip components destroyed since (their memory may now belong to something else).
                if(!UE.LiveInSlot(ptr,o.Slot))continue;
                var sc=UObj.Of(ptr);
                sc.Set("Visibility",o.Visibility);sc.Set("MaxDistance",o.MaxDistance);
                if(o.RequiredOffset>0)Mem.Write(ptr+o.RequiredOffset,o.RequiredTag);
                if(o.ViewerOffset>0)Mem.Write(ptr+o.ViewerOffset,o.ViewerTag);
                restored++;
            }
            catch(Exception e){Log($"SIL restore {ptr:X}: {e.GetType().Name}: {e.Message}");}
        }
        if(silOriginals.Count>0)Log($"In-game silhouette highlight OFF: restored {restored}/{silOriginals.Count} silhouette components");
        silOriginals.Clear();silNext.Clear();
    }

    // A weakspot mesh still outlined while F10 is off and the enemy has no game silhouette active
    // was left by an overlay that did not exit cleanly: turn it back off (once per component).
    readonly HashSet<nint> leftoverChecked=new();
    void ClearLeftoverOutline(UObj ch,UObj component,List<LiveShape> shapes,HitLocation[] hits,float weakThreshold,float resistThreshold)
    {
        var address=component.Address;
        if(!leftoverChecked.Add(address)||address==ch.Ptr("Mesh")||!UE.IsType(address,"StaticMeshComponent"))return;
        if(!CustomDepthOn(address)||CustomDepthStencil(address) is <252 or >254||CustomDepthOn(ch.Ptr("Mesh")))return;
        var name=ObjectName(address);
        bool weak=shapes.Count>0?shapes.Any(s=>s.Location.IsWeak(weakThreshold,resistThreshold))
            :name.Contains("weakspot",StringComparison.OrdinalIgnoreCase)||hits.Where(h=>h.IsWeak(weakThreshold,resistThreshold)).Any(h=>TokenIn(name,h.Name));
        if(!weak)return;
        try{QueueCustomDepth(address,false,CustomDepthStencil(address));Log($"CLEANUP leftover outline {ObjectName(ch.Address)}:{name}");}
        catch(Exception e){Once("leftover:"+address,$"CLEANUP {name}: {e.GetType().Name}: {e.Message}");}
    }
}
