using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// One layout for the whole target sentence. Glyphs keep their identity across revisions.
public class OutlinedText : FrameworkElement
{
    private sealed class CaptionPeer(OutlinedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.StatusLabel.Length == 0 ? owner.Text : owner.StatusLabel + "，" + owner.Text;
        protected override string GetClassNameCore() => "Caption";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new CaptionPeer(this);
    private sealed class Glyph
    {
        public string Text = "";
        public string Element = "";
        public readonly TranslateTransform Translation = new();
        public readonly DrawingGroup Drawing = new();
        public readonly DrawingGroup Keyline = new();
        public Point From, To;
        public double OpacityFrom = 1, OpacityTo = 1, Start, Duration = .24;
        public bool Leaving;
        public bool OnStage;
        public Size Cell;
        public Rect InkBounds=Rect.Empty;
        public double Progress(double now) => Math.Clamp((now - Start) / Duration, 0, 1);
        public (Point Position, double Opacity) Pose(double now)
        {
            var t = 1 - Math.Pow(1 - Progress(now), 3);
            return (new(From.X + (To.X - From.X) * t, From.Y + (To.Y - From.Y) * t), OpacityFrom + (OpacityTo - OpacityFrom) * t);
        }
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(nameof(TextSize), typeof(double), typeof(OutlinedText), new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(string), typeof(OutlinedText), new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AdaptiveForegroundProperty = DependencyProperty.Register(nameof(AdaptiveForeground), typeof(Brush), typeof(OutlinedText), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));
    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(OutlinedText), new FrameworkPropertyMetadata(new FontFamily("Microsoft YaHei UI, Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBackdropProperty = DependencyProperty.Register(nameof(TextBackdrop), typeof(bool), typeof(OutlinedText), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LightBackdropProperty = DependencyProperty.Register(nameof(LightBackdrop), typeof(bool), typeof(OutlinedText), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BackdropOpacityProperty = DependencyProperty.Register(nameof(BackdropOpacity), typeof(double), typeof(OutlinedText), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender,(d,_)=>((OutlinedText)d).UpdateBackdrops(),(_,value)=>value is double opacity&&double.IsFinite(opacity)?Math.Clamp(opacity,0,1):.75));
    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(nameof(ViewportHeight),typeof(double),typeof(OutlinedText),new FrameworkPropertyMetadata(double.PositiveInfinity,FrameworkPropertyMetadataOptions.AffectsMeasure|FrameworkPropertyMetadataOptions.AffectsRender));
    public double ViewportHeight {get=>(double)GetValue(ViewportHeightProperty);set=>SetValue(ViewportHeightProperty,value);}
    public double ScrollOffset {get;private set;}
    private double viewportOffset,viewportFrom,viewportTo,viewportStart;
    private const double ScrollDuration=.28;
    public CaptionChangeKind LastChange {get;private set;}
    private double layoutHeight=-1;
    private bool Rolling => double.IsFinite(ViewportHeight);
    public double BackdropOpacity { get => (double)GetValue(BackdropOpacityProperty); set => SetValue(BackdropOpacityProperty,value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty,value); }
    public bool TextBackdrop { get => (bool)GetValue(TextBackdropProperty); set => SetValue(TextBackdropProperty,value); }
    public bool LightBackdrop { get => (bool)GetValue(LightBackdropProperty); set => SetValue(LightBackdropProperty,value); }
    public Brush? AdaptiveForeground { get => (Brush?)GetValue(AdaptiveForegroundProperty); set => SetValue(AdaptiveForegroundProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double TextSize { get => (double)GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool Outline { get; set; } = true;
    public FontWeight Weight { get; set; } = FontWeights.Medium;
    public double LineHeight { get; set; } = double.NaN;
    private readonly List<Glyph> glyphs = [];
    private readonly DrawingGroup scene = new();
    private readonly DrawingGroup keylineScene = new();
    private sealed class BackdropLine
    {
        public double Y;
        public readonly List<Glyph> Glyphs = [];
        public readonly RectangleGeometry Shape = new() { RadiusX=4,RadiusY=4 };
        public readonly DrawingGroup Layer = new();
        public readonly GeometryDrawing Drawing = new();
        public BackdropLine() { Drawing.Geometry=Shape;Layer.Children.Add(Drawing); }
    }
    private readonly DrawingGroup backdropScene = new();
    private readonly ImageDrawing statusDrawing = new();
    internal ImageSource? StatusIcon => statusDrawing.ImageSource;
    internal string StatusLabel { get; private set; } = "";
    internal Rect StatusBounds => statusDrawing.Rect;
    private const double StatusSize = 12;
    private double StatusInset => StatusIcon is null ? 0 : 20;
    private double TotalHorizontalInset => HorizontalInset * 2 + StatusInset;
    internal void SetStatusIcon(ImageSource? icon, string label = "")
    {
        StatusLabel = label;
        if (ReferenceEquals(icon, StatusIcon)) return;
        var hasIconChanged = (icon is null) != (StatusIcon is null);
        statusDrawing.ImageSource = icon;
        if (!hasIconChanged) return;
        dirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }
    private readonly List<BackdropLine> backdropLines = [];
    private sealed record GlyphStamp(BitmapSource Image, BitmapSource Keyline, Rect Bounds);
    private readonly Dictionary<string, GlyphStamp> stampCache = [];
    private FontWeight layoutWeight;
    private double layoutDpi;
    private FontFamily? layoutFamily;
    private double layoutLineHeight;
    private bool layoutBackdrop;
    private double HorizontalInset => TextBackdrop ? 10 : 2;
    private double VerticalInset => TextBackdrop ? 4 : 0;
    private string sceneColor = "";
    private bool sceneOutline;
    private Brush? ink;
    private Pen? outlinePen;
    private string[] elements = [];
    private double now, layoutWidth = -1, layoutSize;
    private string layoutText = "";
    private bool requestedAnimation, dirty = true, hasTranslation, translatedText;
    private (string Text,double Width,double Size,double Line,string Color,FontFamily Family,FontWeight Weight,double Dpi,double Viewport)? mathKey;
    private MathCaptionLayout? mathLayout;
    private readonly Dictionary<string,BitmapSource?> formulaCache=[];
    public int RenderedFormulaCount {get;private set;}
    private BitmapSource? Formula(string token)
    {
        if(formulaCache.TryGetValue(token,out var image))return image;
        if(formulaCache.Count>=128)formulaCache.Clear();
        image=MathCaptionLayout.RenderFormula(token,TextSize,Color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        formulaCache[token]=image;return image;
    }
    private MathCaptionLayout? MathLayout(double width)
    {
        var key=(Text,width,TextSize,LineHeight,Color,FontFamily,Weight,VisualTreeHelper.GetDpi(this).PixelsPerDip,ViewportHeight);
        if(!mathKey.HasValue||!mathKey.Value.Equals(key))
        {
            formulaCache.Clear();mathKey=key;
            mathLayout=MathCaptionLayout.Create(Text,width,TextSize,double.IsFinite(LineHeight)?LineHeight:TextSize*1.5,FontFamily,Weight,Formula,Rolling?ViewportHeight-VerticalInset*2-4:double.PositiveInfinity);
        }
        RenderedFormulaCount=mathLayout?.FormulaCount??0;return mathLayout;
    }
    public bool IsAnimating => dirty && requestedAnimation || glyphs.Any(g => g.Progress(now) < 1) || Math.Abs(viewportOffset-viewportTo)>.01;
    public int StableGlyphCount => glyphs.Count(g => !g.Leaving && g.Pose(now).Opacity >= .999);
    public int LayoutBuilds { get; private set; }
    public int VisibleGlyphCount => glyphs.Count(g=>g.OnStage);
    public event Action? AnimationRequested;
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OutlinedText)d;
        view.dirty = true;
        if (!view.requestedAnimation) view.glyphs.Clear();
    }
    public void SetAnimatedText(string value, bool animate, bool newPage = false)
    {
        if(!newPage&&Text==value&&requestedAnimation==animate&&!dirty)return;
        if(!newPage&&MathNotation.HasIncompleteMath(value)&&RenderedFormulaCount>0)return;
        if (newPage && !animate) { glyphs.Clear(); elements = []; layoutText = ""; dirty = true; }
        requestedAnimation = animate;
        if (Text != value) Text = value;
        if (!animate && IsAnimating)
        {
            foreach (var glyph in glyphs) glyph.Start = now - glyph.Duration - .001;
            UpdateViewport();
            foreach(var glyph in glyphs)UpdatePose(glyph);
            foreach (var glyph in glyphs.Where(g => g.Leaving).ToArray()) { scene.Children.Remove(glyph.Drawing); keylineScene.Children.Remove(glyph.Keyline); glyphs.Remove(glyph); }
            UpdateStage();
            UpdateBackdrops();
        }
        if (dirty) InvalidateVisual();
        if(animate&&IsAnimating)AnimationRequested?.Invoke();
    }
    public bool SetTranslation(string translated, string fallback, bool animate, bool newPage = false, bool awaitingRevision = false)
    {
        translatedText=true;
        if (newPage) hasTranslation = false;
        // ASR revisions can invalidate a draft before its replacement is available.
        // Hold the previous visible translation, marked as updating by the caller.
        if (string.IsNullOrWhiteSpace(translated) && hasTranslation && animate) return true;
        // An invalidated ASR tail can temporarily leave only the already translated prefix.
        // Hold A while B is pending, instead of treating that intermediate prefix as a deletion.
        if(awaitingRevision&&hasTranslation&&animate&&translated.Length<Text.Length&&Text.StartsWith(translated,StringComparison.Ordinal))return true;
        hasTranslation = !string.IsNullOrWhiteSpace(translated);
        SetAnimatedText(hasTranslation ? translated : fallback, animate && hasTranslation, newPage);
        return false;
    }
    public void AdvanceAnimation(double seconds)
    {
        if (seconds <= 0 || !IsAnimating) return;
        now += seconds;
        UpdateViewport();
        for (var i = glyphs.Count - 1; i >= 0; i--)
        {
            var glyph = glyphs[i];
            UpdatePose(glyph);
            if (glyph.Leaving && glyph.Progress(now) >= 1) { scene.Children.Remove(glyph.Drawing); keylineScene.Children.Remove(glyph.Keyline); glyphs.RemoveAt(i); }
        }
        // Mutable retained transforms/opacity are composed by WPF; text is not redrawn per tick.
        UpdateStage();
        UpdateBackdrops();
    }
    private void UpdatePose(Glyph glyph)
    {
        var pose = glyph.Pose(now);
        if (glyph.Translation.X != pose.Position.X) glyph.Translation.X = pose.Position.X;
        var y=pose.Position.Y-viewportOffset;
        if (glyph.Translation.Y != y) glyph.Translation.Y = y;
        var opacity=DisplayOpacity(glyph,pose);
        if (glyph.Drawing.Opacity != opacity) glyph.Drawing.Opacity = opacity;
        if (glyph.Keyline.Opacity != opacity) glyph.Keyline.Opacity = opacity;
    }
    private double DisplayOpacity(Glyph glyph,(Point Position,double Opacity) pose)
    {
        // The viewport follows revealed letters, not the end of a queued result.
        // Fade each outgoing row together as it crosses the upper edge.
        if(!Rolling||viewportOffset<=0)return pose.Opacity;
        var y=pose.Position.Y-viewportOffset;
        return pose.Opacity*Math.Clamp((y+TextSize*.55)/(TextSize*.55),0,1);
    }
    private void UpdateViewport()
    {
        if(!Rolling||!requestedAnimation)
        {
            viewportOffset=viewportFrom=viewportTo=Rolling?ScrollOffset:0;
            return;
        }
        var bottom=glyphs.Where(g=>!g.Leaving&&(g.OpacityFrom>.001||g.Start<=now))
            .Select(g=>g.To.Y+g.Cell.Height+VerticalInset+4).DefaultIfEmpty(ActualHeight).Max();
        var target=Math.Clamp(bottom-ActualHeight,0,ScrollOffset);
        if(Math.Abs(target-viewportTo)>.01)
        {
            viewportFrom=viewportOffset;viewportTo=target;viewportStart=now;
        }
        var progress=Math.Clamp((now-viewportStart)/ScrollDuration,0,1);
        viewportOffset=viewportFrom+(viewportTo-viewportFrom)*(1-Math.Pow(1-progress,3));
    }
    private void UpdateStage()
    {
        var viewport=new Rect(RenderSize);viewport.Inflate(2,2);
        foreach(var glyph in glyphs)
        {
            var position=new Point(glyph.Translation.X,glyph.Translation.Y);
            var bounds=glyph.InkBounds;
            if(!bounds.IsEmpty)bounds.Offset(position.X,position.Y);
            var visible=!Rolling||!bounds.IsEmpty&&glyph.Drawing.Opacity>.001&&viewport.IntersectsWith(bounds);
            if(visible==glyph.OnStage)continue;
            glyph.OnStage=visible;
            if(visible){scene.Children.Add(glyph.Drawing);keylineScene.Children.Add(glyph.Keyline);}
            else{scene.Children.Remove(glyph.Drawing);keylineScene.Children.Remove(glyph.Keyline);}
        }
    }
    private void RefreshInk()
    {
        if (ink != null && sceneColor == Color && sceneOutline == Outline && layoutSize == TextSize) return;
        sceneColor = Color; sceneOutline = Outline;
        ink = (Brush)new BrushConverter().ConvertFromString(Color)!; ink.Freeze();
        outlinePen = Outline ? new Pen(Brushes.Black, Math.Max(1.4, TextSize / 22)) { LineJoin = PenLineJoin.Round } : null;
        outlinePen?.Freeze();
        stampCache.Clear();
        foreach (var glyph in glyphs) PaintGlyph(glyph);
    }
    private void PaintGlyph(Glyph glyph)
    {
        glyph.Drawing.Children.Clear();
        glyph.Keyline.Children.Clear();
        if (!string.IsNullOrWhiteSpace(glyph.Element))
        {
            var stamp = Stamp(glyph.Element);
            glyph.InkBounds=stamp.Bounds;
            var bounds=stamp.Bounds;
            if(glyph.Element.StartsWith('$')&&Formula(glyph.Element) is {} formula)
            {
                var scale=Math.Min(1,Math.Min(Math.Max(1,glyph.Cell.Width-4)/Math.Max(1,formula.Width),Math.Max(1,glyph.Cell.Height-6)/formula.Height));
                bounds=new(0,Math.Max(0,(glyph.Cell.Height-formula.Height*scale)/2),formula.Width*scale,formula.Height*scale);
            }
            else if(mathLayout is not null)bounds.Offset(0,Math.Max(0,(glyph.Cell.Height-(double.IsFinite(LineHeight)?LineHeight:TextSize*1.5))/2));
            glyph.InkBounds=bounds;
            glyph.Drawing.Children.Add(new ImageDrawing(stamp.Image, bounds));
            glyph.Keyline.Children.Add(new ImageDrawing(stamp.Keyline, bounds));
        }
        glyph.Drawing.Transform = glyph.Translation;
        glyph.Keyline.Transform = glyph.Translation;
        UpdatePose(glyph);
    }
    private GlyphStamp Stamp(string element)
    {
        if (stampCache.TryGetValue(element, out var cached)) return cached;
        if(stampCache.Count>=1024)stampCache.Clear();
        if(element.StartsWith('$')&&Formula(element) is {} formula)
        {
            var empty=BitmapSource.Create(1,1,96,96,PixelFormats.Pbgra32,null,new byte[4],4);empty.Freeze();
            var result=new GlyphStamp(formula,empty,new Rect(0,0,formula.Width,formula.Height));stampCache[element]=result;return result;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var letter = new FormattedText(element, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, Weight, FontStretches.Normal), TextSize, Brushes.White, dpi);
        if (double.IsFinite(LineHeight)) letter.LineHeight = LineHeight;
        var geometry = letter.BuildGeometry(new Point(0, 0));
        // A cached thin dark edge separates glyph silhouettes from slide details.
        // Keep it outside the fill; no blur/effects or geometry work per capture frame.
        var edge = new Pen(Brushes.Black, 1.3) { LineJoin = PenLineJoin.Round };
        var bounds = geometry.GetRenderBounds(edge);
        if (outlinePen != null) bounds.Union(geometry.GetRenderBounds(outlinePen));
        if (bounds.IsEmpty) bounds = new Rect(0, 0, 1, 1);
        bounds.Inflate(1 / dpi, 1 / dpi);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.Left, -bounds.Top));
            if (outlinePen != null) dc.DrawGeometry(null, outlinePen, geometry);
            dc.DrawGeometry(ink, null, geometry); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi)), Math.Max(1, (int)Math.Ceiling(bounds.Height * dpi)), dpi * 96, dpi * 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        var edgeVisual = new DrawingVisual();
        using (var dc = edgeVisual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.Left, -bounds.Top));
            dc.DrawGeometry(null, edge, geometry); dc.Pop();
        }
        var keyline = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, dpi * 96, dpi * 96, PixelFormats.Pbgra32);
        keyline.Render(edgeVisual); keyline.Freeze();
        var stamp = new GlyphStamp(bitmap, keyline, new Rect(bounds.Location, new Size(bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi)));
        stampCache[element] = stamp; return stamp;
    }
    private FormattedText Format(double width)
    {
        var text = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, Weight, FontStretches.Normal), TextSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(20, double.IsFinite(width) ? width : 1000), MaxTextHeight = 1000000, Trimming = TextTrimming.None };
        if (double.IsFinite(LineHeight)) text.LineHeight = LineHeight;
        return text;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(20, (double.IsFinite(availableSize.Width) ? availableSize.Width : 1000) - TotalHorizontalInset);
        if(MathLayout(width) is {} math)
        {
            var full=math.Height+VerticalInset*2+4;
            return new(Math.Min(width+TotalHorizontalInset,math.Width+TotalHorizontalInset),Rolling?Math.Min(full,Math.Max(TextSize*1.25+VerticalInset*2+4,ViewportHeight)):full);
        }
        var text = Format(width);
        var height=text.Height+VerticalInset*2+4;
        return new(Math.Min(width + TotalHorizontalInset, text.WidthIncludingTrailingWhitespace + TotalHorizontalInset),Rolling?Math.Min(height,Math.Max(TextSize*1.25+VerticalInset*2+4,ViewportHeight)):height);
    }
    private void LayoutGlyphs(double width)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!dirty && layoutWidth == width && layoutSize == TextSize && layoutWeight == Weight && layoutDpi == dpi && layoutText == Text && layoutFamily==FontFamily && layoutLineHeight.Equals(LineHeight) && layoutBackdrop==TextBackdrop && layoutHeight==ActualHeight) return;
        LayoutBuilds++;
        if (layoutSize != TextSize || layoutWeight != Weight || layoutDpi != dpi || layoutFamily!=FontFamily || !layoutLineHeight.Equals(LineHeight)) stampCache.Clear();
        RefreshInk();
        var math = MathLayout(width);
        var formatted = math is null ? Format(width) : null;
        ScrollOffset=Rolling?Math.Max(0,(math?.Height??formatted!.Height)+VerticalInset*2+4-ActualHeight):0;
        var next = math?.Elements??CaptionDiff.Elements(Text);
        var previous = glyphs.Where(g => !g.Leaving).ToArray();
        var change=CaptionDiff.Analyze(elements,next);LastChange=change.Kind;
        var matches = change.Matches;
        var keep = matches.Where(i => i >= 0 && i < previous.Length).ToHashSet();
        var moving = new List<Glyph>();
        if (requestedAnimation)
        {
            moving.AddRange(glyphs.Where(g => g.Leaving && g.Progress(now) < 1));
            for (var i = 0; i < previous.Length; i++)
            {
                if (keep.Contains(i)) continue;
                var g = previous[i]; var pose = g.Pose(now);
                // Glyphs not yet visible need no exit animation.
                if (pose.Opacity <= .01) continue;
                g.From = pose.Position; g.To = pose.Position;
                g.OpacityFrom = pose.Opacity; g.OpacityTo = 0; g.Start = now; g.Duration = .22; g.Leaving = true;
                moving.Add(g);
            }
        }
        var append = layoutText.Length == 0 || Text.StartsWith(layoutText, StringComparison.Ordinal);
        var additions = matches.Count(i => i < 0);
        // A long result must remain a sequence, rather than squeezing every
        // character into the same half-second. Cap catch-up speed, not duration.
        bool Queued(Glyph g)=>g.OpacityFrom<=.001&&g.OpacityTo>0&&g.Pose(now).Opacity<=.001;
        var pendingCount=previous.Count(Queued);
        var spacing=translatedText
            ?1.0/Math.Clamp(10+(additions+pendingCount)*.12,10,26)
            :1.0/Math.Clamp(18+(additions+pendingCount)*.18,18,42);
        // Reconcile the remaining sequence in reading order. Appending after the
        // previous queue's end on every revision can create minutes of backlog.
        var revealAt=Math.Clamp(previous.Where(Queued).Select(g=>g.Start).DefaultIfEmpty(now+spacing).Min(),now+.001,now+spacing);
        var offset = 0;
        var active = new List<Glyph>();
        var replacements=CaptionDiff.ReplacementSources(matches,previous.Length);
        for (var i = 0; i < next.Length; i++)
        {
            var highlight = formatted?.BuildHighlightGeometry(new Point(HorizontalInset + StatusInset, VerticalInset), offset, next[i].Length);
            offset += next[i].Length;
            var bounds = math is null ? highlight?.Bounds ?? new Rect(0, 0, 0, 0) : math.Cells[i];
            if(math is not null)bounds.Offset(HorizontalInset + StatusInset,VerticalInset);
            var target = new Point(bounds.Left, bounds.Top);
            Glyph g;
            var match = matches[i];
            if (requestedAnimation && match >= 0 && match < previous.Length)
            {
                g = previous[match]; var pose = g.Pose(now);
                if(Queued(g))
                {
                    g.From=new(target.X,target.Y+(append?2:0));g.To=target;
                    g.Start=revealAt;revealAt+=spacing;
                }
                else if (g.To != target)
                {
                    g.From = pose.Position; g.To = target; g.OpacityFrom = pose.Opacity; g.OpacityTo = 1; g.Start = now; g.Duration = Rolling?ScrollDuration:.24;
                }
            }
            else
            {
                var reveal=requestedAnimation&&(!Rolling||target.Y+bounds.Height>viewportOffset);
                var start=reveal?revealAt:now-1;
                if(reveal)revealAt+=spacing;
                var from=new Point(target.X,target.Y+(append?2:0));
                if(requestedAnimation&&change.Kind==CaptionChangeKind.Replace&&replacements[i]>=0)from=previous[replacements[i]].Pose(now).Position;
                g = new() { Text = next[i], From = from, To = target,
                    OpacityFrom = reveal ? 0 : 1, Start = start, Duration = requestedAnimation ? .22 : .001 };
                if (!requestedAnimation) { g.From = target; g.Start = now - 1; }
            }
            g.Element = next[i]; g.Cell=bounds.Size; active.Add(g);
        }
        glyphs.Clear(); glyphs.AddRange(active); glyphs.AddRange(moving);
        UpdateViewport();
        scene.Children.Clear();
        keylineScene.Children.Clear();
        foreach (var glyph in glyphs) { glyph.OnStage=false;PaintGlyph(glyph); }
        UpdateStage();
        backdropLines.Clear(); backdropScene.Children.Clear();
        if(TextBackdrop)
        foreach(var glyph in glyphs)
        {
            var line=backdropLines.FirstOrDefault(l=>Math.Abs(l.Y-glyph.To.Y)<5);
            if(line==null) { line=new() {Y=glyph.To.Y};backdropLines.Add(line);backdropScene.Children.Add(line.Layer); }
            line.Glyphs.Add(glyph);
        }
        UpdateBackdrops();
        elements = next; layoutText = Text; layoutWidth = width; layoutSize = TextSize; layoutWeight = Weight; layoutDpi = dpi; dirty = false;
        layoutFamily=FontFamily;layoutLineHeight=LineHeight;layoutBackdrop=TextBackdrop;
        layoutHeight=ActualHeight;
        // Resize/font reflow can start motion even when the text is unchanged.
        if(requestedAnimation&&IsAnimating)AnimationRequested?.Invoke();
    }
    private void UpdateBackdrops()
    {
        // Composite the opaque row shapes first, then apply transparency once to
        // the language group. Overlapping rows must not accumulate darker alpha.
        backdropScene.Opacity=BackdropOpacity;
        var iconBounds = Rect.Empty;
        var viewport = new Rect(RenderSize);
        foreach(var line in backdropLines)
        {
            var bounds=Rect.Empty;var opacity=0.0;
            foreach(var glyph in line.Glyphs)
            {
                var pose=glyph.Pose(now);
                var visibleOpacity=DisplayOpacity(glyph,pose);
                if(visibleOpacity<=.001||glyph.Cell.Width<=0||glyph.InkBounds.IsEmpty)continue;
                opacity=Math.Max(opacity,visibleOpacity);
                bounds.Union(new Rect(new Point(pose.Position.X,pose.Position.Y-viewportOffset),new Size(glyph.Cell.Width*pose.Opacity,Math.Max(TextSize,glyph.Cell.Height))));
            }
            line.Layer.Opacity=opacity;
            line.Drawing.Brush=LightBackdrop?Brushes.White:Brushes.Black;
            if(bounds.IsEmpty)line.Shape.Rect=Rect.Empty;
            else
            {
                bounds.Inflate(HorizontalInset, VerticalInset);
                if (StatusIcon is not null && iconBounds.IsEmpty)
                {
                    var visible = Rect.Intersect(bounds, viewport);
                    if (visible.Height >= StatusSize + 4)
                    {
                        bounds.X -= StatusInset;
                        bounds.Width += StatusInset;
                        iconBounds = new Rect(bounds.Left + 6, visible.Top + (visible.Height - StatusSize) / 2, StatusSize, StatusSize);
                    }
                }
                line.Shape.Rect = bounds;
            }
        }
        if (statusDrawing.Rect != iconBounds) statusDrawing.Rect = iconBounds;
    }
    public bool IsOnTextBackdrop(Point point)
    {
        if(!new Rect(RenderSize).Contains(point))return false;
        if(!TextBackdrop||BackdropOpacity<=.001)return false;
        foreach(var line in backdropLines)
            if(line.Layer.Opacity>.001&&!line.Shape.Rect.IsEmpty&&line.Shape.FillContains(point))return true;
        return false;
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
        => TextBackdrop ? IsOnTextBackdrop(parameters.HitPoint)?new PointHitTestResult(this,parameters.HitPoint):null : base.HitTestCore(parameters);
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth < 10 || ActualHeight < 5) return;
        LayoutGlyphs(Math.Max(20, ActualWidth - TotalHorizontalInset));
        RefreshInk();
        UpdateStage();
        if(Rolling)
        {
            var area=new Rect(RenderSize);drawingContext.PushClip(new RectangleGeometry(area));
        }
        if(TextBackdrop)
        {
            UpdateBackdrops();
            drawingContext.DrawDrawing(backdropScene);
            if (StatusIcon is not null) drawingContext.DrawDrawing(statusDrawing);
        }
        if (AdaptiveForeground is { } foreground)
        {
            drawingContext.DrawDrawing(keylineScene);
            var area = new Rect(new Point(), RenderSize);
            var mask = new DrawingBrush(scene) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, ViewportUnits = BrushMappingMode.Absolute, Viewport = area, Stretch = Stretch.Fill };
            drawingContext.PushOpacityMask(mask);
            drawingContext.DrawRectangle(foreground, null, area);
            drawingContext.Pop();
        }
        else drawingContext.DrawDrawing(scene);
        if(Rolling)drawingContext.Pop();
    }
}
