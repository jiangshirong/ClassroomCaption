using System.Text.RegularExpressions;

namespace ClassroomCaption.Core;

public static partial class SubtitleText
{
    [GeneratedRegex(@"<\|[^\r\n]*?\|>")]
    private static partial Regex CompleteMarker();
    [GeneratedRegex(@"<\|[^\r\n]*$")]
    private static partial Regex PartialMarker();
    [GeneratedRegex(@"</?(?:title|think|analysis|final|assistant|readable_unit_tag)\s*>",RegexOptions.IgnoreCase)]
    private static partial Regex CompleteTag();
    [GeneratedRegex(@"</?(?:title|think|analysis|final|assistant|readable_unit_tag)?$",RegexOptions.IgnoreCase)]
    private static partial Regex PartialTag();
    public static string Clean(string text)
    {
        if(!text.Contains('<'))return text;
        text=CompleteMarker().Replace(text,"");
        text=PartialMarker().Replace(text,"");
        text=CompleteTag().Replace(text,"");
        text=PartialTag().Replace(text,"");
        return text;
    }
    public static CaptionSegment Clean(CaptionSegment s)
    {
        var ru=Clean(s.Russian);var zh=Clean(s.Chinese);
        return ReferenceEquals(ru,s.Russian)&&ReferenceEquals(zh,s.Chinese)?s:s with {Russian=ru,Chinese=zh};
    }
}
