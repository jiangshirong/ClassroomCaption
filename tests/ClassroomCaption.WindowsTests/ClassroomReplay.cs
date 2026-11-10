using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;

internal static class ClassroomReplay
{
    private sealed record Frame(DateTimeOffset At,string Russian,string Chinese,string File);
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static int Run(string input)
    {
        var frames=JsonSerializer.Deserialize<Frame[]>(File.ReadAllText(input),new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
        var output=Path.GetDirectoryName(Path.GetFullPath(input))!;
        var results=new List<object>();
        foreach(var session in frames.GroupBy(f=>f.File))
        {
            var beforeRestart=session.Key.Contains("094932");
            var stop=beforeRestart?"10:34:10":"11:04:00";
            var sequence=session.Where(f=>f.At.ToString("HH:mm:ss").CompareTo(stop)<0).ToArray();
            var ru=View(17,27.2);var zh=View(30,45);zh.FontFamily=CaptionTypography.Chinese;
            var at=sequence[0].At;var last=at;DateTimeOffset? blank=null;
            var blanks=new List<object>();var minimum=int.MaxValue;var minimumChinese=int.MaxValue;var saved=0;
            var captures=new HashSet<string>();var measurements=new List<object>();
            foreach(var frame in sequence)
            {
                while(at<frame.At)
                {
                    var step=Math.Min(.05,(frame.At-at).TotalSeconds);
                    ru.AdvanceAnimation(step);zh.AdvanceAnimation(step);at=at.AddSeconds(step);
                    var visible=ru.VisibleGlyphCount;
                    if(zh.Text.Length>30)minimumChinese=Math.Min(minimumChinese,zh.VisibleGlyphCount);
                    var clock=at.ToString("HH:mm:ss");
                    if(clock is "10:33:55" or "10:38:20" or "10:38:50" or "11:03:20" && captures.Add(clock))
                        Capture(ru,zh,Path.Combine(output,"replay-"+at.ToString("HHmmss")+".png"));
                    if(ru.Text.Length>100)
                    {
                        minimum=Math.Min(minimum,visible);
                        if(visible==0)
                        {
                            blank??=at;
                            if(saved<2&&at>last.AddSeconds(1))
                            {
                                var bitmap=Render(ru);using var file=File.Create(Path.Combine(output,$"blank-{session.Key[..^6]}-{saved++}.png"));
                                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(file);
                            }
                        }
                        else if(blank is {} start){blanks.Add(new {start,end=at,seconds=(at-start).TotalSeconds,ruTargetLength=ru.Text.Length,visible,detail=Details(ru)});blank=null;}
                    }
                }
                ru.SetAnimatedText(frame.Russian,true);
                zh.SetTranslation(frame.Chinese,"",true);
                Render(ru);Render(zh);last=frame.At;
                measurements.Add(new{frame.At,russian=Details(ru),chinese=Details(zh)});
            }
            if(blank is {} ongoing)blanks.Add(new {start=ongoing,end=at,seconds=(at-ongoing).TotalSeconds,ruTargetLength=ru.Text.Length,visible=ru.VisibleGlyphCount,detail=Details(ru)});
            var result=new {session=session.Key,frames=sequence.Length,rendering=new{kind="WPF offscreen classroom-log replay",width=1920,russianSize=17,chineseSize=30,weight="Normal",stepSeconds=.05,desktopAcceptance=false},minimumVisible=minimum,minimumVisibleChinese=minimumChinese,blankIntervals=blanks,final=Details(ru),measurements};
            results.Add(result);Console.WriteLine(JsonSerializer.Serialize(new{session=session.Key,frames=sequence.Length,minimumVisible=minimum,minimumVisibleChinese=minimumChinese,blankIntervals=blanks,final=Details(ru)}));
        }
        File.WriteAllText(Path.Combine(output,"replay-report.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
    private static void Capture(OutlinedText russian,OutlinedText chinese,string path)
    {
        var width=(int)russian.Width;var height=(int)Math.Ceiling(russian.Height+2+chinese.Height);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
            drawing.DrawImage(Render(russian),new Rect(0,0,russian.Width,russian.Height));
            drawing.DrawImage(Render(chinese),new Rect(0,russian.Height+2,chinese.Width,chinese.Height));
        }
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    private static object Details(OutlinedText view)
    {
        var glyphs=((System.Collections.IEnumerable)typeof(OutlinedText).GetField("glyphs",Private)!.GetValue(view)!).Cast<object>().ToArray();
        var now=(double)typeof(OutlinedText).GetField("now",Private)!.GetValue(view)!;
        T Field<T>(object g,string name)=>(T)g.GetType().GetField(name)!.GetValue(g)!;
        return new {view.ScrollOffset,view.VisibleGlyphCount,view.StableGlyphCount,pending=glyphs.Count(g=>Field<double>(g,"Start")>now),zeroTargetOpacity=glyphs.Count(g=>Field<double>(g,"OpacityTo")==0),maximumDelay=glyphs.Select(g=>Field<double>(g,"Start")-now).DefaultIfEmpty().Max()};
    }
    private static OutlinedText View(double size,double line)=>new() {Width=1920,TextSize=size,LineHeight=line,Height=3*line+12,ViewportHeight=3*line+12,TextBackdrop=true,BackdropOpacity=.7,Outline=false,Weight=FontWeights.Normal};
    private static BitmapSource Render(OutlinedText view)
    {
        var size=new Size(view.Width,view.Height);view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)size.Width,(int)Math.Ceiling(size.Height),96,96,PixelFormats.Pbgra32);bitmap.Render(view);return bitmap;
    }
}
