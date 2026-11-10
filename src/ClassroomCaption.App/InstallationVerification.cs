using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

// Offscreen package smoke test. No microphone capture, screen capture or model calls.
internal static class InstallationVerification
{
    public static int Run(string dataRoot, string report)
    {
        try
        {
            var store = new SettingsStore(dataRoot);
            SettingsStore.CheckWritable(store.Root);
            InitialConfiguration.ApplyEmbedded(store);
            var probe=ProbeDeployment.Ensure(store.Root);
            if(!File.Exists(probe))throw new InvalidOperationException("Embedded probe extraction failed.");
            var window = new MainWindow(store);
            var content = (FrameworkElement)window.Content;
            var overlay=new OverlayWindow(new());
            var chinese=(OutlinedText)overlay.FindName("ChineseCaption");
            overlay.SetCaption(new([new(1,"Добрый день",@"船舶课堂字幕：幅值比 $\frac{x}{y}$",true,true,DateTimeOffset.Now,[])],[],"",0,1),SubtitleActivity.Stopped);
            content.Measure(new Size(1000, 690)); content.Arrange(new Rect(0, 0, 1000, 690)); content.UpdateLayout();
            new RenderTargetBitmap(1000, 690, 96, 96, PixelFormats.Pbgra32).Render(content);
            var captions=(FrameworkElement)overlay.Content;
            captions.Measure(new Size(1000,690));captions.Arrange(new Rect(0,0,1000,690));captions.UpdateLayout();
            new RenderTargetBitmap(1000,690,96,96,PixelFormats.Pbgra32).Render(captions);
            if(chinese.RenderedFormulaCount!=1)throw new InvalidOperationException("Formula renderer not loaded.");
            var typeface = new Typeface(CaptionTypography.Chinese, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var font) || !font.CharacterToGlyphMap.ContainsKey('船')) throw new InvalidOperationException("Bundled font not loaded.");
            using var noise = new RnNoiseFilter();
            noise.Process(new float[480], new float[480]);
            var settings = store.Load();
            AtomicFile.Write(report, JsonSerializer.Serialize(new {
                passed = true, probeExtracted = true, wpfRendered = true, formulasRendered = chinese.RenderedFormulaCount, courseTerms = CourseTerminology.Terms.Count, embeddedChineseFontLoaded = true, nativeNoiseReductionLoaded = true,
                apiKeyConfigured = store.LoadCredentials()?.Complete == true,
                workspaceConfigured = !string.IsNullOrWhiteSpace(settings.WorkspaceId),
                sourceModel = settings.LiveTranslateModel, microphoneCaptured = false, networkRequests = false
                ,launchDirectory=App.LaunchDirectory,bundleExtractDirectory=Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
            }));
            return 0;
        }
        catch (Exception ex)
        {
            AtomicFile.Write(report, JsonSerializer.Serialize(new { passed = false, errorType = ex.GetType().Name }));
            return 1;
        }
    }
}
