namespace ClassroomCaption.Core;

// A new transport starts its item IDs at one. Keep the paused reading prefix and
// remap new IDs/revisions so neither the view nor the same local record resets.
public sealed class CaptionContinuation
{
    private readonly CaptionSegment[] retained;
    private readonly long idOffset,revisionOffset;
    private readonly object gate=new();
    private CaptionState? cachedInput,cachedOutput;
    public CaptionContinuation(CaptionState? previous=null)
    {
        previous??=new([],[],"",0,0);
        var all=previous.Confirmed.Concat(previous.Interrupted??[]).Concat(previous.Drafts).OrderBy(s=>s.At).ToList();
        idOffset=all.Select(s=>s.Id).DefaultIfEmpty(0).Max();
        if(!string.IsNullOrWhiteSpace(previous.UntranslatedRussian))
            all.Add(new(++idOffset,previous.UntranslatedRussian,"",false,false,
                all.Count>0?all[^1].At.AddTicks(1):DateTimeOffset.UtcNow,[]));
        retained=all.TakeLast(200).ToArray();
        revisionOffset=previous.Revision+1;
    }
    public CaptionSegment Map(CaptionSegment segment)=>segment with {Id=segment.Id+idOffset,TokenIds=segment.TokenIds.Select(id=>id+idOffset).ToArray()};
    public CaptionState Combine(CaptionState current)
    {
        lock(gate)
        {
        if(ReferenceEquals(current,cachedInput)&&cachedOutput is not null)return cachedOutput;
        var active=current.Confirmed.Select(Map).Concat((current.Interrupted??[]).Select(Map)).Concat(current.Drafts.Select(Map)).ToArray();
        var all=retained.Concat(active).OrderBy(s=>s.At).TakeLast(200).ToArray();
        var drafts=current.Drafts.Select(s=>s.Id+idOffset).ToHashSet();
        cachedInput=current;
        cachedOutput=new(all.Where(s=>s.Confirmed&&!drafts.Contains(s.Id)).ToArray(),all.Where(s=>drafts.Contains(s.Id)).ToArray(),
            current.UntranslatedRussian,current.PendingWords,current.Revision+revisionOffset,
            all.Where(s=>!s.Confirmed&&!drafts.Contains(s.Id)).ToArray());
        return cachedOutput;
        }
    }
}
