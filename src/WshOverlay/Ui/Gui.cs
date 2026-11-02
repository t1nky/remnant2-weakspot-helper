using System.Globalization;
using System.Numerics;

// A small immediate-mode UI drawn with Direct2D: one movable, resizable window with tabs, a scrolling
// body and the handful of widgets the menu needs. Widgets return true when the user changed the value.
// Labels may carry a hidden id suffix: "Text##id".
internal sealed class Gui
{
    readonly Direct2D d;
    public static uint Rgba(float r,float g,float b,float a)=>
        (uint)(Math.Clamp(a,0,1)*255+0.5f)<<24|(uint)(Math.Clamp(b,0,1)*255+0.5f)<<16|(uint)(Math.Clamp(g,0,1)*255+0.5f)<<8|(uint)(Math.Clamp(r,0,1)*255+0.5f);
    public static readonly uint TextColor=Rgba(0.92f,0.93f,0.95f,1),Muted=Rgba(0.55f,0.57f,0.6f,1),Accent=Rgba(0.30f,0.78f,0.85f,1),
        Good=Rgba(0.45f,0.85f,0.45f,1),Warn=Rgba(1f,0.65f,0.2f,1);
    static readonly uint WindowBg=Rgba(0.075f,0.08f,0.095f,0.96f),TitleBg=Rgba(0.10f,0.11f,0.13f,1),Frame=Rgba(0.15f,0.16f,0.19f,1),
        FrameHover=Rgba(0.21f,0.23f,0.27f,1),FrameActive=Rgba(0.18f,0.45f,0.50f,1),Border=Rgba(1,1,1,0.08f),Separator=Rgba(1,1,1,0.10f),
        PopupBg=Rgba(0.075f,0.08f,0.095f,0.98f);

    public float Scale=1;
    float Pad=>8*Scale;float Spacing=>6*Scale;float FrameH=>d.LineHeight+8*Scale;float IndentW=>16*Scale;

    // Input.
    Vector2 mouse;bool down,pressed,released;float wheel;
    long now;
    string activeId;          // widget being dragged / pressed
    // Window.
    Vector2 winPos,winSize;bool dragging,resizing;Vector2 dragOffset;
    // Layout.
    float left,right,cursorY,rowY,rowH,itemRight,indent;bool sameLine;float sameLineAt=-1;
    Vector2 clipMin,clipMax;
    Vector2 lastMin,lastMax;string lastId;bool lastHovered;
    float labelColumn;
    int disabled;
    // Scroll body.
    // Scroll offset and how far it can go (measured from the previous frame's content).
    readonly Dictionary<string,float> scroll=new(),scrollMax=new();string scrollKey;float scrollTop,scrollBottom;
    // Tooltip.
    string hoverId,hoverSeenThisFrame;long hoverSince;string tooltip;
    // Combo popup.
    string popupId;string[] popupItems;int popupSelected;Vector2 popupMin,popupMax;int popupOpenedFrame;
    string popupResultId;int popupResult;
    int frame;
    // Two-step confirm buttons and collapsing headers.
    readonly Dictionary<string,long> confirmUntil=new();
    readonly Dictionary<string,bool> headers=new();

    public Gui(Direct2D d){this.d=d;}

    // True while the pointer is over the menu or a drag is in progress: the overlay must take clicks.
    public bool WantsMouse{get;private set;}
    public Vector2 WindowPos=>winPos;public Vector2 WindowSize=>winSize;

    public void BeginFrame(Vector2 mouse,bool mouseDown,float wheel)
    {
        frame++;now=Environment.TickCount64;
        this.mouse=mouse;pressed=mouseDown&&!down;released=!mouseDown&&down;down=mouseDown;this.wheel=wheel;
        hoverSeenThisFrame=null;tooltip=null;
        if(!down&&!released)activeId=null;
    }

    static string Display(string label){int i=label.IndexOf("##",StringComparison.Ordinal);return i<0?label:label[..i];}
    bool Inside(Vector2 min,Vector2 max)=>mouse.X>=min.X&&mouse.Y>=min.Y&&mouse.X<max.X&&mouse.Y<max.Y;
    bool InputBlocked=>disabled>0||popupId!=null&&frame>popupOpenedFrame||dragging||resizing;
    uint Fade(uint c)=>disabled>0?(c&0x00ffffff)|((uint)((c>>24)*0.45f)<<24):c;

    // Hover / press / click for one widget rectangle.
    (bool Hovered,bool Held,bool Clicked) Behavior(string id,Vector2 min,Vector2 max)
    {
        bool hovered=!InputBlocked&&Inside(min,max)&&Inside(clipMin,clipMax)&&(activeId==null||activeId==id);
        if(hovered&&pressed)activeId=id;
        bool held=activeId==id&&down;
        bool clicked=activeId==id&&released&&hovered;
        if(released&&activeId==id)activeId=null;
        lastMin=min;lastMax=max;lastId=id;lastHovered=hovered;
        if(hovered)hoverSeenThisFrame=id;
        return (hovered,held,clicked);
    }

    // ---------------------------------------------------------------- window

    public void BeginWindow(string title,ref Vector2 pos,ref Vector2 size,Vector2 minSize,Vector2 maxSize,Vector2 display)
    {
        // Move / resize from last frame's interaction.
        float titleH=d.LineHeight+14*Scale,grip=14*Scale;
        if(dragging){if(down)pos=mouse-dragOffset;else dragging=false;}
        if(resizing){if(down)size=Vector2.Clamp(mouse-pos+dragOffset,minSize,maxSize);else resizing=false;}
        pos=Vector2.Clamp(pos,Vector2.Zero,Vector2.Max(Vector2.Zero,display-new Vector2(80,titleH)));
        winPos=pos;winSize=size;
        bool popupOpen=popupId!=null&&frame>popupOpenedFrame;
        if(pressed&&!popupOpen&&activeId==null)
        {
            if(Inside(pos,pos+new Vector2(size.X,titleH))){dragging=true;dragOffset=mouse-pos;}
            else if(Inside(pos+size-new Vector2(grip,grip),pos+size)){resizing=true;dragOffset=pos+size-mouse;}
        }
        WantsMouse=Inside(pos,pos+size)||popupId!=null||dragging||resizing||activeId!=null;

        d.FillRect(pos,pos+size,WindowBg,8*Scale);
        d.FillRect(pos,pos+new Vector2(size.X,titleH),TitleBg,8*Scale);
        d.FillRect(pos+new Vector2(0,titleH-8*Scale),pos+new Vector2(size.X,titleH),TitleBg);
        d.Rect(pos,pos+size,Border,8*Scale);
        var tw=d.Measure(title);
        d.Text(pos+new Vector2((size.X-tw)/2,(titleH-d.LineHeight)/2),TextColor,title);
        // Resize grip.
        var g=pos+size;
        for(int i=1;i<=3;i++){float o=i*4*Scale;d.Line(g-new Vector2(o,2*Scale),g-new Vector2(2*Scale,o),Muted,1);}

        float wp=12*Scale;
        left=pos.X+wp;right=pos.X+size.X-wp;cursorY=pos.Y+titleH+wp*0.75f;indent=0;
        clipMin=pos;clipMax=pos+size;
        labelColumn=d.Measure("Re-acquire lost target within")+Pad;
    }
    public void EndWindow()
    {
        // Popups and tooltips draw last, above everything.
        if(popupId!=null)DrawPopup();
        if(hoverSeenThisFrame==null)hoverId=null;
        if(tooltip!=null)
        {
            float maxW=d.LineHeight*18;var size=d.MeasureWrapped(tooltip,maxW);
            var p=mouse+new Vector2(16*Scale,10*Scale);
            d.FillRect(p-new Vector2(Pad,Pad*0.6f),p+size+new Vector2(Pad,Pad*0.6f),PopupBg,5*Scale);
            d.Rect(p-new Vector2(Pad,Pad*0.6f),p+size+new Vector2(Pad,Pad*0.6f),Border,5*Scale);
            d.TextWrapped(p,maxW+1,TextColor,tooltip);
        }
    }

    // Tab row; returns the selected index.
    public int Tabs(string[] names,int selected)
    {
        float x=left,h=FrameH;var y=cursorY;
        for(int i=0;i<names.Length;i++)
        {
            float w=d.Measure(names[i])+2*Pad*1.4f;
            var min=new Vector2(x,y);var max=new Vector2(x+w,y+h);
            var (hov,held,clicked)=Behavior("tab:"+names[i],min,max);
            if(clicked)selected=i;
            d.FillRect(min,max,i==selected?FrameActive:hov?FrameHover:Frame,5*Scale);
            d.Text(new Vector2(x+Pad*1.4f,y+(h-d.LineHeight)/2),TextColor,names[i]);
            x+=w+4*Scale;
        }
        cursorY=y+h;
        d.Line(new Vector2(left,cursorY),new Vector2(right,cursorY),FrameActive,2*Scale);
        cursorY+=Spacing*1.5f;
        return selected;
    }

    // Scrolling body filling the rest of the window.
    public void BeginScroll(string key)
    {
        scrollKey=key;scrollTop=cursorY;scrollBottom=winPos.Y+winSize.Y-12*Scale;
        float s=scroll.GetValueOrDefault(key);
        if(Inside(new Vector2(winPos.X,scrollTop),new Vector2(winPos.X+winSize.X,scrollBottom))&&wheel!=0&&!(popupId!=null))s-=wheel*FrameH*2;
        // Clamp before drawing: clamping only afterwards drew one frame past the end, then snapped back.
        scroll[key]=s=Math.Clamp(s,0,scrollMax.GetValueOrDefault(key,float.MaxValue));
        clipMin=new Vector2(winPos.X,scrollTop);clipMax=new Vector2(winPos.X+winSize.X,scrollBottom);
        d.PushClip(clipMin,clipMax);
        cursorY=scrollTop-s;
        right-=8*Scale; // room for the scrollbar
    }
    public void EndScroll()
    {
        d.PopClip();
        float s=scroll[scrollKey],content=cursorY+s-scrollTop,view=scrollBottom-scrollTop;
        float max=Math.Max(0,content-view);
        scrollMax[scrollKey]=max;
        if(s>max)scroll[scrollKey]=s=max;
        if(max>0)
        {
            float x=winPos.X+winSize.X-9*Scale,barH=Math.Max(20*Scale,view*view/content),barY=scrollTop+(view-barH)*(s/max);
            d.FillRect(new Vector2(x,barY),new Vector2(x+4*Scale,barY+barH),FrameHover,2*Scale);
        }
        clipMin=winPos;clipMax=winPos+winSize;right+=8*Scale;
    }

    // ---------------------------------------------------------------- layout

    Vector2 Place(float w,float h)
    {
        Vector2 p;
        if(sameLine)
        {
            p=new Vector2(sameLineAt>=0?sameLineAt:itemRight+Spacing,rowY);
            rowH=Math.Max(rowH,h);sameLine=false;sameLineAt=-1;
        }
        else{p=new Vector2(left+indent,cursorY);rowY=p.Y;rowH=h;}
        cursorY=rowY+rowH+Spacing;
        itemRight=p.X+w;
        return p;
    }
    public void SameLine(float atX=-1){sameLine=true;sameLineAt=atX;}
    // Next item on the same line, this far from the content's left edge.
    public void SameLineAt(float offset)=>SameLine(left+indent+offset);
    public float ContentRight=>right;
    public float LineHeight=>d.LineHeight;
    public float Measure(string text)=>d.Measure(text);
    public void AddSpacing(){cursorY+=Spacing;}
    public void Indent(){indent+=IndentW;}
    public void Unindent(){indent=Math.Max(0,indent-IndentW);}
    public void BeginDisabled(bool disable){if(disable||disabled>0)disabled++;}
    public void EndDisabled(){if(disabled>0)disabled--;}
    float ItemWidth=>Math.Max(60*Scale,right-(left+indent)-labelColumn);

    // ---------------------------------------------------------------- widgets

    public void Text(string text,uint color=0)
    {
        if(color==0)color=TextColor;
        var p=Place(d.Measure(text),d.LineHeight);
        d.Text(p,Fade(color),text);
        lastMin=p;lastMax=p+new Vector2(d.Measure(text),d.LineHeight);lastId="text:"+text;
        lastHovered=!InputBlocked&&Inside(lastMin,lastMax)&&Inside(clipMin,clipMax);
        if(lastHovered)hoverSeenThisFrame=lastId;
    }
    public void TextDisabled(string text)=>Text(text,Muted);

    public void Tip(string text)
    {
        if(text==null||!lastHovered)return;
        if(hoverId!=lastId){hoverId=lastId;hoverSince=now;}
        else if(now-hoverSince>=450)tooltip=text;
    }

    public bool Checkbox(string label,ref bool value)
    {
        float box=d.LineHeight+2*Scale;var text=Display(label);
        var p=Place(box+Pad*0.75f+d.Measure(text),box);
        var (hov,held,clicked)=Behavior("cb:"+label,p,p+new Vector2(box+Pad*0.75f+d.Measure(text),box));
        if(clicked)value=!value;
        d.FillRect(p,p+new Vector2(box,box),Fade(held?FrameActive:hov?FrameHover:Frame),4*Scale);
        if(value)
        {
            float s=box;uint c=Fade(Accent);
            d.Line(p+new Vector2(s*0.22f,s*0.52f),p+new Vector2(s*0.42f,s*0.72f),c,2*Scale);
            d.Line(p+new Vector2(s*0.42f,s*0.72f),p+new Vector2(s*0.78f,s*0.28f),c,2*Scale);
        }
        d.Text(p+new Vector2(box+Pad*0.75f,(box-d.LineHeight)/2),Fade(TextColor),text);
        return clicked;
    }

    // Checkbox with its hotkey right-aligned and a tooltip.
    public bool Toggle(string label,ref bool value,string hotkey,string tip)
    {
        bool changed=Checkbox(label,ref value);Tip(tip);
        if(hotkey!=null)
        {
            float w=d.Measure(hotkey);
            d.Text(new Vector2(right-w,rowY+(rowH-d.LineHeight)/2),Fade(Muted),hotkey);
        }
        return changed;
    }

    public bool Button(string label)
    {
        var text=Display(label);float w=d.Measure(text)+2*Pad*1.5f;
        var p=Place(w,FrameH);
        var (hov,held,clicked)=Behavior("btn:"+label,p,p+new Vector2(w,FrameH));
        d.FillRect(p,p+new Vector2(w,FrameH),Fade(held?FrameActive:hov?FrameHover:Frame),5*Scale);
        d.Text(p+new Vector2(Pad*1.5f,(FrameH-d.LineHeight)/2),Fade(TextColor),text);
        return clicked;
    }
    // A button that has to be clicked twice within a few seconds.
    public bool ConfirmButton(string label,string confirmLabel)
    {
        bool armed=confirmUntil.TryGetValue(label,out long until)&&now<until;
        if(!Button(armed?confirmLabel+"##"+label:label))return false;
        if(armed){confirmUntil.Remove(label);return true;}
        confirmUntil[label]=now+3000;return false;
    }

    public bool SliderFloat(string label,ref float value,float min,float max,string format)
    {
        var text=Display(label);float w=ItemWidth;
        var p=Place(w+Pad*0.75f+d.Measure(text),FrameH);
        var fmin=p;var fmax=p+new Vector2(w,FrameH);
        var (hov,held,_)=Behavior("sl:"+label,fmin,fmax);
        bool changed=false;
        if(held&&max>min)
        {
            float t=Math.Clamp((mouse.X-fmin.X-4*Scale)/(w-8*Scale),0,1);
            float v=min+t*(max-min);
            if(v!=value){value=v;changed=true;}
        }
        d.FillRect(fmin,fmax,Fade(held?FrameHover:hov?FrameHover:Frame),5*Scale);
        float frac=max>min?Math.Clamp((value-min)/(max-min),0,1):0;
        float gw=Math.Max(10*Scale,FrameH*0.45f),gx=fmin.X+2*Scale+frac*(w-gw-4*Scale);
        d.FillRect(new Vector2(gx,fmin.Y+2*Scale),new Vector2(gx+gw,fmax.Y-2*Scale),Fade(held?Accent:Accent&0xccffffff),4*Scale);
        var shown=Format(format,value);float sw=d.Measure(shown);
        d.Text(new Vector2(fmin.X+(w-sw)/2,fmin.Y+(FrameH-d.LineHeight)/2),Fade(TextColor),shown);
        d.Text(new Vector2(fmax.X+Pad*0.75f,fmin.Y+(FrameH-d.LineHeight)/2),Fade(TextColor),text);
        return changed;
    }
    public bool SliderInt(string label,ref int value,int min,int max,string format)
    {
        float f=value;
        if(!SliderFloat(label,ref f,min,max,format.Replace("%d","%.0f")))return false;
        int v=(int)MathF.Round(f);if(v==value)return false;
        value=v;return true;
    }

    public bool Combo(string label,ref int index,string[] items)
    {
        var id="combo:"+label;bool changed=false;
        if(popupResultId==id){popupResultId=null;if(popupResult!=index){index=popupResult;changed=true;}}
        var text=Display(label);float w=ItemWidth;
        var p=Place(w+Pad*0.75f+d.Measure(text),FrameH);
        var fmin=p;var fmax=p+new Vector2(w,FrameH);
        var (hov,held,clicked)=Behavior(id,fmin,fmax);
        if(clicked&&popupId==null){popupId=id;popupItems=items;popupSelected=index;popupOpenedFrame=frame;popupMin=new Vector2(fmin.X,fmax.Y+2*Scale);popupMax=popupMin+new Vector2(w,items.Length*FrameH+4*Scale);}
        if(popupId==id)popupItems=items;
        d.FillRect(fmin,fmax,Fade(popupId==id||held?FrameActive:hov?FrameHover:Frame),5*Scale);
        var current=index>=0&&index<items.Length?items[index]:"";
        d.PushClip(fmin,new Vector2(fmax.X-FrameH,fmax.Y));
        d.Text(new Vector2(fmin.X+Pad,fmin.Y+(FrameH-d.LineHeight)/2),Fade(TextColor),current);
        d.PopClip();
        // Down chevron.
        var c=new Vector2(fmax.X-FrameH/2,fmin.Y+FrameH/2);float a=FrameH*0.16f;
        d.Line(c+new Vector2(-a,-a*0.5f),c+new Vector2(0,a*0.5f),Fade(TextColor),1.5f*Scale);
        d.Line(c+new Vector2(0,a*0.5f),c+new Vector2(a,-a*0.5f),Fade(TextColor),1.5f*Scale);
        d.Text(new Vector2(fmax.X+Pad*0.75f,fmin.Y+(FrameH-d.LineHeight)/2),Fade(TextColor),text);
        return changed;
    }
    void DrawPopup()
    {
        var items=popupItems;
        d.FillRect(popupMin,popupMax,PopupBg,5*Scale);
        d.Rect(popupMin,popupMax,Border,5*Scale);
        bool interactive=frame>popupOpenedFrame;
        int clickedIndex=-1;
        for(int i=0;i<items.Length;i++)
        {
            var min=popupMin+new Vector2(2*Scale,2*Scale+i*FrameH);var max=new Vector2(popupMax.X-2*Scale,min.Y+FrameH);
            bool hov=interactive&&Inside(min,max);
            if(hov||i==popupSelected)d.FillRect(min,max,hov?FrameHover:Frame,4*Scale);
            d.Text(new Vector2(min.X+Pad,min.Y+(FrameH-d.LineHeight)/2),TextColor,items[i]);
            if(hov&&pressed)clickedIndex=i;
        }
        if(clickedIndex>=0){popupResultId=popupId;popupResult=clickedIndex;popupId=null;activeId="popup";}
        else if(interactive&&pressed&&!Inside(popupMin,popupMax)){popupId=null;activeId="popup";}
    }

    public void SeparatorText(string text)
    {
        cursorY+=Spacing*0.5f;
        var p=Place(right-(left+indent),d.LineHeight);
        float w=d.Measure(text);
        d.Text(p,Fade(TextColor),text);
        float y=p.Y+d.LineHeight/2;
        d.Line(new Vector2(p.X+w+Pad,y),new Vector2(right,y),Separator,1);
    }

    public bool CollapsingHeader(string label)
    {
        bool open=headers.GetValueOrDefault(label);
        var p=Place(right-(left+indent),FrameH);
        var max=new Vector2(right,p.Y+FrameH);
        var (hov,held,clicked)=Behavior("hdr:"+label,p,max);
        if(clicked){open=!open;headers[label]=open;}
        d.FillRect(p,max,Fade(held?FrameActive:hov?FrameHover:Frame),5*Scale);
        var c=p+new Vector2(Pad+4*Scale,FrameH/2);float a=FrameH*0.14f;uint col=Fade(TextColor);
        if(open){d.Line(c+new Vector2(-a,-a*0.5f),c+new Vector2(0,a*0.5f),col,1.5f*Scale);d.Line(c+new Vector2(0,a*0.5f),c+new Vector2(a,-a*0.5f),col,1.5f*Scale);}
        else{d.Line(c+new Vector2(-a*0.5f,-a),c+new Vector2(a*0.5f,0),col,1.5f*Scale);d.Line(c+new Vector2(a*0.5f,0),c+new Vector2(-a*0.5f,a),col,1.5f*Scale);}
        d.Text(p+new Vector2(Pad*2+8*Scale,(FrameH-d.LineHeight)/2),col,Display(label));
        return open;
    }

    // Small colored marker in front of the next item on the same line.
    public void Dot(uint color,bool square=false)
    {
        float h=d.LineHeight,r=h*0.28f;
        var p=Place(2*r+2*Scale,h);var c=p+new Vector2(r+1,h/2);
        if(square)d.FillRect(c-new Vector2(r,r),c+new Vector2(r,r),Fade(color),2*Scale);else d.FillCircle(c,r,Fade(color));
        SameLine();
    }
    public void Swatch(uint color,string text){Dot(color,true);Text(text);}

    // printf-style subset used by the menus: %.Nf, %d, %%.
    static string Format(string format,float value)
    {
        var sb=new System.Text.StringBuilder();
        for(int i=0;i<format.Length;i++)
        {
            char ch=format[i];
            if(ch!='%'||i+1>=format.Length){sb.Append(ch);continue;}
            if(format[i+1]=='%'){sb.Append('%');i++;continue;}
            int j=i+1,decimals=-1;
            if(format[j]=='.'){j++;int start=j;while(j<format.Length&&char.IsDigit(format[j]))j++;decimals=int.Parse(format[start..j],CultureInfo.InvariantCulture);}
            if(j<format.Length&&format[j]=='f'){sb.Append(value.ToString("F"+(decimals<0?6:decimals),CultureInfo.InvariantCulture));i=j;continue;}
            if(j<format.Length&&format[j]=='d'){sb.Append(((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture));i=j;continue;}
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
