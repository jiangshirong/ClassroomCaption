using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassroomCaption.App;

internal static class AudioInputNames
{
    private static string Clean(string name) => Regex.Replace(
        Regex.Replace(name,@"[®™]|\((?:R|TM)\)","",RegexOptions.IgnoreCase),@"\s+"," ").Trim();

    private static (string Name,string Detail) Split(string name)
    {
        var clean=Clean(name);
        var match=Regex.Match(clean,@"^(.+?)\s*[（(](.+)[）)]\s*$");
        return match.Success?(match.Groups[1].Value.Trim(),match.Groups[2].Value.Trim()):(clean,"");
    }

    public static string ShortName(string name) => Split(name).Name;

    private static string Detail(string name)
    {
        var detail=Split(name).Detail;
        if(Regex.IsMatch(detail,@"英特尔|Intel",RegexOptions.IgnoreCase))return "Intel";
        if(Regex.IsMatch(detail,@"Realtek",RegexOptions.IgnoreCase))return "Realtek";
        detail=Regex.Replace(detail,@"\bUSB (?:PnP )?(?:Audio|Sound) Device\b","USB",RegexOptions.IgnoreCase);
        detail=Regex.Replace(detail,@"\s+(?:Hands-Free AG Audio|Audio|音频)$","",RegexOptions.IgnoreCase).Trim();
        var offsets=StringInfo.ParseCombiningCharacters(detail);
        return offsets.Length>22?detail[..offsets[22]]+"…":detail;
    }

    public static IReadOnlyList<AudioInput> Distinguish(IReadOnlyList<AudioInput> inputs)
    {
        var result=inputs.Select(input=>input with {DisplayName=ShortName(input.Name)}).ToArray();
        foreach(var group in Enumerable.Range(0,result.Length).GroupBy(i=>result[i].DisplayName,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))
        {
            foreach(var index in group.ToArray())
            {
                var detail=Detail(result[index].Name);
                if(detail.Length>0)result[index]=result[index] with {DisplayName=result[index].DisplayName+" · "+detail};
            }
        }
        foreach(var group in Enumerable.Range(0,result.Length).GroupBy(i=>result[i].DisplayName,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))
        {
            var number=0;
            foreach(var index in group.OrderBy(i=>result[i].Id,StringComparer.Ordinal).ToArray())
                result[index]=result[index] with {DisplayName=result[index].DisplayName+" · "+ ++number};
        }
        return result;
    }
}
