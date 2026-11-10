namespace ClassroomCaption.Core;

public sealed record CaptionPage(string Russian, string Chinese, bool HasMore = false, bool Turned = false);

/// <summary>A reading page grows with consecutive phrases; it turns only when translated content overflows.</summary>
public sealed class CaptionPageComposer
{
    private long? anchor;
    private long? beforeAnchor;
    private DateTimeOffset? shownAt;
    public void Reset() { anchor = null; beforeAnchor = null; shownAt = null; }
    private static CaptionSegment[] Segments(CaptionState state) => state.Confirmed.Concat(state.Interrupted ?? []).OrderBy(s => s.At).Concat(state.Drafts).ToArray();
    public void JumpToLatest(CaptionState state)
    {
        var segments = Segments(state);
        if (segments.Length == 0) { Reset(); return; }
        anchor = segments[^1].TokenIds.FirstOrDefault();
        beforeAnchor = segments.Length > 1 ? segments[^2].TokenIds.LastOrDefault() : null;
        shownAt = null;
    }
    public CaptionPage Compose(CaptionState state, Func<string, string, bool> fits, DateTimeOffset now, bool allowPageTurn = true,
        Func<string, string, bool>? canTurnPage = null, bool contentSettled = true)
    {
        var segments = Segments(state);
        var start = anchor.HasValue ? Array.FindIndex(segments, s => s.TokenIds.Contains(anchor.Value)) : 0;
        if (start < 0)
        {
            var predecessor = beforeAnchor.HasValue ? Array.FindIndex(segments, s => s.TokenIds.Contains(beforeAnchor.Value)) : -1;
            start = predecessor >= 0 ? Math.Min(predecessor + 1, segments.Length - 1) : 0;
        }
        var end = start; var ru = ""; var zh = ""; var turned = false;
        void Fill()
        {
            ru = ""; zh = ""; end = start;
            while (end < segments.Length)
            {
                var nextRu = Join(ru, segments[end].Russian, " ");
                var nextZh = Join(zh, segments[end].Chinese, "");
                if (end > start && !fits(nextRu, nextZh)) break;
                ru = nextRu; zh = nextZh; end++;
            }
        }
        Fill();
        if (segments.Length > 0)
        {
            if (!contentSettled) shownAt = now;
            shownAt ??= now;
            if (contentSettled && allowPageTurn && end < segments.Length && now - shownAt.Value >= TimeSpan.FromSeconds(3) && (canTurnPage?.Invoke(ru, zh) ?? true))
            {
                start = end; Fill(); shownAt = now; turned = true;
            }
            anchor = segments[start].TokenIds.FirstOrDefault();
            beforeAnchor = start > 0 ? segments[start - 1].TokenIds.LastOrDefault() : null;
        }
        // Keep live ASR as an append-only preview on this page, not a sliding last-N-words strip.
        if (end == segments.Length)
        {
            foreach (var word in state.UntranslatedRussian.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Join(ru, word, " ");
                if (!fits(candidate, zh)) break;
                ru = candidate;
            }
        }
        return new(ru, zh, end < segments.Length, turned);
    }
    private static string Join(string first, string second, string separator) =>
        string.IsNullOrWhiteSpace(first) ? second : string.IsNullOrWhiteSpace(second) ? first : first + separator + second;
}
