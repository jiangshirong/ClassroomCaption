using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassroomCaption.App;

internal sealed record CaptionVisualSnapshot(BitmapSource Image,Rect Bounds)
{
    internal static CaptionVisualSnapshot Capture(OverlayWindow source)
    {
        source.PrepareTransition();
        var root=(FrameworkElement)source.Content;var dpi=VisualTreeHelper.GetDpi(root);
        var image=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(source.Width*dpi.DpiScaleX)),
            Math.Max(1,(int)Math.Ceiling(source.Height*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        image.Render(root);
        var pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);
        var left=image.PixelWidth;var right=-1;var top=image.PixelHeight;var bottom=-1;
        for(var y=0;y<image.PixelHeight;y++)for(var x=0;x<image.PixelWidth;x++)
            if(pixels[(y*image.PixelWidth+x)*4+3]>0){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        if(right<left){left=top=0;right=Math.Min(image.PixelWidth-1,47);bottom=Math.Min(image.PixelHeight-1,47);}
        var crop=new CroppedBitmap(image,new Int32Rect(left,top,right-left+1,bottom-top+1));crop.Freeze();
        return new(crop,new Rect(source.Left+left/dpi.DpiScaleX,source.Top+top/dpi.DpiScaleY,
            (right-left+1)/dpi.DpiScaleX,(bottom-top+1)/dpi.DpiScaleY));
    }
}

internal readonly record struct CaptionMorphPose(Rect Bounds,double ImageOpacity,double SurfaceOpacity,double IconOpacity,double Angle);

// One continuous progress value drives both geometry and opacity. Reversing it
// retraces the same curve; no resize/reposition of a visible HWND is involved.
internal sealed class CaptionMorphVisual : FrameworkElement
{
    internal const double Diameter=48;
    private readonly CaptionVisualSnapshot? snapshot;
    private readonly Rect contentBounds,stageBounds;
    private readonly Point dockCenter;
    private readonly bool ballOnly;
    private static readonly Brush Surface=Freeze(new SolidColorBrush(Color.FromRgb(32,58,61)));
    private static readonly Pen Rim=Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(155,214,202))),1.5));
    private readonly Geometry icon=(Geometry)Application.Current.FindResource("CaptionsIcon");
    private readonly Pen iconPen;
    private static T Freeze<T>(T value) where T:Freezable {value.Freeze();return value;}
    public static readonly DependencyProperty ProgressProperty=DependencyProperty.Register(nameof(Progress),typeof(double),typeof(CaptionMorphVisual),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress {get=>(double)GetValue(ProgressProperty);set=>SetValue(ProgressProperty,value);}
    internal CaptionMorphVisual(CaptionVisualSnapshot? snapshot,Rect contentBounds,Rect stageBounds,Point dockCenter,bool ballOnly=false)
    {
        this.snapshot=snapshot;this.contentBounds=contentBounds;this.stageBounds=stageBounds;this.dockCenter=dockCenter;this.ballOnly=ballOnly;
        Width=stageBounds.Width;Height=stageBounds.Height;IsHitTestVisible=false;
        iconPen=Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(196,234,226))),1.7/(22/icon.Bounds.Width)) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round});
    }
    private static double Ease(double t){t=Math.Clamp(t,0,1);return t*t*t*(t*(t*6-15)+10);}
    internal static CaptionMorphPose Pose(Rect content,Point dock,double progress,bool ballOnly=false)
    {
        var p=Math.Clamp(progress,0,1);var start=new Point(content.Left+content.Width/2,content.Top+content.Height/2);
        var travel=Ease(ballOnly?p:(p-.16)/.84);var gather=ballOnly?1:Ease(p/.62);
        var center=new Point(start.X+(dock.X-start.X)*travel,start.Y+(dock.Y-start.Y)*travel);
        var width=content.Width+(Diameter-content.Width)*gather;var height=content.Height+(Diameter-content.Height)*gather;
        var blend=ballOnly?1:Ease((p-.30)/.32);
        return new(new Rect(center.X-width/2,center.Y-height/2,width,height),1-blend,blend,
            ballOnly?1:Ease((p-.50)/.20),(dock.X>=start.X?360:-360)*Ease(ballOnly?p:(p-.55)/.45));
    }
    protected override void OnRender(DrawingContext dc)
    {
        var pose=Pose(contentBounds,dockCenter,Progress,ballOnly);var rect=pose.Bounds;rect.Offset(-stageBounds.Left,-stageBounds.Top);
        if(snapshot is not null&&pose.ImageOpacity>0)
        {
            // Preserve the letters' aspect ratio as they gather, and round the
            // fading image into the same silhouette instead of leaving corners.
            var scale=Math.Min(rect.Width/snapshot.Bounds.Width,rect.Height/snapshot.Bounds.Height);
            var image=new Rect(rect.Left+(rect.Width-snapshot.Bounds.Width*scale)/2,rect.Top+(rect.Height-snapshot.Bounds.Height*scale)/2,
                snapshot.Bounds.Width*scale,snapshot.Bounds.Height*scale);
            var clipRadius=Math.Min(rect.Width,rect.Height)/2*pose.SurfaceOpacity;
            dc.PushClip(new RectangleGeometry(rect,clipRadius,clipRadius));dc.PushOpacity(pose.ImageOpacity);
            dc.DrawImage(snapshot.Image,image);dc.Pop();dc.Pop();
        }
        if(pose.SurfaceOpacity<=0)return;
        dc.PushOpacity(pose.SurfaceOpacity);
        var inside=rect;inside.Inflate(-.75,-.75);var radius=Math.Min(inside.Width,inside.Height)/2;
        dc.DrawRoundedRectangle(Surface,Rim,inside,radius,radius);
        if(pose.IconOpacity>0)
        {
            dc.PushOpacity(pose.IconOpacity);var center=new Point(rect.Left+rect.Width/2,rect.Top+rect.Height/2);
            dc.PushTransform(new RotateTransform(pose.Angle,center.X,center.Y));
            dc.PushTransform(new TranslateTransform(center.X-11,center.Y-11));
            var scale=22/icon.Bounds.Width;dc.PushTransform(new ScaleTransform(scale,scale));
            dc.PushTransform(new TranslateTransform(-icon.Bounds.Left,-icon.Bounds.Top));dc.DrawGeometry(null,iconPen,icon);
            dc.Pop();dc.Pop();dc.Pop();dc.Pop();dc.Pop();
        }
        dc.Pop();
    }
}
