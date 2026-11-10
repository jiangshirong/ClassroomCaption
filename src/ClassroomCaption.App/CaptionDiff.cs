using System.Globalization;

namespace ClassroomCaption.App;

public enum CaptionChangeKind { Initial, Append, Insert, Delete, Replace, Reorder, Rewrite, Unchanged }
public sealed record CaptionChange(CaptionChangeKind Kind,int[] Matches);

public static class CaptionDiff
{
    public static int[] ReplacementSources(int[] matches,int oldCount)
    {
        var sources=Enumerable.Repeat(-1,matches.Length).ToArray();var previous=-1;
        for(var i=0;i<matches.Length;)
        {
            if(matches[i]>=0){previous=matches[i++];continue;}
            var start=i;while(i<matches.Length&&matches[i]<0)i++;
            var end=i<matches.Length?matches[i]:oldCount;
            for(var j=start;j<i&&previous+1+j-start<end;j++)sources[j]=previous+1+j-start;
        }
        return sources;
    }
    public static CaptionChange Analyze(string[] old,string[] next)
    {
        var matches=Match(old,next);
        if(old.SequenceEqual(next))return new(CaptionChangeKind.Unchanged,matches);
        if(old.Length==0)return new(CaptionChangeKind.Initial,matches);
        if(next.Length>=old.Length&&old.SequenceEqual(next.Take(old.Length)))return new(CaptionChangeKind.Append,matches);
        var common=matches.Count(i=>i>=0);
        var bag=old.GroupBy(s=>s).ToDictionary(g=>g.Key,g=>g.Count());var overlap=0;
        foreach(var s in next)if(bag.TryGetValue(s,out var count)&&count>0){overlap++;bag[s]=count-1;}
        var ratio=common/(double)Math.Max(1,Math.Max(old.Length,next.Length));
        var rolling=matches.Length>0&&matches[0]>0;
        var run=0;
        if(rolling)while(run<matches.Length&&matches[run]==matches[0]+run)run++;
        rolling=rolling&&run>=Math.Max(8,Math.Min(old.Length,next.Length)*.6);
        var reorder=!rolling&&ratio<.78&&overlap/(double)Math.Max(1,Math.Max(old.Length,next.Length))>.9;
        var kind=reorder?CaptionChangeKind.Reorder:!rolling&&ratio<.38?CaptionChangeKind.Rewrite:common==old.Length?CaptionChangeKind.Insert:common==next.Length?CaptionChangeKind.Delete:CaptionChangeKind.Replace;
        // Unrelated sentences and major clause reordering crossfade as a coherent block.
        // Matching scattered punctuation or single common characters would create distracting motion.
        if(kind is CaptionChangeKind.Reorder or CaptionChangeKind.Rewrite)Array.Fill(matches,-1);
        return new(kind,matches);
    }
    public static string[] Elements(string text) => StringInfo.GetTextElementEnumerator(text).AsEnumerable().ToArray();
    private static IEnumerable<string> AsEnumerable(this TextElementEnumerator iterator)
    { while (iterator.MoveNext()) yield return iterator.GetTextElement(); }

    // Map each new grapheme to its old index. Preserve islands of unchanged text, not just a prefix.
    public static int[] Match(string[] old, string[] next)
    {
        var result = Enumerable.Repeat(-1, next.Length).ToArray();
        var prefix = 0;
        while (prefix < old.Length && prefix < next.Length && old[prefix] == next[prefix]) { result[prefix] = prefix; prefix++; }
        if(prefix==0&&old.Length>0&&next.Length>0)
        {
            var overlap=RollingOverlap(old,next);
            if(overlap>=Math.Max(8,Math.Min(old.Length,next.Length)*.6))
            {
                for(var i=0;i<overlap;i++)result[i]=old.Length-overlap+i;
                return result;
            }
        }
        var a = old.Length; var b = next.Length;
        while (a > prefix && b > prefix && old[a - 1] == next[b - 1]) { result[--b] = --a; }
        // Long rolling buffers often change at both ends. Preserve unchanged
        // interior runs instead of treating the entire buffer as a new sentence.
        if ((long)(a - prefix) * (b - prefix) > 1_000_000)
        {
            MatchRuns(old,next,result,prefix,a,b);
            return result;
        }
        var lengths = new int[a - prefix + 1, b - prefix + 1];
        for (var i = a - 1; i >= prefix; i--)
            for (var j = b - 1; j >= prefix; j--)
                lengths[i - prefix, j - prefix] = old[i] == next[j]
                    ? 1 + lengths[i - prefix + 1, j - prefix + 1]
                    : Math.Max(lengths[i - prefix + 1, j - prefix], lengths[i - prefix, j - prefix + 1]);
        var x = prefix; var y = prefix;
        while (x < a && y < b)
        {
            if (old[x] == next[y]) { result[y++] = x++; }
            else if (lengths[x - prefix + 1, y - prefix] >= lengths[x - prefix, y - prefix + 1]) x++;
            else y++;
        }
        return result;
    }
    private static int RollingOverlap(string[] old,string[] next)
    {
        var borders=new int[next.Length];
        for(var i=1;i<next.Length;i++)
        {
            var n=borders[i-1];
            while(n>0&&next[i]!=next[n])n=borders[n-1];
            if(next[i]==next[n])n++;
            borders[i]=n;
        }
        var length=0;
        foreach(var element in old)
        {
            if(length==next.Length)length=borders[length-1];
            while(length>0&&element!=next[length])length=borders[length-1];
            if(element==next[length])length++;
        }
        return length;
    }
    private static void MatchRuns(string[] old,string[] next,int[] result,int start,int oldEnd,int nextEnd)
    {
        const int block=16;
        var anchors=new Dictionary<string,List<int>>(StringComparer.Ordinal);
        string Key(string[] text,int offset)=>string.Join('\0',text,offset,block);
        for(var i=start;i+block<=oldEnd;i++)
        {
            var key=Key(old,i);
            if(!anchors.TryGetValue(key,out var positions))anchors[key]=positions=[];
            positions.Add(i);
        }
        var cursor=start;
        for(var i=start;i+block<=nextEnd;)
        {
            if(!anchors.TryGetValue(Key(next,i),out var candidates)){i++;continue;}
            var index=candidates.BinarySearch(cursor);if(index<0)index=~index;
            if(index>=candidates.Count){i++;continue;}
            var match=candidates[index];var count=block;
            while(match+count<oldEnd&&i+count<nextEnd&&old[match+count]==next[i+count])count++;
            for(var n=0;n<count;n++)result[i+n]=match+n;
            cursor=match+count;i+=count;
        }
    }
}
