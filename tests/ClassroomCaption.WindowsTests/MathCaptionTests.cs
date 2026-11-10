using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.App;
using ClassroomCaption.Core;

internal static class MathCaptionTests
{
    public static void Run(Action<string,Action> test,string root)
    {
        test("Classroom settings hide the glossary without losing saved custom overrides",()=>
        {
            var store=new SettingsStore(Path.Combine(root,"hidden-terms"));
            var settings=new AppSettings {OutputDirectory=store.Root,Glossary="демпфирование = 自定义阻尼"};
            var window=new SettingsWindow(store,settings,new ApiCredentials("synthetic"));
            if(window.FindName("Glossary") is not null)throw new Exception("Large editor still constructed");
            ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(!window.Saved||store.Load().Glossary!=settings.Glossary)throw new Exception("Hidden legacy glossary was erased");
        });
        test("Latex and explicit plain divisions render as taller atomic formulae in bilingual captions",()=>
        {
            var view=new OutlinedText {Width=760,TextSize=32,LineHeight=49,TextBackdrop=true,Outline=false,Text=@"幅值比 x/y，角频率 $\frac{\omega}{\omega_0}$，衰减项 $\nu_{33}^{2}$。"};
            var bitmap=Render(view,760,240);
            if(view.RenderedFormulaCount!=3||view.DesiredSize.Height<49)throw new Exception("Formulae are still rendered as raw text");
            var directory=Path.Combine("artifacts","math-preview");Directory.CreateDirectory(directory);
            Save(bitmap,Path.Combine(directory,"fractions-dark.png"));
            view.Color="#000000";view.LightBackdrop=true;
            Save(Render(view,760,240),Path.Combine(directory,"fractions-light.png"));
            if(view.RenderedFormulaCount!=3)throw new Exception("Theme change lost formula resources");
        });
        test("Formula updates hold incomplete input, recover from malformed input and reuse cached layout",()=>
        {
            var view=new OutlinedText {Width=420,TextSize=32,Text=@"结果 $\frac{x}{y}$"};Render(view,420,180);
            var previous=view.Text;view.SetAnimatedText(@"结果 $\frac{x}{",true);
            if(view.Text!=previous)throw new Exception("Incomplete TeX replaced a readable formula");
            view.SetAnimatedText(@"结果 $\frac{x+1}{y}$",true);Render(view,420,180);
            if(view.RenderedFormulaCount!=1)throw new Exception("Completed revision was not applied");
            var builds=view.LayoutBuilds;for(var i=0;i<10;i++){view.AdvanceAnimation(.02);Render(view,420,180);}
            if(view.LayoutBuilds!=builds)throw new Exception("Animation rebuilt mathematical layout");
            view.SetAnimatedText(@"错误 $\zzzzunknowncommand{x}$",false);Render(view,420,180);
            if(view.RenderedFormulaCount!=0||view.DesiredSize.Width<=0)throw new Exception("Invalid TeX crashed or erased the caption");
        });
        test("Tall fractions wrap and roll within the subtitle viewport while backgrounds remain draggable",()=>
        {
            var view=new OutlinedText {Width=340,TextSize=40,LineHeight=60,TextBackdrop=true,ViewportHeight=132,Text=string.Concat(Enumerable.Repeat(@"幅值 $\frac{\sqrt{x_1^2+x_2^2}}{\omega_0^2-\omega^2}$。",6))};
            Render(view,340,132);
            if(view.RenderedFormulaCount!=6||view.ScrollOffset<=0||view.DesiredSize.Height>132.1)throw new Exception("Tall formula layout escaped its viewport");
            if(!Enumerable.Range(0,132).Any(y=>view.IsOnTextBackdrop(new Point(20,y))))throw new Exception("Formula caption lost its drag region");
        });
    }
    private static RenderTargetBitmap Render(FrameworkElement view,int width,int height)
    {
        view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);return image;
    }
    private static void Save(BitmapSource image,string path)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
}
