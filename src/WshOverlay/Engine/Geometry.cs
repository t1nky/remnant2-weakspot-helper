using System.Numerics;
using System.Runtime.InteropServices;

// Engine math types (UE5 uses doubles) and collision shapes read from the running game.
[StructLayout(LayoutKind.Sequential)]
internal struct FVector
{
    public double X,Y,Z;
    public readonly Vector3 ToFloats()=>new((float)X,(float)Y,(float)Z);
}
[StructLayout(LayoutKind.Sequential)]
internal struct FTransform
{
    public double QX,QY,QZ,QW,TX,TY,TZ,TW,SX,SY,SZ,SW;
    // Row-vector matrix: scale, then rotation, then translation (FTransform::ToMatrixWithScale).
    public readonly Matrix4x4 ToMatrixWithScale()
    {
        double x2=QX+QX,y2=QY+QY,z2=QZ+QZ,xx=QX*x2,yy=QY*y2,zz=QZ*z2,yz=QY*z2,wx=QW*x2,xy=QX*y2,wz=QW*z2,xz=QX*z2,wy=QW*y2;
        return new Matrix4x4
        {
            M11=(float)((1-(yy+zz))*SX),M12=(float)((xy+wz)*SX),M13=(float)((xz-wy)*SX),
            M21=(float)((xy-wz)*SY),M22=(float)((1-(xx+zz))*SY),M23=(float)((yz+wx)*SY),
            M31=(float)((xz+wy)*SZ),M32=(float)((yz-wx)*SZ),M33=(float)((1-(xx+yy))*SZ),
            M41=(float)TX,M42=(float)TY,M43=(float)TZ,M44=1
        };
    }
}
internal static class ViewMath
{
    // Forward (X), right (Y) and up (Z) axes of a rotator (pitch, yaw, roll in degrees).
    public static void Axes(Vector3 rotation,out Vector3 x,out Vector3 y,out Vector3 z)
    {
        float p=rotation.X*MathF.PI/180,yw=rotation.Y*MathF.PI/180,r=rotation.Z*MathF.PI/180;
        float sp=MathF.Sin(p),cp=MathF.Cos(p),sy=MathF.Sin(yw),cy=MathF.Cos(yw),sr=MathF.Sin(r),cr=MathF.Cos(r);
        x=new(cp*cy,cp*sy,sp);
        y=new(sr*sp*cy-cr*sy,sr*sp*sy+cr*cy,-sr*cp);
        z=new(-(cr*sp*cy+sr*sy),cy*sr-cr*sp*sy,cr*cp);
    }
    // Perspective projection onto a screen of the given center (horizontal field of view in degrees).
    public static Vector2 WorldToScreen(Vector3 point,Vector3 camera,Vector3 rotation,float fov,float cx,float cy)
    {
        Axes(rotation,out var ax,out var ay,out var az);
        var v=point-camera;float z=Vector3.Dot(v,ax);if(z<1)z=1;
        float focal=cx/MathF.Tan(fov*MathF.PI/360);
        return new(cx+Vector3.Dot(v,ay)*focal/z,cy-Vector3.Dot(v,az)*focal/z);
    }
}

internal sealed class BodyGeometry
{
    public string Name{get;set;}
    public string Bone{get;set;}
    public ShapeGeometry[] Shapes{get;set;}
}
internal sealed class Vec
{
    public float X{get;set;}
    public float Y{get;set;}
    public float Z{get;set;}
    public Vector3 Value=>new(X,Y,Z);
}
internal sealed class Rot
{
    public float Pitch{get;set;}
    public float Yaw{get;set;}
    public float Roll{get;set;}
    // Unreal FRotator::Quaternion, including UE's pitch/roll sign conventions.
    public Quaternion Value
    {
        get
        {
            var p=Pitch*MathF.PI/360;var y=Yaw*MathF.PI/360;var r=Roll*MathF.PI/360;
            var sp=MathF.Sin(p);var cp=MathF.Cos(p);var sy=MathF.Sin(y);var cy=MathF.Cos(y);var sr=MathF.Sin(r);var cr=MathF.Cos(r);
            return new(cr*sp*sy-sr*cp*cy,-cr*sp*cy-sr*cp*sy,cr*cp*sy-sr*sp*cy,cr*cp*cy+sr*sp*sy);
        }
    }
}
internal sealed class Quat
{
    public float X{get;set;}
    public float Y{get;set;}
    public float Z{get;set;}
    public float W{get;set;}=1;
    public Quaternion Value=>new(X,Y,Z,W);
}
internal sealed class ShapeTransform
{
    public Quat Rotation{get;set;}=new();
    public Vec Translation{get;set;}=new();
    public Vec Scale3D{get;set;}=new(){X=1,Y=1,Z=1};
    public Matrix4x4 Matrix=>Matrix4x4.CreateScale(Scale3D.Value)*Matrix4x4.CreateFromQuaternion(Rotation.Value)*Matrix4x4.CreateTranslation(Translation.Value);
}
internal sealed class ShapeGeometry
{
    public string Kind{get;set;}
    public Vec Center{get;set;}=new();
    public Rot Rotation{get;set;}=new();
    public float Radius{get;set;}
    public float Length{get;set;}
    public float X{get;set;}
    public float Y{get;set;}
    public float Z{get;set;}
    public bool HasQuery{get;set;}=true;   // the shape answers traces (QueryOnly / QueryAndPhysics)
    public Vec[] VertexData{get;set;}=[];
    public int[] IndexData{get;set;}=[];
    public ShapeTransform Transform{get;set;}=new();
    // Shapes never change once read, so the derived values are computed on first use and kept.
    sealed record Derived(Matrix4x4 Local,Vector3 Center);
    Derived derived;
    Derived D=>derived??=new(Kind=="Convex"?Transform.Matrix:Matrix4x4.CreateFromQuaternion(Rotation.Value)*Matrix4x4.CreateTranslation(Center.Value),
        Kind=="Convex"&&VertexData.Length>0?VertexData.Aggregate(Vector3.Zero,(sum,v)=>sum+v.Value)/VertexData.Length:Vector3.Zero);
    public Matrix4x4 LocalMatrix=>D.Local;
    public Vector3 LocalCenter=>D.Center;

    public IEnumerable<Vector3[]> Lines(int steps=16)
    {
        if(Kind=="Convex")
        {
            var edges=new HashSet<(int,int)>();
            for(int i=0;i+2<IndexData.Length;i+=3)for(int j=0;j<3;j++)
            {
                int a=IndexData[i+j],b=IndexData[i+(j+1)%3];if(a>b)(a,b)=(b,a);
                if(a>=0&&b<VertexData.Length&&edges.Add((a,b)))yield return [VertexData[a].Value,VertexData[b].Value];
            }
            yield break;
        }
        if(Kind=="Box")
        {
            var v=new Vector3[8];
            for(int i=0;i<8;i++)v[i]=new((i&1)==0?-X/2:X/2,(i&2)==0?-Y/2:Y/2,(i&4)==0?-Z/2:Z/2);
            for(int i=0;i<8;i++)for(int b=1;b<=4;b*=2)if((i&b)==0)yield return [v[i],v[i|b]];
            yield break;
        }
        float half=Kind=="Sphyl"?Length/2:0;
        for(int axis=0;axis<3;axis++)
        {
            var ring=new Vector3[steps+1];
            for(int i=0;i<=steps;i++)
            {
                float a=i*2*MathF.PI/steps,u=Radius*MathF.Cos(a),v=Radius*MathF.Sin(a);
                ring[i]=axis switch{0=>new(0,u,v+MathF.Sign(v)*half),1=>new(u,0,v+MathF.Sign(v)*half),_=>new(u,v,half)};
            }
            yield return ring;
            if(axis==2&&half>0)yield return ring.Select(v=>v-new Vector3(0,0,2*half)).ToArray();
        }
    }

    // Segment parameter t, preserved under inverse affine transforms; exact spheres,
    // oriented boxes and UE capsules (Length is the cylindrical part, local Z axis).
    public float? Intersect(Vector3 from,Vector3 to,Matrix4x4 world)
    {
        if(!Matrix4x4.Invert(world,out var inv))return null;
        return IntersectLocal(Vector3.Transform(from,inv),Vector3.Transform(to,inv));
    }
    // Same, for a segment already in the shape's local space.
    public float? IntersectLocal(Vector3 a,Vector3 b)
    {
        var d=b-a;
        if(Kind=="Convex")
        {
            if(IndexData.Length<12||VertexData.Length<4)return null;
            float lo=0,hi=1;var center=LocalCenter;
            for(int i=0;i+2<IndexData.Length;i+=3)
            {
                var p=VertexData[IndexData[i]].Value;var q=VertexData[IndexData[i+1]].Value;var r=VertexData[IndexData[i+2]].Value;
                var n=Vector3.Cross(q-p,r-p);float len=n.Length();if(len<1e-7)continue;n/=len;
                if(Vector3.Dot(n,center-p)>0)n=-n;
                float dist=Vector3.Dot(n,a-p),den=Vector3.Dot(n,d);
                if(MathF.Abs(den)<1e-7){if(dist>1e-4)return null;continue;}
                float t=-dist/den;
                if(den<0)lo=MathF.Max(lo,t);else hi=MathF.Min(hi,t);
                if(lo>hi+1e-5)return null;
            }
            return lo;
        }
        if(Kind=="Box")
        {
            float lo=0,hi=1;var ext=new Vector3(X,Y,Z)/2;
            for(int i=0;i<3;i++)
            {
                if(MathF.Abs(d[i])<1e-7){if(MathF.Abs(a[i])>ext[i])return null;continue;}
                float u=(-ext[i]-a[i])/d[i],v=(ext[i]-a[i])/d[i];
                lo=MathF.Max(lo,MathF.Min(u,v));hi=MathF.Min(hi,MathF.Max(u,v));
                if(lo>hi)return null;
            }
            return lo;
        }
        float half=Kind=="Sphyl"?Length/2:0;
        if(Vector3.DistanceSquared(a,new(0,0,Math.Clamp(a.Z,-half,half)))<=Radius*Radius)return 0;
        float best=float.PositiveInfinity;
        void Sphere(Vector3 c)
        {
            var m=a-c;float qa=Vector3.Dot(d,d),qb=Vector3.Dot(m,d),qc=Vector3.Dot(m,m)-Radius*Radius;
            float det=qb*qb-qa*qc;
            if(qa<=0||det<0)return;
            float t=(-qb-MathF.Sqrt(det))/qa;
            if(t>=0&&t<=1)best=MathF.Min(best,t);
        }
        Sphere(new(0,0,half));Sphere(new(0,0,-half));
        if(half>0)
        {
            float qa=d.X*d.X+d.Y*d.Y,qb=a.X*d.X+a.Y*d.Y,qc=a.X*a.X+a.Y*a.Y-Radius*Radius,det=qb*qb-qa*qc;
            if(qa>0&&det>=0)
            {
                float t=(-qb-MathF.Sqrt(det))/qa,z=a.Z+t*d.Z;
                if(t>=0&&t<=1&&MathF.Abs(z)<=half)best=MathF.Min(best,t);
            }
        }
        return float.IsFinite(best)?best:null;
    }
}

internal sealed record HitLocation(ulong Id,string Name,nint Material,float Scalar,bool WeakFlag,bool ResistFlag,bool Destroyed,bool InitiallyEnabled,ulong[] Dependencies)
{
    public bool IsResisted(float threshold)=>ResistFlag||threshold>=0&&Scalar<=threshold;
    public bool IsWeak(float weakThreshold,float resistThreshold)=>Material!=0&&!IsResisted(resistThreshold)&&(WeakFlag||weakThreshold>=0&&Scalar>=weakThreshold);
}
internal static class ProtectionResolver
{
    // Lists on a location are prerequisites, not children to activate.
    public static HashSet<ulong> Frontier(HitLocation weak,IReadOnlyDictionary<ulong,HitLocation> all)
    {
        var result=new HashSet<ulong>();var visited=new HashSet<ulong>();
        void Visit(HitLocation h)
        {
            if(!visited.Add(h.Id)||h.Destroyed)return;
            var unmet=h.Dependencies.Where(id=>all.TryGetValue(id,out var p)&&!p.Destroyed).ToArray();
            if(unmet.Length==0){result.Add(h.Id);return;}
            foreach(var id in unmet)Visit(all[id]);
        }
        Visit(weak);return result;
    }
}

// Collision data read straight from the game's loaded assets (skeletons, physics assets, body setups).
// Assets are immutable cooked data, so callers cache the results.
internal static class LiveGeometry
{
    // Reference skeleton of a USkeletalMesh: bone name -> index.
    public static Dictionary<string,int> Bones(nint meshAsset)
    {
        // USkeletalMesh::GetRefSkeleton (vtable +0x328) returns this+0x2E0 in this build.
        if(Mem.Peek<nint>(Mem.Peek<nint>(meshAsset)+0x328)-Mem.Base!=0x497c1a0)
            throw new InvalidDataException("Unrecognized mesh reference-skeleton getter.");
        var array=meshAsset+0x300;int count=Mem.Read<int>(array+8);var data=Mem.Ptr(array);
        if(count<=0||count>8192||data==0)throw new InvalidDataException("Invalid reference skeleton.");
        var raw=Mem.Bytes(data,count*12);
        var result=new Dictionary<string,int>();
        for(int i=0;i<count;i++)result.TryAdd(UE.Name(BitConverter.ToUInt64(raw,i*12)),i);
        return result;
    }

    static IEnumerable<UObj> Elements(UObj owner,string field)
    {
        var prop=UE.Field(owner.Type,field);if(prop==null)yield break;
        var data=Mem.Peek<nint>(owner.Address+prop.Offset);int count=Mem.Peek<int>(owner.Address+prop.Offset+8);
        if(count<0||count>65536||prop.InnerSize<=0||prop.InnerSize>65536||count>0&&data==0)
            throw new InvalidDataException("Invalid geometry array "+field);
        for(int i=0;i<count;i++)yield return new UObj(data+i*prop.InnerSize,prop.InnerStruct);
    }
    static Vec Vector(nint address)
    {
        var p=Mem.Read<FVector>(address);
        return new(){X=(float)p.X,Y=(float)p.Y,Z=(float)p.Z};
    }
    // Simple collision of a UBodySetup (its KAggregateGeom).
    public static ShapeGeometry[] Shapes(nint bodySetup)
    {
        var aggregate=UObj.Of(bodySetup).Struct("AggGeom");var result=new List<ShapeGeometry>();
        foreach(var (field,kind) in new[]{("SphereElems","Sphere"),("SphylElems","Sphyl"),("BoxElems","Box"),("ConvexElems","Convex")})
        foreach(var e in Elements(aggregate,field))
        {
            var s=new ShapeGeometry{Kind=kind};
            // ECollisionEnabled: only QueryOnly (1) and QueryAndPhysics (3) answer hit traces.
            if(UE.Field(e.Type,"CollisionEnabled") is {} ce)s.HasQuery=Mem.Peek<byte>(e.Address+ce.Offset) is 1 or 3;
            if(kind!="Convex")
            {
                s.Center=Vector(e.At("Center"));
                if(kind!="Sphere")
                {var rot=Mem.Read<FVector>(e.At("Rotation"));s.Rotation=new(){Pitch=(float)rot.X,Yaw=(float)rot.Y,Roll=(float)rot.Z};}
                if(kind is "Sphere" or "Sphyl")s.Radius=e.Get<float>("Radius");
                if(kind=="Sphyl")s.Length=e.Get<float>("Length");
                if(kind=="Box"){s.X=e.Get<float>("X");s.Y=e.Get<float>("Y");s.Z=e.Get<float>("Z");}
            }
            else
            {
                var (vdata,vn)=e.Array("VertexData");
                if(vn<0||vn>65536)throw new InvalidDataException("Invalid convex vertices.");
                var vraw=Mem.Bytes(vdata,vn*24);
                s.VertexData=Enumerable.Range(0,vn).Select(i=>new Vec{X=(float)BitConverter.ToDouble(vraw,i*24),Y=(float)BitConverter.ToDouble(vraw,i*24+8),Z=(float)BitConverter.ToDouble(vraw,i*24+16)}).ToArray();
                var (idata,inum)=e.Array("IndexData");
                if(inum<0||inum>200000)throw new InvalidDataException("Invalid convex indices.");
                var iraw=Mem.Bytes(idata,inum*4);
                s.IndexData=Enumerable.Range(0,inum).Select(i=>BitConverter.ToInt32(iraw,i*4)).ToArray();
                var t=Mem.Read<FTransform>(e.At("Transform"));
                s.Transform=new(){Rotation=new(){X=(float)t.QX,Y=(float)t.QY,Z=(float)t.QZ,W=(float)t.QW},
                    Translation=new(){X=(float)t.TX,Y=(float)t.TY,Z=(float)t.TZ},Scale3D=new(){X=(float)t.SX,Y=(float)t.SY,Z=(float)t.SZ}};
            }
            result.Add(s);
        }
        return result.ToArray();
    }
}
