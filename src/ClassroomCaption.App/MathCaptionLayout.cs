using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.Core;
using WpfMath.Controls;

namespace ClassroomCaption.App;

internal sealed record MathCaptionLayout(string[] Elements,Rect[] Cells,double Width,double Height,int FormulaCount)
{
    public static MathCaptionLayout? Create(string text,double width,double size,double lineHeight,FontFamily family,FontWeight weight,Func<string,BitmapSource?> formula,double maximumRowHeight=double.PositiveInfinity)
    {
        var runs=MathNotation.Parse(text);
        if(!runs.Any(r=>r.Formula)&&MathNotation.Normalize(text)==text)return null;
        var cells=new Rect[runs.Count];var x=0d;var y=0d;var rowHeight=lineHeight;var rowStart=0;var maximum=0d;var count=0;
        void EndLine(int end)
        {
            for(var j=rowStart;j<end;j++)cells[j]=new(cells[j].X,y,cells[j].Width,rowHeight);
            maximum=Math.Max(maximum,x);y+=rowHeight;x=0;rowHeight=lineHeight;rowStart=end;
        }
        for(var i=0;i<runs.Count;i++)
        {
            var run=runs[i];var w=0d;var h=lineHeight;
            if(run.Formula&&formula(run.Text) is {} image)
            {
                var scale=Math.Min(1,Math.Min(Math.Max(1,width-4)/Math.Max(1,image.Width),Math.Max(1,maximumRowHeight-6)/image.Height));w=image.Width*scale+4;h=Math.Max(lineHeight,image.Height*scale+6);count++;
                w=Math.Min(width,w);
            }
            else
            {
                var formatted=new FormattedText(run.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface(family,FontStyles.Normal,weight,FontStretches.Normal),size,Brushes.White,1);
                w=formatted.WidthIncludingTrailingWhitespace;
            }
            if(run.Text is "\r" or "\n" or "\r\n"){EndLine(i);cells[i]=new(0,y,0,lineHeight);rowStart=i+1;continue;}
            if(x+w>width&&x>0)EndLine(i);
            cells[i]=new(x,y,w,h);x+=w;rowHeight=Math.Max(rowHeight,h);
        }
        EndLine(runs.Count);
        return new(runs.Select(r=>r.Text).ToArray(),cells,maximum,y,count);
    }
    public static BitmapSource? RenderFormula(string token,double size,string color,double dpi)
    {
        if(!token.StartsWith('$')||!token.EndsWith('$')||token.Length>514)return null;
        var tex=token[1..^1];var depth=0;
        foreach(var c in tex){if(c=='{')depth++;if(c=='}')depth--;if(depth<0||depth>16)return null;}
        if(depth!=0)return null;
        try
        {
            var control=new FormulaControl {Scale=size,Foreground=(Brush)new BrushConverter().ConvertFromString(color)!,Formula=tex};
            if(control.HasError)return null;
            control.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var s=control.DesiredSize;
            if(!double.IsFinite(s.Width)||!double.IsFinite(s.Height)||s.Width<=0||s.Height<=0||s.Width>10000||s.Height>1000)return null;
            control.Arrange(new Rect(s));control.UpdateLayout();
            var image=new RenderTargetBitmap((int)Math.Ceiling(s.Width*dpi)+2,(int)Math.Ceiling(s.Height*dpi)+2,96*dpi,96*dpi,PixelFormats.Pbgra32);
            image.Render(control);image.Freeze();return image;
        }
        catch(ArgumentException){return null;}
        catch(InvalidOperationException){return null;}
    }
}
