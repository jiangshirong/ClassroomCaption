using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public partial class OverlayWindow : Window
{
    private readonly OverlaySettings settings;
    private nint handle;
    private readonly CaptionAnimationClock animationClock;
    private SubtitleActivity activity = SubtitleActivity.Ready;
    private bool demoContent;
    private static readonly ImageSource RunningIcon = CreateStatusIcon("#67E8A4");
    private static readonly ImageSource FaultIcon = CreateStatusIcon("#FFAD75");
    private static readonly ImageSource IdleIcon = CreateStatusIcon("#B4C1CF");
    private bool readingFrozen;
    private CaptionState caption = new([], [], "", 0, 0);
    public void ResetReading() { readingFrozen=false;animationClock.Paused=false;RussianCaption.SetAnimatedText("", false, true); ChineseCaption.SetTranslation("", "", false, true); demoContent = false; }
    internal void FreezeReading(){readingFrozen=true;animationClock.Paused=true;}
    internal void ResumeReading(){readingFrozen=false;animationClock.Paused=false;RenderCaption();}
    private readonly System.Windows.Threading.DispatcherTimer saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    public event Action? SettingsChanged;
    public event Action? StopTranslationRequested;
    internal void PrepareTransition()
    {
        if(!IsVisible){new WindowInteropHelper(this).EnsureHandle();EnsureVisible();}
        var root=(FrameworkElement)Content;root.Measure(new Size(Width,Height));root.Arrange(new Rect(0,0,Width,Height));root.UpdateLayout();
    }
    public OverlayWindow(OverlaySettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(DragHeader), true);
        animationClock = new(this, RussianCaption, ChineseCaption);
        ApplySettings();
        Width = Math.Clamp(settings.Width, 380, 16000);
        Left = double.IsFinite(settings.Left) ? settings.Left : 100; Top = double.IsFinite(settings.Top) ? settings.Top : 100;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
            ApplySettings(); EnsureVisible();
        };
        Loaded+=(_,_)=>EnsureVisible();
        saveDebounce.Tick += (_, _) => { saveDebounce.Stop(); SavePosition(); };
        LocationChanged += (_, _) => { if (IsLoaded) { saveDebounce.Stop(); saveDebounce.Start(); } };
        SizeChanged += (_, _) => { if (IsLoaded) { saveDebounce.Stop(); saveDebounce.Start(); } };
        Closing += (_, _) => { saveDebounce.Stop(); SavePosition(); };
    }
    public void ApplySettings()
    {
        ChineseCaption.TextSize = Math.Clamp(settings.ChineseSize, 20, 90);
        RussianCaption.TextSize = Math.Clamp(settings.RussianSize, 12, 60);
        ChineseCaption.Color = RussianCaption.Color = settings.LightTextBackground ? "#000000" : "#FFFFFF";
        ChineseCaption.LightBackdrop = RussianCaption.LightBackdrop = settings.LightTextBackground;
        ChineseCaption.BackdropOpacity = RussianCaption.BackdropOpacity = settings.TextBackgroundOpacity;
        ChineseCaption.LineHeight = ChineseCaption.TextSize * 1.5;
        RussianCaption.LineHeight = RussianCaption.TextSize * 1.6;
        Height=RequiredOverlayHeight(RussianCaption.TextSize,ChineseCaption.TextSize);
        ChineseCaption.Outline = RussianCaption.Outline = false;
        ChineseCaption.InvalidateVisual(); RussianCaption.InvalidateVisual();
        PanelBrush.Opacity = 0;
        ResizeMode = ResizeMode.NoResize;
        EnsureVisible();
        RenderCaption();
        if (handle != 0)
        {
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            style = settings.ClickThrough ? style | 0x20L | 0x08000000L : style & ~0x20L & ~0x08000000L;
            SetWindowLongPtr(handle, -20, new nint(style));
        }
    }
    public void SetCaption(CaptionState state, SubtitleActivity currentActivity = SubtitleActivity.Ready, bool isDemoContent = false)
    {
        if(ReferenceEquals(caption,state)&&activity==currentActivity&&demoContent==(isDemoContent||currentActivity==SubtitleActivity.Demo)&&renderWidth==ActualWidth)return;
        caption = state; activity = currentActivity; demoContent = isDemoContent || currentActivity == SubtitleActivity.Demo; RenderCaption();
    }
    public void SetActivity(SubtitleActivity value)
    {
        if (activity == value) return;
        activity = value; RenderCaption();
    }
    private double renderWidth=-1;
    private void RenderCaption()
    {
        if(readingFrozen)return;
        renderWidth=ActualWidth;
        var state = caption;
        RussianScroll.MaxHeight = ThreeLineHeight(RussianCaption.LineHeight);
        ChineseScroll.MaxHeight = ThreeLineHeight(ChineseCaption.LineHeight);
        RussianCaption.ViewportHeight=RussianScroll.MaxHeight;
        ChineseCaption.ViewportHeight=ChineseScroll.MaxHeight;
        var width=Math.Max(380,ActualWidth>0?ActualWidth:Width);
        // Retain several screenful rows, including the row fading out. A fixed
        // 600-character buffer can contain fewer than three rows on a 4K screen.
        var ruLimit=Math.Clamp((int)Math.Ceiling(width/(RussianCaption.TextSize*.45))*5,600,6000);
        var zhLimit=Math.Clamp((int)Math.Ceiling(width/ChineseCaption.TextSize)*5,400,4000);
        var display = CaptionFlowComposer.Compose(state,ruLimit,zhLimit);
        // Progressive captions are a reading behavior, independent of Windows'
        // decorative animation preference (often disabled on classroom PCs).
        var animate = activity is SubtitleActivity.Listening or SubtitleActivity.Demo;
        RussianCaption.SetAnimatedText(display.Russian, animate);
        var chinese = !string.IsNullOrWhiteSpace(display.Chinese) ? display.Chinese :
            !string.IsNullOrWhiteSpace(display.Russian) ? "等待译文…" : SubtitleActivityText.Empty(activity);
        ChineseCaption.SetTranslation(display.Chinese, chinese, animate,awaitingRevision:!string.IsNullOrWhiteSpace(state.UntranslatedRussian));
        UpdateStatusBadge();
    }
    private static ImageSource CreateStatusIcon(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var image = new DrawingImage(new GeometryDrawing(brush, null, new EllipseGeometry(new Point(4, 4), 4, 4)));
        image.Freeze();
        return image;
    }
    private void UpdateStatusBadge()
    {
        var label = activity switch
        {
            SubtitleActivity.Listening => "翻译运行中",
            SubtitleActivity.Reconnecting => "错误中断 · 正在自动恢复",
            SubtitleActivity.RecoveringAudio => "错误中断 · 正在恢复麦克风",
            _ => SubtitleActivityText.Label(activity)
        };
        var icon = activity switch
        {
            SubtitleActivity.Listening => RunningIcon,
            SubtitleActivity.Reconnecting or SubtitleActivity.RecoveringAudio or SubtitleActivity.Faulted => FaultIcon,
            _ => IdleIcon
        };
        var onRussian = RussianCaption.Text.Length > 0;
        RussianCaption.SetStatusIcon(!demoContent && onRussian ? icon : null, !demoContent && onRussian ? label : "");
        ChineseCaption.SetStatusIcon(!demoContent && !onRussian ? icon : null, !demoContent && !onRussian ? label : "");
    }
    public static double ThreeLineHeight(double lineHeight)=>3*lineHeight+12;
    public static double RequiredOverlayHeight(double russianSize,double chineseSize)
        =>ThreeLineHeight(russianSize*1.6)+2+ThreeLineHeight(chineseSize*1.5);
    public void SetListeningFeedback(string message)
    {
        if(readingFrozen||caption.Confirmed.Count>0||caption.Drafts.Count>0||(caption.Interrupted?.Count??0)>0||caption.UntranslatedRussian.Length>0)return;
        if (activity is not (SubtitleActivity.Listening or SubtitleActivity.Buffering or SubtitleActivity.Reconnecting)) return;
        ChineseCaption.Text = message; RussianCaption.Text = "";
        UpdateStatusBadge();
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if(e.ClickCount==2&&CaptionHitTest(e.GetPosition((IInputElement)Content))>=1&&!IsInteractive(e.OriginalSource as DependencyObject))
        {e.Handled=true;StopTranslationRequested?.Invoke();return;}
        if (e.ButtonState != MouseButtonState.Pressed || CaptionHitTest(e.GetPosition((IInputElement)Content))!=2 || IsInteractive(e.OriginalSource as DependencyObject)) return;
        DragMove(); EnsureVisible(); SavePosition(); e.Handled = true;
    }
    public int CaptionHitTest(Point point)
    {
        if(settings.ClickThrough)return -1; // HTTRANSPARENT; native layered alpha also passes blank pixels through.
        if(Content is not FrameworkElement root)return -1;
        bool Contains(OutlinedText text,System.Windows.Controls.ScrollViewer scroll)
            => scroll.Visibility==Visibility.Visible && scroll.ActualWidth>0 && scroll.ActualHeight>0
            && new Rect(scroll.RenderSize).Contains(root.TranslatePoint(point,scroll))
            && text.IsOnTextBackdrop(root.TranslatePoint(point,text));
        return Contains(RussianCaption,RussianScroll)||Contains(ChineseCaption,ChineseScroll)?settings.Locked?1:2:-1;
    }
    public static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton or System.Windows.Controls.Button || node.GetType().Name == "ResizeGrip") return true;
        return false;
    }
    public void SavePosition()
    {
        settings.Left = Left; settings.Top = Top; settings.Width = ActualWidth > 0 ? ActualWidth : Width; settings.Height = ActualHeight > 0 ? ActualHeight : Height;
        SettingsChanged?.Invoke();
    }
    private void EnsureVisible()
    {
        if(handle==0)return;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null) return;
        var screen=NativeDisplays.FromWindow(handle);
        if(!GetWindowRect(handle,out var native))return;
        var inverse=source.CompositionTarget.TransformFromDevice;
        Rect Convert(System.Drawing.Rectangle rect)=>new(Left+(rect.Left-native.Left)*inverse.M11,Top+(rect.Top-native.Top)*inverse.M22,rect.Width*inverse.M11,rect.Height*inverse.M22);
        var layout=ScreenLayout(Convert(screen.Bounds),Convert(screen.WorkingArea),Top,RequiredOverlayHeight(RussianCaption.TextSize,ChineseCaption.TextSize));
        Width=layout.Width;Height=layout.Height;Left=layout.Left;Top=layout.Top;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out NativeRect rectangle);
    public static Rect ScreenLayout(Rect bounds,Rect workingArea,double top,double requestedHeight)
    {
        var height=Math.Min(requestedHeight,workingArea.Height);
        return new(bounds.Left,Math.Clamp(top,workingArea.Top,Math.Max(workingArea.Top,workingArea.Bottom-height)),bounds.Width,height);
    }
    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if(message==0x00A3&&wParam.ToInt32()==2) // WM_NCLBUTTONDBLCLK / HTCAPTION
        {handled=true;StopTranslationRequested?.Invoke();return 0;}
        if(message==0x0203) // Locked caption backgrounds are client areas.
        {
            var packed=lParam.ToInt64();var pixel=new Point(unchecked((short)(packed&0xffff)),unchecked((short)((packed>>16)&0xffff)));
            var source=HwndSource.FromHwnd(hwnd);var point=source?.CompositionTarget?.TransformFromDevice.Transform(pixel)??pixel;
            if(CaptionHitTest(point)>=1){handled=true;StopTranslationRequested?.Invoke();return 0;}
        }
        // No context menu on captions. The tray menu remains independently usable.
        if(message is 0x00A4 or 0x00A5 or 0x007B){handled=true;return 0;}
        if (message == 0x0084) // WM_NCHITTEST
        {
            var packed = lParam.ToInt64();
            var screenPoint = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            var point = PointFromScreen(screenPoint);
            handled=true;return new nint(CaptionHitTest(point));
        }
        if (message is 0x007E or 0x02E0 or 0x0232) Dispatcher.BeginInvoke(EnsureVisible);
        return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
