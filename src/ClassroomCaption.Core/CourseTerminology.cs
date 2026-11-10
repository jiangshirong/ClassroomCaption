using System.Text.Json;

namespace ClassroomCaption.Core;

public static class CourseTerminology
{
    private sealed record Entry(string Source,string Target);
    private static readonly Lazy<IReadOnlyDictionary<string,string>> terms=new(()=>
    {
        using var resource=typeof(CourseTerminology).Assembly.GetManifestResourceStream("ClassroomCaption.ShipMotionTerms.json")!;
        var entries=JsonSerializer.Deserialize<Entry[]>(resource,new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
        return entries.ToDictionary(e=>e.Source,e=>e.Target,StringComparer.OrdinalIgnoreCase);
    });
    public static IReadOnlyDictionary<string,string> Terms=>terms.Value;
    public static Dictionary<string,string> Merge(string custom)
    {
        var result=new Dictionary<string,string>(Terms,StringComparer.OrdinalIgnoreCase);
        foreach(var (source,target) in LiveTranslateProtocol.Terms(custom))result[source]=target;
        if(result.Count>1000)throw new UserFacingException("合并后的术语超过 1000 条，请精简自定义配置。");
        return result;
    }
}
