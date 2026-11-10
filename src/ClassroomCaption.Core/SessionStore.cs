using System.Text;
using System.Text.Json;

namespace ClassroomCaption.Core;

public sealed record SavedCaption(string Label, string Russian, string Chinese);

public sealed class SessionStore : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly StreamWriter readable;
    public string ReadablePath=>Path.ChangeExtension(FilePath,"txt");
    private long lastRevision = -1;
    private bool failed;
    private readonly Dictionary<long, CaptionSegment> previousDrafts = [];
    private string previousSource = "";
    public bool IsHealthy => !failed;
    public string FilePath { get; }
    public event Action<string>? Failed;
    public SessionStore(string directory, bool demo = false)
    {
        SettingsStore.CheckWritable(directory);
        FilePath = Path.Combine(directory, $"{(demo ? "演示-" : "课堂-")}{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
        writer = new(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        try{readable = new(new FileStream(ReadablePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),new UTF8Encoding(true)){AutoFlush=true};}
        catch{writer.Dispose();throw;}
        Write(new { type = "session", version = 1, at = DateTimeOffset.Now, language = "ru", demo });
        if (failed) { writer.Dispose();readable.Dispose(); throw new UserFacingException("无法写入新的课堂记录，请选择可写目录后再开始。"); }
    }
    public void SaveCommitted(CaptionSegment segment) {Write(new { type = "segment", segment.Id, segment.Russian, segment.Chinese, confirmed = true, segment.At });Readable("已确认",segment.Russian,segment.Chinese);}
    public void SaveArchived(CaptionSegment segment) {Write(new { type = "interrupted", segment.Id, segment.Russian, segment.Chinese, confirmed = false, segment.At });Readable("未确认片段",segment.Russian,segment.Chinese);}
    private void Readable(string state,string ru,string zh)
    {
        lock(gate)
        {
            if(failed)return;
            try{readable.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · {state}]\n原文：{SubtitleText.Clean(ru)}\n译文：{SubtitleText.Clean(zh)}\n");}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failed=true;Failed?.Invoke("课堂可读文字保存失败，请检查保存目录。");}
        }
    }
    public void RawMarker(string field,string text)=>Write(new {type="internal_marker",at=DateTimeOffset.Now,field,text});
    public void Trace(TranslationTrace trace)=>Write(new {type=trace.MarkerText is null?"provider_trace":"provider_marker",at=DateTimeOffset.Now,trace},trace.MarkerText is not null||trace.EventType is "session.created" or "session.updated" or "session.finished" or "response.done" or "error");
    public void SaveDraft(CaptionState state)
    {
        lock (gate)
        {
            if (state.Revision <= lastRevision) return;
            lastRevision = state.Revision;
            var ids = state.Drafts.Select(s => s.Id).ToHashSet();
            var removed = previousDrafts.Keys.Where(id => !ids.Contains(id)).ToArray();
            var upserts = state.Drafts.Where(s => !previousDrafts.TryGetValue(s.Id, out var old) || old.Russian!=s.Russian||old.Chinese!=s.Chinese).Select(s => new { s.Id, s.Russian, s.Chinese, confirmed = false }).ToArray();
            if(removed.Length==0&&upserts.Length==0&&previousSource==state.UntranslatedRussian)return;
            foreach(var s in upserts)Readable("暂译更新",s.Russian,s.Chinese);
            var common = 0;
            while (common < previousSource.Length && common < state.UntranslatedRussian.Length && previousSource[common] == state.UntranslatedRussian[common]) common++;
            if(previousSource!=state.UntranslatedRussian&&!string.IsNullOrWhiteSpace(state.UntranslatedRussian))Readable("原文草稿，尚未翻译",state.UntranslatedRussian,"");
            Write(new { type = "draft_patch", revision = state.Revision, at = DateTimeOffset.Now, removed, segments = upserts, sourceKeep = common, sourceAppend = state.UntranslatedRussian[common..] });
            previousDrafts.Clear(); foreach (var s in state.Drafts) previousDrafts[s.Id] = s;
            previousSource = state.UntranslatedRussian;
        }
    }
    public void Gap(string description) { Write(new { type = "gap", at = DateTimeOffset.Now, description }); Readable("音频缺口", "", description); }
    public void Metrics(object metrics) => Write(new { type = "metrics", at = DateTimeOffset.Now, metrics });
    public void TokenUsage(TranslationUsage usage) => Write(new { type = "token_usage", at = DateTimeOffset.Now,
        responseId = usage.ResponseId, audioInputTokens = usage.AudioInputTokens,
        chineseOutputTokens = usage.ChineseOutputTokens, connection = usage.Connection, source = "vendor" });
    public void Status(string code, string description) => Write(new { type = "status", at = DateTimeOffset.Now, code, description });
    private void Write(object value,bool flush=true)
    {
        lock (gate)
        {
            if (failed) return;
            try { writer.WriteLine(JsonSerializer.Serialize(value)); if(flush)writer.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; Failed?.Invoke("课堂文字保存失败，请停止后选择可写目录；内存中的字幕仍可导出。"); }
        }
    }
    public void Dispose() { lock (gate) { try { writer.Dispose(); } catch (IOException) { } try{readable.Dispose();}catch(IOException){} } }

    public static void ExportText(string jsonlPath, string outputPath, CaptionState? memoryState = null) => AtomicFile.Write(outputPath, ReadText(jsonlPath, memoryState));

    public static string ReadText(string jsonlPath, CaptionState? memoryState = null)
    {
        var text = new StringBuilder();
        foreach (var row in ReadEntries(jsonlPath, memoryState))
        {
            if (!string.IsNullOrEmpty(row.Label)) text.AppendLine("[" + row.Label + "]");
            if (!string.IsNullOrEmpty(row.Russian)) text.AppendLine(row.Russian);
            if (!string.IsNullOrEmpty(row.Chinese)) text.AppendLine(row.Chinese);
            text.AppendLine();
        }
        return text.ToString();
    }
    public static IReadOnlyList<SavedCaption> ReadEntries(string jsonlPath, CaptionState? memoryState = null)
    {
        var entries = new List<SavedCaption>();
        var history = new Dictionary<long, (string Label, string Ru, string Zh)>();
        var languageAnomalies = new Dictionary<string, string>();
        string source = "";
        var drafts = new Dictionary<long, (string Ru, string Zh)>();
        Stream input;
        try { input = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (memoryState is not null && ex is IOException or UnauthorizedAccessException)
        {
            input = new MemoryStream();
            entries.Add(new("原保存目录暂时无法读取；以下为内存中最近保留的内容，较早历史需恢复原文件后导出。", "", ""));
        }
        using var reader = new StreamReader(input, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) when (reader.EndOfStream) { break; } // A crash can leave one incomplete final record.
            using var doc = document;
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "asr_language_anomaly":
                    var anomaly = root.GetProperty("sentence");
                    languageAnomalies[anomaly.GetProperty("Key").GetString() ?? ""] = anomaly.GetProperty("Text").GetString() ?? "";
                    break;
                case "segment":
                    history[root.GetProperty("Id").GetInt64()] = ("已确认", root.GetProperty("Russian").GetString() ?? "", root.GetProperty("Chinese").GetString() ?? "");
                    drafts.Remove(root.GetProperty("Id").GetInt64());
                    break;
                case "interrupted":
                    history[root.GetProperty("Id").GetInt64()] = ("中断暂存，未确认", root.GetProperty("Russian").GetString() ?? "", root.GetProperty("Chinese").GetString() ?? "");
                    drafts.Remove(root.GetProperty("Id").GetInt64());
                    break;
                case "draft":
                    drafts.Clear();
                    foreach (var s in root.GetProperty("segments").EnumerateArray()) drafts[s.GetProperty("Id").GetInt64()] = (s.GetProperty("Russian").GetString() ?? "", s.GetProperty("Chinese").GetString() ?? "");
                    source = root.GetProperty("untranslatedRussian").GetString() ?? ""; break;
                case "draft_patch":
                    foreach (var id in root.GetProperty("removed").EnumerateArray()) drafts.Remove(id.GetInt64());
                    foreach (var s in root.GetProperty("segments").EnumerateArray()) drafts[s.GetProperty("Id").GetInt64()] = (s.GetProperty("Russian").GetString() ?? "", s.GetProperty("Chinese").GetString() ?? "");
                    var keep = root.GetProperty("sourceKeep").GetInt32();
                    if (keep < 0 || keep > source.Length) throw new UserFacingException("课堂记录中的草稿补丁损坏，无法完整导出。");
                    source = source[..keep] + root.GetProperty("sourceAppend").GetString(); break;
                case "gap": entries.Add(new("音频缺口", "", root.GetProperty("description").GetString() ?? "")); break;
            }
        }
        if (memoryState is not null)
        {
            foreach (var segment in memoryState.Confirmed.Concat(memoryState.Interrupted ?? []).OrderBy(s => s.At)) history[segment.Id] = (segment.Confirmed ? "已确认" : "中断暂存，未确认", segment.Russian, segment.Chinese);
            drafts.Clear(); foreach (var segment in memoryState.Drafts) drafts[segment.Id] = (segment.Russian, segment.Chinese);
            source = memoryState.UntranslatedRussian;
        }
        foreach (var segment in history.Values) entries.Add(new(segment.Label, segment.Ru, segment.Zh));
        foreach (var draft in drafts.Values) entries.Add(new("暂译", draft.Ru, draft.Zh));
        if (!string.IsNullOrWhiteSpace(source)) entries.Add(new("俄语草稿，尚未翻译", source, ""));
        foreach (var anomaly in languageAnomalies.Values) entries.Add(new("ASR 语种异常原始返回，未作为俄语翻译；后续可能已纠正", anomaly, ""));
        return entries.Select(e=>e with {Russian=SubtitleText.Clean(e.Russian),Chinese=SubtitleText.Clean(e.Chinese)}).ToArray();
    }
}
