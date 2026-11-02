using System.Numerics;
using System.Runtime.InteropServices;

// Minimal Direct2D + DirectWrite renderer over raw COM vtables (both ship with Windows).
// Draws into a transparent, DWM-composited window: everything not drawn stays see-through.
// Colors are 0xAABBGGRR (alpha in the high byte, red in the low byte).
internal sealed unsafe class Direct2D : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct ColorF{public float R,G,B,A;}
    [StructLayout(LayoutKind.Sequential)] struct PointF{public float X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct RectF{public float L,T,R,B;}
    [StructLayout(LayoutKind.Sequential)] struct RoundedRect{public RectF Rect;public float RadiusX,RadiusY;}
    [StructLayout(LayoutKind.Sequential)] struct Ellipse{public PointF Center;public float RadiusX,RadiusY;}
    [StructLayout(LayoutKind.Sequential)] struct RenderTargetProps{public int Type,Format,AlphaMode;public float DpiX,DpiY;public int Usage,MinLevel;}
    [StructLayout(LayoutKind.Sequential)] struct HwndTargetProps{public nint Hwnd;public uint Width,Height;public int PresentOptions;}
    [StructLayout(LayoutKind.Sequential)] struct TextMetrics{public float Left,Top,Width,WidthIncludingTrailingWhitespace,Height,LayoutWidth,LayoutHeight;public uint MaxBidiDepth,LineCount;}

    [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type,Guid* riid,nint options,nint* factory);
    [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type,Guid* riid,nint* factory);

    const int D2DERR_RECREATE_TARGET=unchecked((int)0x8899000C);
    nint factory,dwrite,target,brush,font,fontWrap;
    readonly nint hwnd;
    uint width,height;
    float fontSize;
    readonly Dictionary<string,float> widths=new();
    public float LineHeight{get;private set;}

    static nint* V(nint com)=>*(nint**)com;
    static void Check(int hr,string what){if(hr<0)throw new COMException(what,hr);}
    static void Release(ref nint com){if(com!=0){((delegate* unmanaged<nint,uint>)V(com)[2])(com);com=0;}}

    public Direct2D(nint hwnd,uint width,uint height,float fontSize)
    {
        this.hwnd=hwnd;this.width=Math.Max(1,width);this.height=Math.Max(1,height);
        var d2dIid=new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        var dwIid=new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        nint f,w;
        Check(D2D1CreateFactory(0/*single threaded*/,&d2dIid,0,&f),"D2D1CreateFactory");factory=f;
        Check(DWriteCreateFactory(0/*shared*/,&dwIid,&w),"DWriteCreateFactory");dwrite=w;
        SetFontSize(fontSize);
        CreateTarget();
    }

    void CreateTarget()
    {
        var rt=new RenderTargetProps{Format=87/*B8G8R8A8_UNORM*/,AlphaMode=1/*premultiplied*/,DpiX=96,DpiY=96};
        var ht=new HwndTargetProps{Hwnd=hwnd,Width=width,Height=height,PresentOptions=2/*immediately: no vsync wait*/};
        nint t;
        Check(((delegate* unmanaged<nint,RenderTargetProps*,HwndTargetProps*,nint*,int>)V(factory)[14])(factory,&rt,&ht,&t),"CreateHwndRenderTarget");
        target=t;
        var white=new ColorF{R=1,G=1,B=1,A=1};nint b;
        Check(((delegate* unmanaged<nint,ColorF*,nint,nint*,int>)V(target)[8])(target,&white,0,&b),"CreateSolidColorBrush");
        brush=b;
        ((delegate* unmanaged<nint,int,void>)V(target)[34])(target,2/*grayscale: ClearType needs an opaque background*/);
    }

    public void SetFontSize(float size)
    {
        if(size==fontSize&&font!=0)return;
        Release(ref font);Release(ref fontWrap);widths.Clear();
        fontSize=size;
        font=CreateFormat(size,true);fontWrap=CreateFormat(size,false);
        LineHeight=MathF.Ceiling(Measure("Ag",out float h)>0?h:size*1.3f);
    }
    nint CreateFormat(float size,bool noWrap)
    {
        nint format;
        fixed(char* family="Segoe UI",locale="en-us")
            Check(((delegate* unmanaged<nint,char*,nint,int,int,int,float,char*,nint*,int>)V(dwrite)[15])(dwrite,family,0,400,0,5,size,locale,&format),"CreateTextFormat");
        if(noWrap)((delegate* unmanaged<nint,int,int>)V(format)[5])(format,1/*DWRITE_WORD_WRAPPING_NO_WRAP*/);
        return format;
    }

    public void Resize(uint w,uint h)
    {
        w=Math.Max(1,w);h=Math.Max(1,h);
        if(w==width&&h==height)return;
        width=w;height=h;
        var size=stackalloc uint[2];size[0]=w;size[1]=h;
        if(target!=0)((delegate* unmanaged<nint,uint*,int>)V(target)[58])(target,size);
    }

    public void Begin()
    {
        ((delegate* unmanaged<nint,void>)V(target)[48])(target);
        var clear=new ColorF();
        ((delegate* unmanaged<nint,ColorF*,void>)V(target)[47])(target,&clear);
    }
    public void End()
    {
        ulong tag1,tag2;
        int hr=((delegate* unmanaged<nint,ulong*,ulong*,int>)V(target)[49])(target,&tag1,&tag2);
        if(hr==D2DERR_RECREATE_TARGET){Release(ref brush);Release(ref target);CreateTarget();}
    }

    void Color(uint c,float alpha=1)
    {
        var f=new ColorF{R=(c&0xff)/255f,G=((c>>8)&0xff)/255f,B=((c>>16)&0xff)/255f,A=(c>>24)/255f*alpha};
        ((delegate* unmanaged<nint,ColorF*,void>)V(brush)[8])(brush,&f);
    }

    public void Line(Vector2 a,Vector2 b,uint color,float thickness=1)
    {
        Color(color);
        ((delegate* unmanaged<nint,PointF,PointF,nint,float,nint,void>)V(target)[15])(target,new PointF{X=a.X,Y=a.Y},new PointF{X=b.X,Y=b.Y},brush,thickness,0);
    }
    public void Rect(Vector2 min,Vector2 max,uint color,float rounding=0,float thickness=1)
    {
        Color(color);
        var r=new RoundedRect{Rect=new RectF{L=min.X,T=min.Y,R=max.X,B=max.Y},RadiusX=rounding,RadiusY=rounding};
        ((delegate* unmanaged<nint,RoundedRect*,nint,float,nint,void>)V(target)[18])(target,&r,brush,thickness,0);
    }
    public void FillRect(Vector2 min,Vector2 max,uint color,float rounding=0)
    {
        Color(color);
        if(rounding<=0)
        {
            var r=new RectF{L=min.X,T=min.Y,R=max.X,B=max.Y};
            ((delegate* unmanaged<nint,RectF*,nint,void>)V(target)[17])(target,&r,brush);
            return;
        }
        var rr=new RoundedRect{Rect=new RectF{L=min.X,T=min.Y,R=max.X,B=max.Y},RadiusX=rounding,RadiusY=rounding};
        ((delegate* unmanaged<nint,RoundedRect*,nint,void>)V(target)[19])(target,&rr,brush);
    }
    public void Circle(Vector2 center,float radius,uint color,float thickness=1)
    {
        Color(color);
        var e=new Ellipse{Center=new PointF{X=center.X,Y=center.Y},RadiusX=radius,RadiusY=radius};
        ((delegate* unmanaged<nint,Ellipse*,nint,float,nint,void>)V(target)[20])(target,&e,brush,thickness,0);
    }
    public void FillCircle(Vector2 center,float radius,uint color)
    {
        Color(color);
        var e=new Ellipse{Center=new PointF{X=center.X,Y=center.Y},RadiusX=radius,RadiusY=radius};
        ((delegate* unmanaged<nint,Ellipse*,nint,void>)V(target)[21])(target,&e,brush);
    }

    // Single-line text with its top-left corner at p.
    public void Text(Vector2 p,uint color,string text,float alpha=1)
    {
        if(string.IsNullOrEmpty(text))return;
        Color(color,alpha);
        var r=new RectF{L=p.X,T=p.Y,R=p.X+100000,B=p.Y+LineHeight*2};
        fixed(char* s=text)
            ((delegate* unmanaged<nint,char*,uint,nint,RectF*,nint,int,int,void>)V(target)[27])(target,s,(uint)text.Length,font,&r,brush,0,0);
    }
    // Text wrapped to maxWidth.
    public void TextWrapped(Vector2 p,float maxWidth,uint color,string text)
    {
        Color(color);
        var r=new RectF{L=p.X,T=p.Y,R=p.X+maxWidth,B=p.Y+10000};
        fixed(char* s=text)
            ((delegate* unmanaged<nint,char*,uint,nint,RectF*,nint,int,int,void>)V(target)[27])(target,s,(uint)text.Length,fontWrap,&r,brush,0,0);
    }

    public float Measure(string text)=>Measure(text,out _);
    float Measure(string text,out float height)
    {
        height=LineHeight;
        if(string.IsNullOrEmpty(text))return 0;
        lock(widths)if(widths.TryGetValue(text,out float w)&&LineHeight>0)return w;
        var m=Layout(text,font,100000);height=m.Height;
        lock(widths){if(widths.Count>4096)widths.Clear();widths[text]=m.WidthIncludingTrailingWhitespace;}
        return m.WidthIncludingTrailingWhitespace;
    }
    public Vector2 MeasureWrapped(string text,float maxWidth)
    {
        var m=Layout(text,fontWrap,maxWidth);return new(m.Width,m.Height);
    }
    TextMetrics Layout(string text,nint format,float maxWidth)
    {
        nint layout;var m=new TextMetrics();
        fixed(char* s=text)
            Check(((delegate* unmanaged<nint,char*,uint,nint,float,float,nint*,int>)V(dwrite)[18])(dwrite,s,(uint)text.Length,format,maxWidth,100000,&layout),"CreateTextLayout");
        ((delegate* unmanaged<nint,TextMetrics*,int>)V(layout)[60])(layout,&m);
        Release(ref layout);
        return m;
    }

    public void PushClip(Vector2 min,Vector2 max)
    {
        var r=new RectF{L=min.X,T=min.Y,R=max.X,B=max.Y};
        ((delegate* unmanaged<nint,RectF*,int,void>)V(target)[45])(target,&r,1/*aliased*/);
    }
    public void PopClip()=>((delegate* unmanaged<nint,void>)V(target)[46])(target);

    public void Dispose()
    {
        Release(ref brush);Release(ref target);Release(ref font);Release(ref fontWrap);Release(ref dwrite);Release(ref factory);
    }
}
