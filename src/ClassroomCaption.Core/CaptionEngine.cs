using System.Text.RegularExpressions;

namespace ClassroomCaption.Core;

public sealed record SourceCaptionUpdate(string Key, string Text, bool Final, long BeginMilliseconds = 0, long? EndMilliseconds = null);
public sealed record SourceToken(long Id, string Text, bool SourceFinal);
public sealed record TranslationPart(int Start, int End, string Chinese, bool Complete);
public sealed record TranslationSnapshot(long RequestId, IReadOnlyList<SourceToken> Tokens, IReadOnlyList<CaptionSegment> Context, DateTimeOffset CreatedAt)
{
    public string Russian => string.Join(" ", Tokens.Select(t => t.Text));
    public string Signature => string.Join("|", Tokens.Select(t => $"{t.Id}:{t.SourceFinal}"));
}
public sealed record CaptionSegment(long Id, string Russian, string Chinese, bool Confirmed, bool Complete, DateTimeOffset At, IReadOnlyList<long> TokenIds);
public sealed record CaptionState(IReadOnlyList<CaptionSegment> Confirmed, IReadOnlyList<CaptionSegment> Drafts, string UntranslatedRussian, int PendingWords, long Revision, IReadOnlyList<CaptionSegment>? Interrupted = null);

/// <summary>Token identities survive ASR prefix reuse. Only an unchanged, ordered source span can accept a translation.</summary>
public sealed class CaptionEngine
{
    public Func<CaptionState>? LiveState { get; set; }
    private sealed class Word(long id, string text) { public long Id = id; public string Text = text; public bool Confirmed; public bool Final; public bool? TranslatedFinal; }
    private sealed class Sentence { public List<Word> Words = []; public bool Final; }
    private readonly object gate = new();
    private readonly Dictionary<string, Sentence> sentences = [];
    private readonly List<string> order = [];
    private readonly HashSet<string> retired = [];
    private readonly Queue<string> retiredOrder = new();
    private readonly List<CaptionSegment> confirmed = [];
    private readonly List<CaptionSegment> drafts = [];
    private readonly List<CaptionSegment> interrupted = [];
    private long nextToken, nextRequest, nextSegment, revision, appliedRequest;
    public string? BoundaryWarning { get; private set; }
    public event Action<CaptionSegment>? Committed;
    public event Action<CaptionSegment>? Archived;

    public void Update(SourceCaptionUpdate update)
    {
        lock (gate)
        {
            if (retired.Contains(update.Key)) return;
            if (!sentences.TryGetValue(update.Key, out var sentence)) { sentences[update.Key] = sentence = new(); order.Add(update.Key); }
            var words = Regex.Matches(update.Text.Trim(), @"\S+").Select(m => m.Value).ToArray();
            var common = 0;
            while (common < words.Length && common < sentence.Words.Count && words[common] == sentence.Words[common].Text) common++;
            if (sentence.Words.Skip(common).Any(w => w.Confirmed)) { BoundaryWarning = "ASR 尝试改写已确认内容；已保留原记录，请留意该处。"; return; }
            var changed = common != words.Length || common != sentence.Words.Count || update.Final != sentence.Final;
            if (!changed) return;
            sentence.Words.RemoveRange(common, sentence.Words.Count - common);
            sentence.Words.AddRange(words.Skip(common).Select(w => new Word(++nextToken, w)));
            sentence.Final = update.Final;
            foreach (var word in sentence.Words) word.Final = update.Final;
            var valid = ActiveWords().Select(w => w.Id).ToHashSet();
            drafts.RemoveAll(s => s.TokenIds.Any(id => !valid.Contains(id)));
            // Invalidated draft spans must be translated again, including their unchanged prefix.
            var covered = drafts.SelectMany(s => s.TokenIds).ToHashSet();
            foreach (var word in ActiveWords()) if (!covered.Contains(word.Id)) word.TranslatedFinal = null;
            revision++;
            Promote();
        }
    }

    public TranslationSnapshot? CreateSnapshot(int maximumWords = 120)
    {
        lock (gate)
        {
            var words = ActiveWords().ToList();
            var start = words.FindIndex(w => w.TranslatedFinal is null || w.TranslatedFinal != w.Final);
            if (start < 0) return null;
            // Revisit the immediately preceding provisional phrase so new words can resolve its meaning.
            var previous = drafts.LastOrDefault(s => !s.Complete && s.TokenIds.Count > 0 && words.FindIndex(w => w.Id == s.TokenIds[^1]) < start);
            if (previous is not null)
            {
                var previousStart = words.FindIndex(w => w.Id == previous.TokenIds[0]);
                // Leave room for new words. Repeating a full previous batch can otherwise starve the suffix forever.
                if (previousStart >= 0 && start - previousStart <= Math.Min(8, maximumWords / 3)) start = previousStart;
            }
            var end = Math.Min(words.Count, start + maximumWords);
            // Never cut through an existing draft span: its unselected suffix would otherwise be lost.
            foreach (var draft in drafts)
            {
                var first = words.FindIndex(w => w.Id == draft.TokenIds[0]);
                var last = words.FindIndex(w => w.Id == draft.TokenIds[^1]);
                if (first < end && last >= end) end = first > start ? first : last + 1;
            }
            if (end <= start) return null;
            var context = confirmed.Concat(drafts.Where(s => words.FindIndex(w => w.Id == s.TokenIds[^1]) < start))
                .TakeLast(3).ToArray();
            return new(++nextRequest, words.GetRange(start, end - start).Select(w => new SourceToken(w.Id, w.Text, w.Final)).ToArray(), context, DateTimeOffset.UtcNow);
        }
    }

    public bool Apply(TranslationSnapshot snapshot, IReadOnlyList<TranslationPart> parts)
        => Apply(snapshot, parts, out _);

    public bool Apply(TranslationSnapshot snapshot, IReadOnlyList<TranslationPart> parts, out int appliedWords)
    {
        appliedWords = 0;
        ValidateParts(snapshot, parts);
        lock (gate)
        {
            if (snapshot.RequestId <= appliedRequest) return false;
            var words = ActiveWords().ToList();
            var start = words.FindIndex(w => w.Id == snapshot.Tokens[0].Id);
            if (start < 0) return false;
            var matched = 0;
            while (matched < snapshot.Tokens.Count && start + matched < words.Count &&
                words[start + matched].Id == snapshot.Tokens[matched].Id && words[start + matched].Text == snapshot.Tokens[matched].Text) matched++;
            // A Chinese segment may depend on every source word in that segment; never split its translation.
            // Later segments are also withheld after the first changed range to preserve continuous coverage.
            var validParts = parts.TakeWhile(p => p.End <= matched).ToArray();
            if (validParts.Length == 0) return false;
            var validCount = validParts[^1].End;
            var selected = snapshot.Tokens.Take(validCount).Select(w => w.Id).ToHashSet();
            if (drafts.Any(s => s.TokenIds.Any(selected.Contains) && s.TokenIds.Any(id => !selected.Contains(id)))) return false;
            drafts.RemoveAll(s => s.TokenIds.Any(selected.Contains));
            foreach (var part in validParts)
            {
                var tokens = snapshot.Tokens.Skip(part.Start).Take(part.End - part.Start).ToArray();
                drafts.Add(new(++nextSegment, string.Join(" ", tokens.Select(t => t.Text)), part.Chinese.Trim(), false, part.Complete, DateTimeOffset.UtcNow, tokens.Select(t => t.Id).ToArray()));
            }
            for (var i = 0; i < validCount; i++) words[start + i].TranslatedFinal = snapshot.Tokens[i].SourceFinal;
            appliedWords = validCount;
            appliedRequest = snapshot.RequestId;
            revision++;
            Promote();
            return true;
        }
    }

    public static void ValidateParts(TranslationSnapshot snapshot, IReadOnlyList<TranslationPart> parts)
    {
        if (snapshot.Tokens.Count == 0 || parts.Count is 0 or > 120) throw new UserFacingException("翻译分段为空或过多，已保留俄语原文等待重试。");
        var expected = 0;
        foreach (var part in parts)
        {
            if (part.Start != expected || part.End <= part.Start || part.End > snapshot.Tokens.Count || string.IsNullOrWhiteSpace(part.Chinese) || part.Chinese.Length > 10000)
                throw new UserFacingException("翻译分段不连续、缺失或格式不正确，未覆盖现有字幕。");
            expected = part.End;
        }
        if (expected != snapshot.Tokens.Count) throw new UserFacingException("翻译未覆盖全部原文，已保留原文等待重试。");
    }

    public CaptionState State()
    {
        if (LiveState is not null) return LiveState();
        lock (gate)
        {
            var words = ActiveWords().ToList();
            var covered = drafts.SelectMany(s => s.TokenIds).ToHashSet();
            var positions = words.Select((w, i) => (w.Id, i)).ToDictionary(x => x.Id, x => x.i);
            return new(confirmed.TakeLast(200).ToArray(), drafts.OrderBy(s => positions.GetValueOrDefault(s.TokenIds[0], int.MaxValue)).ToArray(),
                string.Join(" ", words.Where(w => !covered.Contains(w.Id)).Select(w => w.Text)), words.Count, revision, interrupted.ToArray());
        }
    }

    public void BreakConnection()
    {
        lock (gate)
        {
            var words = ActiveWords().ToList();
            var index = 0;
            while (index < words.Count)
            {
                var draft = drafts.FirstOrDefault(s => s.TokenIds[0] == words[index].Id);
                CaptionSegment archived;
                if (draft is not null)
                {
                    archived = draft with { At = DateTimeOffset.UtcNow, Confirmed = false, Complete = false };
                    index += draft.TokenIds.Count;
                }
                else
                {
                    var end = index + 1;
                    while (end < words.Count && !drafts.Any(s => s.TokenIds[0] == words[end].Id)) end++;
                    var span = words.GetRange(index, end - index);
                    archived = new(++nextSegment, string.Join(" ", span.Select(w => w.Text)), "（连接中断，此处尚无完整翻译）", false, false, DateTimeOffset.UtcNow, span.Select(w => w.Id).ToArray());
                    index = end;
                }
                interrupted.Add(archived); Archived?.Invoke(archived);
            }
            foreach (var key in order) { retired.Add(key); retiredOrder.Enqueue(key); }
            sentences.Clear(); order.Clear(); drafts.Clear();
            while (interrupted.Count > 200) interrupted.RemoveAt(0);
            while (retiredOrder.Count > 1000) retired.Remove(retiredOrder.Dequeue());
            revision++;
        }
    }
    public void InterruptSentence(string key)
    {
        lock (gate)
        {
            if (!sentences.TryGetValue(key, out var sentence)) return;
            var pending = sentence.Words.Where(w => !w.Confirmed).ToArray();
            var ids = pending.Select(w => w.Id).ToHashSet();
            drafts.RemoveAll(s => s.TokenIds.Any(ids.Contains));
            if (pending.Length > 0)
            {
                var archived = new CaptionSegment(++nextSegment, string.Join(" ", pending.Select(w => w.Text)),
                    "（语种异常，此段未获确认）", false, false, DateTimeOffset.UtcNow, pending.Select(w => w.Id).ToArray());
                interrupted.Add(archived); Archived?.Invoke(archived);
                while (interrupted.Count > 200) interrupted.RemoveAt(0);
            }
            sentences.Remove(key); order.Remove(key); retired.Add(key); retiredOrder.Enqueue(key);
            var covered = drafts.SelectMany(s => s.TokenIds).ToHashSet();
            foreach (var word in ActiveWords()) if (!covered.Contains(word.Id)) word.TranslatedFinal = null;
            while (retiredOrder.Count > 1000) retired.Remove(retiredOrder.Dequeue());
            revision++; Promote();
        }
    }
    private IEnumerable<Word> ActiveWords() => order.SelectMany(k => sentences[k].Words).Where(w => !w.Confirmed);
    private void Promote()
    {
        while (true)
        {
            var words = ActiveWords().ToList();
            if (words.Count == 0) break;
            var segment = drafts.FirstOrDefault(s => s.TokenIds[0] == words[0].Id);
            if (segment is null || !segment.Complete || segment.TokenIds.Count > words.Count) break;
            var span = words.Take(segment.TokenIds.Count).ToArray();
            if (!span.Select(w => w.Id).SequenceEqual(segment.TokenIds) || span.Any(w => !w.Final)) break;
            foreach (var word in span) word.Confirmed = true;
            var committed = segment with { Confirmed = true };
            confirmed.Add(committed);
            if (confirmed.Count > 200) confirmed.RemoveAt(0);
            drafts.Remove(segment);
            Committed?.Invoke(committed);
        }
        foreach (var key in order.Where(k => sentences[k].Final && sentences[k].Words.All(w => w.Confirmed)).ToArray())
        {
            sentences.Remove(key); order.Remove(key); retired.Add(key); retiredOrder.Enqueue(key);
        }
        while (retiredOrder.Count > 1000) retired.Remove(retiredOrder.Dequeue());
    }
}
