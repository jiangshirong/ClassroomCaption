using System.Net.WebSockets;
using System.Text.Json;

namespace ClassroomCaption.Core;

public static class LiveTranslateProtocol
{
    public const string Model = "qwen3.8-livetranslate-flash-realtime";
    public static Dictionary<string,string> Terms(string text)
    {
        var result = new Dictionary<string,string>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(['=', '＝'], 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) throw new UserFacingException("术语请按“俄语 = 中文译法”填写；不需要术语时可留空。");
            result[parts[0]] = parts[1];
        }
        if(result.Count > 1000) throw new UserFacingException("术语请控制在 1000 条以内。");
        return result;
    }
    public static Uri Endpoint(AppSettings s)
    {
        if(string.IsNullOrWhiteSpace(s.WorkspaceId)) throw new UserFacingException("请在高级连接设置中填写 LiveTranslate 对应地域的 Workspace ID。");
        var baseUri = s.GetRealtimeUri();
        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/api-ws/v1/realtime?model=" + Model);
    }
    public static byte[] Configure(string terms) => JsonSerializer.SerializeToUtf8Bytes(new { type = "session.update", session = new {
        output_modalities = new[]{"text"}, audio = new { input = new { format = new { type="pcm", sample_rate=16000 } } },
        translation = new { language="zh", corpus=new { phrases=CourseTerminology.Merge(terms) } } } });
}

public sealed class LiveTranslateState
{
    private sealed class Item { public long Id; public string Ru="", Zh=""; public bool RuDone, ZhDone, Saved, Interrupted; public string LoggedRu="", LoggedZh=""; public DateTimeOffset At=DateTimeOffset.UtcNow; public string CleanRu="",CleanZh=""; }
    private readonly Dictionary<string,Item> items=[];
    private readonly Dictionary<string,string> links=[];
    private readonly Dictionary<string,(string Text,bool Done)> pending=[];
    private readonly HashSet<Item> touched=[];
    private CaptionState? cachedSnapshot;
    private readonly HashSet<string> events=[];
    private readonly HashSet<string> retired=[];
    private readonly Queue<string> retiredOrder=[];
    private readonly object gate=new(); private long sequence, revision, connection;
    public event Action<CaptionSegment>? Committed;
    public event Action<CaptionSegment>? Archived;
    public event Action<string,string>? InternalMarkupObserved;
    private string Key(string id)=>id.Length==0?"":connection+":"+id;
    public void InterruptConnection()
    {
        lock(gate)
        {
            foreach(var item in items.Values.Where(i=>!i.Saved))
            {
                item.Interrupted=true;item.Saved=true;
                if(item.Ru.Length>0||item.Zh.Length>0)Archived?.Invoke(Segment(item));
            }
            // A reconnect creates a new server session, with its own item/event IDs.
            connection++;links.Clear();pending.Clear();events.Clear();retired.Clear();retiredOrder.Clear();revision++;
        }
    }
    public void Accept(JsonElement e)
    {
        lock(gate)
        {
            string S(JsonElement x,string name)=>x.TryGetProperty(name,out var v)?v.GetString()??"":"";
            var eventId=S(e,"event_id"); if(eventId.Length>0&&!events.Add(eventId)) return;
            if(events.Count>20000) events.Clear();
            var type=S(e,"type"); var id=Key(S(e,"item_id"));
            if(type is not ("conversation.item.created" or "conversation.item.input_audio_transcription.delta" or "conversation.item.input_audio_transcription.completed" or "response.text.delta" or "response.text.done" or "response.done"))return;
            touched.Clear();
            cachedSnapshot=null;
            if(id.Length>0&&retired.Contains(id))return;
            if(type=="conversation.item.created")
            { var item=e.GetProperty("item"); if(S(item,"role")=="assistant") { var target=Key(S(e,"previous_item_id")); var translationId=Key(S(item,"id")); if(retired.Contains(target)||retired.Contains(translationId)){pending.Remove(translationId);Retire(translationId);return;} if(target.Length>0){links[translationId]=target; if(pending.Remove(translationId,out var p)){Get(target).Zh=p.Text;Get(target).ZhDone=p.Done;}} } }
            else if(type=="conversation.item.input_audio_transcription.delta") Get(id).Ru+=S(e,"delta");
            else if(type=="conversation.item.input_audio_transcription.completed") {Get(id).Ru=S(e,"transcript");Get(id).RuDone=true;}
            else if(type is "response.text.delta" or "response.text.done")
            { var done=type.EndsWith("done"); if(links.TryGetValue(id,out var target)){var item=Get(target);item.Zh=done?S(e,"text"):item.Zh+S(e,"delta");/* text.done may also be interrupted; response.done confirms below */}
              else {pending.TryGetValue(id,out var old);pending[id]=(done?S(e,"text"):old.Text+S(e,"delta"),false);} }
            else if(type=="response.done")
            { var response=e.GetProperty("response"); if(S(response,"status")=="completed"&&response.TryGetProperty("output",out var output))foreach(var item in output.EnumerateArray()){if(links.TryGetValue(Key(S(item,"id")),out var target))Get(target).ZhDone=true;else if(pending.TryGetValue(Key(S(item,"id")),out var p))pending[Key(S(item,"id"))]=(p.Text,true);} }
            foreach(var item in touched)
            {
                item.CleanRu=SubtitleText.Clean(item.Ru);item.CleanZh=SubtitleText.Clean(item.Zh);
                if(item.Ru!=item.LoggedRu&&item.CleanRu!=item.Ru){item.LoggedRu=item.Ru;InternalMarkupObserved?.Invoke("source",item.Ru);}
                if(item.Zh!=item.LoggedZh&&item.CleanZh!=item.Zh){item.LoggedZh=item.Zh;InternalMarkupObserved?.Invoke("translation",item.Zh);}
            }
            foreach(var item in touched) if(item.RuDone&&item.ZhDone&&!item.Saved&&!string.IsNullOrWhiteSpace(item.Ru)&&!string.IsNullOrWhiteSpace(item.Zh)){item.Saved=true;Committed?.Invoke(Segment(item));}
            // This is a display-history limit, never a limit on classroom length.
            // An old incomplete/empty source must not prevent every later item from retiring.
            while(items.Count>200)
            {
                var first=items.MinBy(x=>x.Value.Id);
                if(!first.Value.Saved&&(first.Value.Ru.Length>0||first.Value.Zh.Length>0))Archived?.Invoke(Segment(first.Value));
                items.Remove(first.Key);Retire(first.Key);
                foreach(var key in links.Where(x=>x.Value==first.Key).Select(x=>x.Key).ToArray()){links.Remove(key);pending.Remove(key);Retire(key);}
            }
            while(pending.Count>200){var first=pending.First();pending.Remove(first.Key);Retire(first.Key);}
            while(links.Count>400){var first=links.First();links.Remove(first.Key);Retire(first.Key);}
            revision++;
        }
    }
    private void Retire(string id)
    {
        if(!retired.Add(id))return;
        retiredOrder.Enqueue(id);
        while(retiredOrder.Count>2048)retired.Remove(retiredOrder.Dequeue());
    }
    private Item Get(string key){if(!items.TryGetValue(key,out var item))items[key]=item=new(){Id=++sequence};touched.Add(item);return item;}
    private static CaptionSegment Segment(Item i)=>new(i.Id,i.Ru,i.Zh,i.Saved&&!i.Interrupted,i.Saved&&!i.Interrupted,i.At,[]);
    private static CaptionSegment DisplaySegment(Item i)=>Segment(i) with {Russian=i.CleanRu,Chinese=i.CleanZh};
    public CaptionState Snapshot()
    {
        lock(gate)
        {
            if(cachedSnapshot?.Revision==revision)return cachedSnapshot;
            var all=items.Values.OrderBy(x=>x.Id).ToArray();
            return cachedSnapshot=new(all.Where(i=>i.Saved&&!i.Interrupted).Select(DisplaySegment).ToArray(),
                all.Where(i=>!i.Saved&&i.Zh.Length>0).Select(DisplaySegment).ToArray(),
                string.Join(" ",all.Where(i=>!i.Saved&&i.Zh.Length==0).Select(i=>i.CleanRu)),0,revision,
                all.Where(i=>i.Interrupted).Select(DisplaySegment).ToArray());
        }
    }
}
