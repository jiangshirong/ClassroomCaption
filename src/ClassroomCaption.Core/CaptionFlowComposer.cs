using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassroomCaption.Core;

// A bounded rendering window only. The complete transcript stays in the engine/session store.
public static class CaptionFlowComposer
{
    public static CaptionPage Compose(CaptionState state,int russianLimit=600,int chineseLimit=400)
    {
        var segments=state.Confirmed.Concat(state.Interrupted ?? []).OrderBy(s=>s.At).Concat(state.Drafts).TakeLast(12).ToArray();
        // Recognition/translation segment boundaries are not visual line breaks.
        // Let the text formatter wrap both languages at the available width.
        var russian=string.Join(" ",segments.Select(s=>s.Russian).Append(state.UntranslatedRussian).Where(s=>!string.IsNullOrWhiteSpace(s)));
        russian=Regex.Replace(russian,@"\s+"," ").Trim();
        var chinese=string.Concat(segments.Select(s=>Regex.Replace(s.Chinese,@"[ \t]*[\r\n]+[ \t]*","").Trim()));
        return new(Tail(russian,Math.Clamp(russianLimit,1,6000)),Tail(chinese,Math.Clamp(chineseLimit,1,4000)));
    }
    private static string Tail(string text,int limit)
    {
        var offsets=StringInfo.ParseCombiningCharacters(text);
        if(offsets.Length<=limit)return text;
        return text[offsets[^limit]..];
    }
}
