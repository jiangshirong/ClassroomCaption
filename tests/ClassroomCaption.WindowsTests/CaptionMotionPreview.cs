using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;

internal static class CaptionMotionPreview
{
    public static void Render(string directory)
    {
        var frames=Path.Combine(directory,"motion-frames");Directory.CreateDirectory(frames);
        var surface=new Grid {Width=960,Height=350,Background=UiKit.Brush("#EDE9DF")};
        var heading=new TextBlock {Text="字幕过渡验证 · 合成文本",Foreground=UiKit.Brush("#34454D"),FontSize=14,Margin=new(28,16,0,0),VerticalAlignment=VerticalAlignment.Top};
        var phase=new TextBlock {Text="原有译文",Foreground=UiKit.Brush("#34454D"),FontSize=12,Margin=new(0,18,28,0),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};
        var russian=new OutlinedText {Text="Центр тяжести расположен низко.\nСудно обладает достаточной остойчивостью.",Width=900,Height=80,ViewportHeight=80,TextSize=22,LineHeight=35.2,Outline=false,TextBackdrop=true,BackdropOpacity=.75,Margin=new(28,60,0,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        var chinese=new OutlinedText {Text="船舶的初稳性会减小，\n因此需要调整货物位置。",Width=900,Height=132,ViewportHeight=132,TextSize=32,LineHeight=48,Weight=FontWeights.Normal,FontFamily=CaptionTypography.Chinese,Outline=false,TextBackdrop=true,BackdropOpacity=.75,Margin=new(28,160,0,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        surface.Children.Add(heading);surface.Children.Add(phase);surface.Children.Add(russian);surface.Children.Add(chinese);
        var ru=russian.Text;var zh=chinese.Text;
        for(var frame=0;frame<240;frame++)
        {
            if(frame==30){phase.Text="局部替换 · 相同字保留";zh="船舶初稳性减小，\n因此必须调整货物的位置。";chinese.SetTranslation(zh,"",true);}
            if(frame==60){phase.Text="改译等待 · 保留上一版";chinese.SetTranslation("船舶","",true,awaitingRevision:true);if(chinese.Text!=zh)throw new Exception("Pending revision erased its tail");}
            if(frame==75){phase.Text="替换新译文 · 同步过渡";zh="船舶的初稳性明显减小，\n因此必须调整货物的位置。";chinese.SetTranslation(zh,"",true);}
            if(frame==100){phase.Text="区域已满 · 顶部渐隐并上移";ru+="\nСледует учитывать положение груза.";zh+="\n还需要考虑载荷对重心的影响。";russian.SetAnimatedText(ru,true);chinese.SetTranslation(zh,"",true);}
            if(frame==140){ru+="\nНеобходимо снизить центр тяжести.";zh+="\n应降低重心，保持足够的稳性。";russian.SetAnimatedText(ru,true);chinese.SetTranslation(zh,"",true);}
            if(frame==180){phase.Text="整句重译 · 交叉淡化";zh="载荷应尽量放低，\n避免重心继续上升。";chinese.SetTranslation(zh,"",true);}
            if(frame==210){phase.Text="连续修订 · 从当前画面接续";chinese.SetTranslation("载荷应尽量放低，\n从而保证足够的稳性。","",true);}
            if(frame==212)chinese.SetTranslation("载荷应尽量放低，\n从而保持足够的初稳性。","",true);
            russian.AdvanceAnimation(1.0/30);chinese.AdvanceAnimation(1.0/30);
            surface.Measure(new Size(960,350));surface.Arrange(new Rect(0,0,960,350));surface.UpdateLayout();
            var bitmap=new RenderTargetBitmap(960,350,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(frames,$"frame-{frame:D3}.png"));encoder.Save(file);
        }
        File.WriteAllText(Path.Combine(directory,"motion-verification.json"),JsonSerializer.Serialize(new {synthetic=true,frames=240,sampleFramesPerSecond=30,actualDisplayFrameRateMeasured=false,desktopInputUsed=false,russianLayoutBuilds=russian.LayoutBuilds,chineseLayoutBuilds=chinese.LayoutBuilds,cases=new[] {"local replacement","pending prefix","rolling overflow","whole rewrite","interrupted revision"}},new JsonSerializerOptions {WriteIndented=true}));
    }
}
