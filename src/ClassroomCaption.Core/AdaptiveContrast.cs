namespace ClassroomCaption.Core;

public readonly record struct AdaptiveInk(byte Red, byte Green, byte Blue)
{
    public double Luminance => AdaptiveContrast.Luminance(Red, Green, Blue);
}
public enum CaptionInkRole { Chinese, Russian }

public static class AdaptiveContrast
{
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        var value = i / 255.0;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }).ToArray();
    private readonly record struct Candidate(AdaptiveInk Ink, double Light);
    private sealed record Ramp(AdaptiveInk[] Dark, AdaptiveInk[] Light);
    private static readonly Lazy<int[]> HueBins = new(BuildHueBins);
    private static readonly Lazy<Ramp[]> ChineseRamps = new(() => BuildRamps(240));
    private static readonly Lazy<Ramp[]> RussianRamps = new(() => BuildRamps(80));
    public static double Luminance(byte red, byte green, byte blue) => .2126 * Linear[red] + .7152 * Linear[green] + .0722 * Linear[blue];
    public static double Ratio(double a, double b) => (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    public static bool UseBlack(byte red, byte green, byte blue) => Luminance(red,green,blue) >= .1791287847;
    private static int[] BuildHueBins()
    {
        var bins=new int[32768];
        for(var r=0;r<32;r++) for(var g=0;g<32;g++) for(var b=0;b<32;b++)
        {
            var red=Linear[Math.Min(255,r*8+4)];var green=Linear[Math.Min(255,g*8+4)];var blue=Linear[Math.Min(255,b*8+4)];
            // Oklab conversion: Björn Ottosson, https://bottosson.github.io/posts/oklab/ (public-domain formulas).
            var l=Math.Cbrt(.4122214708*red+.5363325363*green+.0514459929*blue);
            var m=Math.Cbrt(.2119034982*red+.6806995451*green+.1073969566*blue);
            var s=Math.Cbrt(.0883024619*red+.2817188376*green+.6299787005*blue);
            var a=1.9779984951*l-2.428592205*m+.4505937099*s;
            var c=.0259040371*l+.7827717662*m-.808675766*s;
            bins[(r<<10)|(g<<5)|b]=a*a+c*c<.000625 ? 64 : ((int)Math.Round((Math.Atan2(c,a)*180/Math.PI+540)/360*64))%64;
        }
        return bins;
    }
    private static Ramp[] BuildRamps(double accent)
    {
        var ramps=new Ramp[65];
        for(var hue=0;hue<65;hue++)
        {
            var angle=hue==64 ? accent : hue*360.0/64;
            if(hue!=64) { var delta=(accent-angle+540)%360-180;angle+=delta*.2; }
            var dark=new List<Candidate>();var light=new List<Candidate>();
            for(var step=0;step<=64;step++)
            {
                var t=step/64.0;
                var d=FromOklch(.48*t,.13*t,angle); dark.Add(new(d,d.Luminance));
                var v=FromOklch(.88+.12*t,.075*(1-t),angle);light.Add(new(v,v.Luminance));
            }
            dark.Add(new(new(0,0,0),0));light.Add(new(new(255,255,255),1));
            var sortedDark=dark.OrderBy(c=>c.Light).ToArray();var sortedLight=light.OrderBy(c=>c.Light).ToArray();
            var darkLookup=new AdaptiveInk[1024];var lightLookup=new AdaptiveInk[1024];
            var darkIndex=0;var lightIndex=0;
            for(var bin=0;bin<1024;bin++)
            {
                // Preserve at least 90% of the stronger black/white contrast, and never
                // sacrifice the 4.5:1 floor for hue. Neutral slides use exact black/white.
                var lower=bin/1024.0;var upper=(bin+1)/1024.0;
                var maximum=Math.Min(.05/.90-.05,(lower+.05)/4.5-.05);
                var minimum=Math.Max(.90*1.05-.05,4.5*(upper+.05)-.05);
                while(darkIndex+1<sortedDark.Length&&sortedDark[darkIndex+1].Light<=maximum)darkIndex++;
                while(lightIndex+1<sortedLight.Length&&sortedLight[lightIndex].Light<minimum)lightIndex++;
                darkLookup[bin]=hue==64 ? new(0,0,0) : sortedDark[darkIndex].Ink;
                lightLookup[bin]=hue==64 ? new(255,255,255) : sortedLight[lightIndex].Ink;
            }
            ramps[hue]=new(darkLookup,lightLookup);
        }
        return ramps;
    }
    private static AdaptiveInk FromOklch(double light,double chroma,double hue)
    {
        var angle=hue*Math.PI/180;var a=chroma*Math.Cos(angle);var b=chroma*Math.Sin(angle);
        var l=light+.3963377774*a+.2158037573*b;var m=light-.1055613458*a-.0638541728*b;var s=light-.0894841775*a-1.291485548*b;
        l=l*l*l;m=m*m*m;s=s*s*s;
        static byte Encode(double value) { value=Math.Clamp(value,0,1); return (byte)Math.Clamp((int)Math.Round(255*(value<=.0031308 ? value*12.92 : 1.055*Math.Pow(value,1/2.4)-.055)),0,255); }
        return new(Encode(4.0767416621*l-3.3077115913*m+.2309699292*s),Encode(-1.2684380046*l+2.6097574011*m-.3413193965*s),Encode(-.0041960863*l-.7034186147*m+1.707614701*s));
    }
    public static AdaptiveInk Select(byte red,byte green,byte blue,CaptionInkRole role=CaptionInkRole.Chinese)
    {
        var luminance=Luminance(red,green,blue);
        var bin=HueBins.Value[((red>>3)<<10)|((green>>3)<<5)|(blue>>3)];
        var ramp=(role==CaptionInkRole.Chinese ? ChineseRamps.Value : RussianRamps.Value)[bin];
        var lightBin=Math.Clamp((int)(luminance*1024),0,1023);
        return luminance>=.1791287847 ? ramp.Dark[lightBin] : ramp.Light[lightBin];
    }
    public static void MapPair(ReadOnlySpan<byte> source,Span<byte> chinese,Span<byte> russian)
    {
        if(source.Length%4!=0||chinese.Length!=source.Length||russian.Length!=source.Length)throw new ArgumentException("Invalid BGRA pixel data.");
        var hues=HueBins.Value;var cool=ChineseRamps.Value;var warm=RussianRamps.Value;
        for(var i=0;i<source.Length;i+=4)
        {
            var red=(byte)((source[i+2]*254+17)/255);var green=(byte)((source[i+1]*254+24)/255);var blue=(byte)((source[i]*254+39)/255);
            var luminance=Luminance(red,green,blue);var bin=Math.Clamp((int)(luminance*1024),0,1023);
            var hue=hues[((red>>3)<<10)|((green>>3)<<5)|(blue>>3)];
            var dark=luminance>=.1791287847;var c=dark?cool[hue].Dark[bin]:cool[hue].Light[bin];var r=dark?warm[hue].Dark[bin]:warm[hue].Light[bin];
            chinese[i]=c.Blue;chinese[i+1]=c.Green;chinese[i+2]=c.Red;chinese[i+3]=255;
            russian[i]=r.Blue;russian[i+1]=r.Green;russian[i+2]=r.Red;russian[i+3]=255;
        }
    }
    public static byte[] MapBgra(ReadOnlySpan<byte> source,CaptionInkRole role=CaptionInkRole.Chinese)
    {
        var output=new byte[source.Length];MapBgra(source,output,role);return output;
    }
    public static void MapBgra(ReadOnlySpan<byte> source,Span<byte> output,CaptionInkRole role=CaptionInkRole.Chinese)
    {
        if(source.Length%4!=0||output.Length!=source.Length)throw new ArgumentException("Invalid BGRA pixel data.");
        // These palettes are generated once, not recomputed with trigonometry for every screen pixel.
        _=HueBins.Value;_=ChineseRamps.Value;_=RussianRamps.Value;
        for(var i=0;i<source.Length;i+=4)
        {
            var ink=Select((byte)((source[i+2]*254+17)/255),(byte)((source[i+1]*254+24)/255),(byte)((source[i]*254+39)/255),role);
            output[i]=ink.Blue;output[i+1]=ink.Green;output[i+2]=ink.Red;output[i+3]=255;
        }
    }
}
