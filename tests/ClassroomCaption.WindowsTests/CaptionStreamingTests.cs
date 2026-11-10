using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;

internal static class CaptionStreamingTests
{
    public static int Run()
    {
        var results=new List<object>();var failed=0;
        void Check(string name,Action action)
        {
            try{action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;results.Add(new{name,passed=false,error=error.Message});Console.WriteLine("FAIL "+name+": "+error.Message);}
        }
        var longSource=string.Concat(Enumerable.Range(1,40).Select(i=>$"Раздел {i}: положение центра тяжести и движение корабля. "))[..1255];
        Check("Long rolling buffers retain the unchanged Russian text when both ends change",()=>
        {
            var next=longSource[70..]+" Продолжаем рассматривать движение судна.";
            var old=CaptionDiff.Elements(longSource);var target=CaptionDiff.Elements(next);var change=CaptionDiff.Analyze(old,target);
            if(change.Kind is CaptionChangeKind.Rewrite or CaptionChangeKind.Reorder||change.Matches.Count(i=>i>=0)<1185)throw new Exception("Rolling history was mistaken for a new sentence");
            var matched=change.Matches.Where(i=>i>=0).ToArray();
            if(!matched.SequenceEqual(matched.Order())||change.Matches.Where((index,i)=>index>=0&&old[index]!=target[i]).Any())throw new Exception("Unchanged letters matched the wrong source");
        });
        Check("Long revisions preserve a shared interior beyond the bounded matrix size",()=>
        {
            var next="Новый текст: "+longSource[20..^20]+" Другой конец!";
            var change=CaptionDiff.Analyze(CaptionDiff.Elements(longSource),CaptionDiff.Elements(next));
            if(change.Matches.Count(i=>i>=0)<1180)throw new Exception("Shared interior was discarded");
        });
        Check("Evicting a large history prefix preserves the remaining Russian tail",()=>
        {
            var next=longSource[1000..]+" Продолжение.";
            var change=CaptionDiff.Analyze(CaptionDiff.Elements(longSource),CaptionDiff.Elements(next));
            if(change.Kind is CaptionChangeKind.Rewrite or CaptionChangeKind.Reorder||change.Matches.Count(i=>i>=0)<255)throw new Exception("A retained tail was treated as an unrelated sentence");
        });
        Check("Both language viewports keep three complete rows visible",()=>
        {
            foreach(var chinese in new[]{false,true})
            {
                var size=chinese?30:17;var line=size*(chinese?1.5:1.6);
                var view=new OutlinedText{Width=1920,Height=3*line+12,ViewportHeight=3*line+12,TextSize=size,LineHeight=line,Outline=false,TextBackdrop=true,FontFamily=chinese?CaptionTypography.Chinese:new FontFamily("Microsoft YaHei UI, Segoe UI")};
                view.SetAnimatedText(chinese?"甲乙\n丙丁\n戊己":"Аб\nВг\nДе",false);Render(view);
                if(view.VisibleGlyphCount!=6)throw new Exception("A language lost one of its three rows");
            }
        });
        Check("A large appended Russian result never scrolls past the revealed letters",()=>
        {
            var view=Russian();var original="Первая строка.\nВторая строка.\nТретья строка.";
            view.SetAnimatedText(original,false);Render(view);
            view.SetAnimatedText(original+"\n"+longSource[..500],true);Render(view);
            var builds=view.LayoutBuilds;
            for(var frame=0;frame<360;frame++)
            {
                view.AdvanceAnimation(1.0/30);
                if(view.VisibleGlyphCount==0)throw new Exception("Russian viewport became empty before the new row appeared");
            }
            if(view.LayoutBuilds!=builds)throw new Exception("Animation rebuilt text layout per frame");
        });
        Check("Frequent long-window updates preserve visible Russian anchors",()=>
        {
            var view=Russian();view.SetAnimatedText(longSource,false);Render(view);
            var text=longSource;
            for(var update=0;update<20;update++)
            {
                text=(text[12..]+$" Новое {update}.");
                view.SetAnimatedText(text,true);Render(view);
                for(var frame=0;frame<6;frame++)
                {
                    view.AdvanceAnimation(1.0/30);
                    if(view.VisibleGlyphCount<20)throw new Exception("Visible anchors disappeared during a rolling update");
                }
            }
            Settle(view,20);
            if(view.IsAnimating||view.VisibleGlyphCount<20)throw new Exception("Rolling updates failed to catch up");
        });
        Check("Chinese revisions continue progressively without flushing or restarting the queued suffix",()=>
        {
            var view=new OutlinedText{Width=1000,Height=300,TextSize=22,Outline=false,FontFamily=CaptionTypography.Chinese};
            var text=string.Concat(Enumerable.Repeat("船舶稳性取决于重心位置和浮力变化。",6));
            view.SetTranslation(text,"",true);Render(view);Settle(view,.6);var first=view.StableGlyphCount;
            if(first<3||first>28||!view.IsAnimating)throw new Exception("Chinese did not reveal gradually");
            view.SetTranslation("该"+text,"",true);Render(view);Settle(view,.1);
            if(view.StableGlyphCount>first+6)throw new Exception("A revision revealed the whole queued sentence");
            for(var i=0;i<12;i++){view.SetTranslation("该"+text+(i%2==0?"请注意。":"请留意。"),"",true);Render(view);Settle(view,.2);}
            Settle(view,12);
            if(view.IsAnimating||view.StableGlyphCount!=view.Text.Length)throw new Exception("Revisions accumulated a persistent animation backlog");
        });
        Check("Chinese local corrections preserve visible text through every rendered frame",()=>
        {
            var view=new OutlinedText{Width=800,Height=180,TextSize=28,Outline=false,FontFamily=CaptionTypography.Chinese};
            view.SetTranslation("船舶的稳性减小，因此需要调整货物位置。","",false);Render(view);
            view.SetTranslation("船舶的稳性降低，因此必须调整货物位置。","",true);Render(view);
            for(var frame=0;frame<45;frame++)
            {
                view.AdvanceAnimation(1.0/30);var bitmap=Render(view);var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
                if(!pixels.Where((_,i)=>i%4==3).Any(alpha=>alpha>100))throw new Exception("Chinese correction produced an empty frame");
            }
            if(view.IsAnimating)throw new Exception("Local correction never completed");
        });
        Check("Turning animation off immediately reveals the latest rolling viewport",()=>
        {
            var view=Russian();view.SetAnimatedText(longSource,true);Render(view);Settle(view,.3);
            view.SetAnimatedText(longSource,false);Render(view);
            if(view.IsAnimating||view.VisibleGlyphCount<100)throw new Exception("Latest viewport stayed queued after stopping animation");
        });
        var directory=Path.GetFullPath("artifacts/classroom-20261105");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"streaming-tests.json"),JsonSerializer.Serialize(new{total=results.Count,failed,tests=results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{results.Count-failed}/{results.Count} passed");return failed==0?0:1;
    }
    private static OutlinedText Russian()=>new(){Width=1920,Height=93.6,ViewportHeight=93.6,TextSize=17,LineHeight=27.2,TextBackdrop=true,BackdropOpacity=.7,Outline=false};
    private static void Settle(OutlinedText view,double seconds)
    {
        for(var elapsed=0.0;elapsed<seconds;elapsed+=1.0/60)view.AdvanceAnimation(Math.Min(1.0/60,seconds-elapsed));
    }
    private static RenderTargetBitmap Render(OutlinedText view)
    {
        var size=new Size(view.Width,view.Height);view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
        var image=new RenderTargetBitmap((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height),96,96,PixelFormats.Pbgra32);image.Render(view);return image;
    }
}
