using System.Collections.Concurrent;
using System.Text;

// Unreal Engine 5 object model of the supported Remnant 2 build: name pool, object table and runtime
// reflection (class properties and functions are looked up by name, so no generated SDK is needed).
internal static class UE
{
    // Engine globals (RVAs in Remnant2-Win64-Shipping.exe; the build is verified by hash at startup).
    public const int NamePoolRva=0x7925a40,ObjectArrayRva=0x79cbad0,WorldRva=0x7b21ca0,EngineRva=0x7b1e7d0,CollisionProfileClassRva=0x7aee2b8;
    // UObject.
    public const int ObjIndex=0xC,ObjClass=0x10,ObjName=0x18,ObjOuter=0x20;
    // UField / UStruct / UClass.
    const int FieldNext=0x28,StructSuper=0x40,StructChildren=0x48,StructChildProperties=0x50;
    public const int ClassDefaultObject=0x110;
    // FField / FProperty.
    const int FFieldClass=0x8,FFieldNext=0x20,FFieldName=0x28;
    public const int PropElementSize=0x3C,PropFlags=0x40,PropOffset=0x4C,PropExtra=0x78;
    // FBoolProperty: ByteOffset / FieldMask after the FProperty base.
    const int BoolByteOffset=0x79,BoolFieldMask=0x7B;
    public const int ProcessEventSlot=77;
    public const int LevelActors=0x98;

    public static nint World=>Mem.Peek<nint>(Mem.Base+WorldRva);

    public static void Initialize()
    {
        if(Name(3)!="ByteProperty")throw new InvalidDataException("Name table failed validation.");
        var global=Mem.Base+ObjectArrayRva;
        var chunks=Mem.Peek<nint>(global);int count=Mem.Peek<int>(global+20);
        if(chunks==0||count<=0||count>10_000_000)throw new InvalidDataException("Object table failed validation.");
    }

    // ---------------------------------------------------------------- names

    static readonly ConcurrentDictionary<uint,string> names=new();
    // FName: pool entry index (low 32 bits) and instance number (high 32 bits).
    public static string Name(ulong key)
    {
        uint index=(uint)key,number=(uint)(key>>32);
        if(!names.TryGetValue(index,out var name))
        {
            var block=Mem.Peek<nint>(Mem.Base+NamePoolRva+(nint)((index>>16)+2)*8);
            if(block==0)return $"<{key:X}>";
            var entry=block+(nint)(index&0xffff)*2;
            var header=Mem.Peek<ushort>(entry);int length=header>>6;
            if(length==0||length>1024)return $"<{key:X}>";
            bool wide=(header&1)!=0;
            var raw=new byte[length*(wide?2:1)];
            if(!Mem.TryRead(entry+2,raw))return $"<{key:X}>";
            name=(wide?Encoding.Unicode:Encoding.UTF8).GetString(raw);
            names[index]=name;
        }
        return number>0?name+"_"+(number-1):name;
    }
    public static string ObjectName(nint obj)=>obj==0?"None":Name(Mem.Peek<ulong>(obj+ObjName));
    public static string ObjectPath(nint obj)
    {
        var parts=new List<string>();
        for(int depth=0;obj!=0&&depth<32;depth++){parts.Add(ObjectName(obj));obj=Mem.Peek<nint>(obj+ObjOuter);}
        parts.Reverse();return string.Join('.',parts);
    }
    public static nint ClassOf(nint obj)=>obj==0?0:Mem.Peek<nint>(obj+ObjClass);
    public static nint OuterOf(nint obj)=>obj==0?0:Mem.Peek<nint>(obj+ObjOuter);
    public static string ClassName(nint obj)=>ObjectName(ClassOf(obj));

    // ---------------------------------------------------------------- type checks

    static readonly ConcurrentDictionary<(nint Class,ulong Name,string Target),bool> typeCache=new();
    // True when the object's class is (or derives from) the named class.
    public static bool IsType(nint obj,string target)
    {
        if(obj==0)return false;
        return ClassIs(Mem.Peek<nint>(obj+ObjClass),target);
    }
    public static bool ClassIs(nint cls,string target)
    {
        if(cls==0)return false;
        var key=(cls,Mem.Peek<ulong>(cls+ObjName),target);
        if(typeCache.TryGetValue(key,out bool result))return result;
        result=false;
        for(int depth=0;cls!=0&&depth<64;depth++)
        {
            if(Name(Mem.Peek<uint>(cls+ObjName))==target){result=true;break;}
            cls=Mem.Peek<nint>(cls+StructSuper);
        }
        typeCache[key]=result;
        return result;
    }

    // ---------------------------------------------------------------- reflection

    // A reflected property. Struct: the struct type (struct properties) or class (object properties).
    // Inner*: the element of an array property.
    public sealed record Prop(nint Address,int Offset,int Size,string Type,nint Struct,int InnerSize,nint InnerStruct);
    static readonly ConcurrentDictionary<(nint Type,string Name),Prop> props=new();
    static readonly ConcurrentDictionary<(nint Class,string Name),nint> functions=new();

    // Property of a class or struct type (searched through its super chain); null when there is none.
    public static Prop Field(nint type,string name)
    {
        if(type==0)return null;
        if(props.TryGetValue((type,name),out var hit))return hit;
        Prop found=null;
        for(var t=type;t!=0&&found==null;t=Mem.Peek<nint>(t+StructSuper))
            for(var p=Mem.Peek<nint>(t+StructChildProperties);p!=0;p=Mem.Peek<nint>(p+FFieldNext))
            {
                if(Name(Mem.Peek<uint>(p+FFieldName))!=name)continue;
                string kind=Name(Mem.Peek<uint>(Mem.Peek<nint>(p+FFieldClass)));
                var extra=Mem.Peek<nint>(p+PropExtra);
                int innerSize=0;nint innerStruct=0;
                if(kind=="ArrayProperty"&&extra!=0){innerSize=Mem.Peek<int>(extra+PropElementSize);innerStruct=Mem.Peek<nint>(extra+PropExtra);}
                found=new(p,Mem.Peek<int>(p+PropOffset),Mem.Peek<int>(p+PropElementSize),kind,kind is "StructProperty" or "ObjectProperty"?extra:0,innerSize,innerStruct);
                break;
            }
        props[(type,name)]=found;
        return found;
    }
    public static int Offset(nint obj,string name)=>Field(ClassOf(obj),name)?.Offset??-1;

    // UFunction of a class (searched through its super chain); 0 when there is none.
    public static nint Function(nint cls,string name)
    {
        if(functions.TryGetValue((cls,name),out var hit))return hit;
        nint found=0;
        for(var c=cls;c!=0&&found==0;c=Mem.Peek<nint>(c+StructSuper))
            for(var f=Mem.Peek<nint>(c+StructChildren);f!=0;f=Mem.Peek<nint>(f+FieldNext))
                if(Name(Mem.Peek<uint>(f+ObjName))==name){found=f;break;}
        return functions[(cls,name)]=found;
    }
    // Parameters of a function: name -> (offset, size, flags).
    public static Dictionary<string,(int Offset,int Size,ulong Flags)> Parameters(nint function)
    {
        var result=new Dictionary<string,(int,int,ulong)>();
        for(var p=Mem.Peek<nint>(function+StructChildProperties);p!=0;p=Mem.Peek<nint>(p+FFieldNext))
            result[Name(Mem.Peek<uint>(p+FFieldName))]=(Mem.Peek<int>(p+PropOffset),Mem.Peek<int>(p+PropElementSize),Mem.Peek<ulong>(p+PropFlags));
        return result;
    }

    // Reflected bool property: byte offset and bit mask.
    public static bool? Bool(nint obj,string name)
    {
        var p=Field(ClassOf(obj),name);if(p==null)return null;
        byte mask=Mem.Peek<byte>(p.Address+BoolFieldMask);if(mask==0)return null;
        return (Mem.Peek<byte>(obj+p.Offset+Mem.Peek<byte>(p.Address+BoolByteOffset))&mask)!=0;
    }

    // Reflection results are keyed by type address; types can be unloaded with a level, so start over
    // whenever the world or its level list changes.
    static nint snapshotWorld,snapshotLevels;
    // Returns true when the world changed (callers drop their own address-keyed caches too).
    public static bool BeginSnapshot(nint world,nint levels)
    {
        if(world==snapshotWorld&&levels==snapshotLevels)return false;
        snapshotWorld=world;snapshotLevels=levels;
        props.Clear();functions.Clear();typeCache.Clear();
        return true;
    }

    // ---------------------------------------------------------------- object table

    // FUObjectItem for an object index; 0 when out of range.
    static nint Item(int index)
    {
        var global=Mem.Base+ObjectArrayRva;
        if(index<0||index>=Mem.Peek<int>(global+20))return 0;
        var chunk=Mem.Peek<nint>(Mem.Peek<nint>(global)+(index>>16)*8);
        return chunk==0?0:chunk+(index&0xffff)*24;
    }
    const uint Unreachable=0x30200000; // PendingKill | Garbage | Unreachable
    // FWeakObjectPtr encoding (index | serial << 32) of a live object; 0 when it has none.
    public static ulong WeakKey(nint obj)
    {
        if(obj==0)return 0;
        int index=Mem.Peek<int>(obj+ObjIndex);var item=Item(index);
        if(item==0||Mem.Peek<nint>(item)!=obj||(Mem.Peek<uint>(item+8)&Unreachable)!=0)return 0;
        uint serial=Mem.Peek<uint>(item+16);return serial==0?0:((ulong)serial<<32)|(uint)index;
    }
    public static nint ResolveWeakKey(ulong key)
    {
        int index=unchecked((int)key);uint serial=(uint)(key>>32);
        if(serial==0)return 0;
        var item=Item(index);
        return item!=0&&Mem.Peek<uint>(item+16)==serial&&(Mem.Peek<uint>(item+8)&Unreachable)==0?Mem.Peek<nint>(item):0;
    }
    public static nint WeakObject(nint address)=>ResolveWeakKey(Mem.Read<ulong>(address));
    // Object-table slot key for objects that may not have a serial number yet (fresh NewObject results).
    public static ulong SlotKey(nint obj)=>obj==0?0:((ulong)(uint)Mem.Peek<int>(obj+ObjIndex)<<32)|1;
    public static bool LiveInSlot(nint obj,ulong slotKey)
    {
        if(obj==0||slotKey==0)return false;
        int index=(int)(slotKey>>32);var item=Item(index);
        return item!=0&&Mem.Peek<nint>(item)==obj&&(Mem.Peek<uint>(item+8)&Unreachable)==0&&Mem.Peek<int>(obj+ObjIndex)==index;
    }

    // Objects of an exact class name whose path matches (full object table scan).
    public static Dictionary<string,nint> FindObjects(string className,Func<string,bool> pathFilter,int max=16)
    {
        var result=new Dictionary<string,nint>();
        var global=Mem.Base+ObjectArrayRva;
        var chunks=Mem.Ptr(global);int count=Mem.Read<int>(global+20);
        var classNames=new Dictionary<nint,string>();
        for(int c=0;c*65536<count&&result.Count<max;c++)
        {
            var chunk=Mem.Ptr(chunks+c*8);if(chunk==0)continue;
            int n=Math.Min(65536,count-c*65536);var items=Mem.Bytes(chunk,n*24);
            for(int i=0;i<n&&result.Count<max;i++)
            {
                var obj=(nint)BitConverter.ToInt64(items,i*24);if(obj==0)continue;
                var cls=Mem.Peek<nint>(obj+ObjClass);if(cls==0)continue;
                if(!classNames.TryGetValue(cls,out var cn))classNames[cls]=cn=ObjectName(cls);
                if(cn!=className)continue;
                var path=ObjectPath(obj);
                if(pathFilter(path))result[path]=obj;
            }
        }
        return result;
    }
}

// A UObject (Type = its class) or an inline struct (Type = its struct) with fields accessed by name.
internal readonly record struct UObj(nint Address,nint Type)
{
    public static UObj Of(nint obj)=>new(obj,UE.ClassOf(obj));
    public bool IsNull=>Address==0;
    public UE.Prop Prop(string name)
    {
        if(Address==0)throw new NullReferenceException($"Reading {name} of a null object.");
        return UE.Field(Type,name)??throw new MissingFieldException(UE.ObjectName(Type),name);
    }
    public nint At(string name)=>Address+Prop(name).Offset;
    public UObj Obj(string name)=>Of(Mem.Peek<nint>(At(name)));
    public nint Ptr(string name)=>Mem.Peek<nint>(At(name));
    public UObj Struct(string name){var p=Prop(name);return new(Address+p.Offset,p.Struct);}
    // An object or inline struct field, whichever the property is.
    public UObj Ref(string name)=>Prop(name).Type=="StructProperty"?Struct(name):Obj(name);
    public T Get<T>(string name) where T:unmanaged=>Mem.Peek<T>(At(name));
    public bool Set<T>(string name,T value) where T:unmanaged=>Mem.Write(At(name),value);
    // TArray header of an array property.
    public (nint Data,int Count) Array(string name){var a=At(name);return (Mem.Peek<nint>(a),Mem.Peek<int>(a+8));}
    // Inline struct element of an array property.
    public UObj Element(string name,int index)
    {
        var p=Prop(name);var data=Mem.Peek<nint>(Address+p.Offset);
        return new(data+(nint)index*p.InnerSize,p.InnerStruct);
    }
}
