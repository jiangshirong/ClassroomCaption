namespace ClassroomCaption.Core;

// One notification per congestion episode. Exact total loss remains in DroppedBytes/session metrics.
public sealed class AudioDropReporter
{
    private long observed, episodeStart;
    private DateTimeOffset firstDrop, lastDrop;
    private bool active, reported;
    public string? Observe(long totalDropped, int bufferedBytes, DateTimeOffset now, bool flush = false)
    {
        if (totalDropped > observed)
        {
            if (!active) { active = true; reported = false; episodeStart = observed; firstDrop = now; }
            lastDrop = now; observed = totalDropped;
        }
        string? notice = null;
        var lost = totalDropped - episodeStart;
        if (active && !reported && lost > 0 && (lost >= 16000 || now - firstDrop >= TimeSpan.FromSeconds(1) || flush))
        {
            var seconds = lost / 32000.0;
            var duration = seconds < .1 ? "不足 0.1 秒" : $"约 {Math.Ceiling(seconds * 10) / 10:F1} 秒";
            notice = $"音频发送积压，{duration}声音未发送。";
            reported = true;
        }
        if (active && now - lastDrop >= TimeSpan.FromSeconds(2) && bufferedBytes <= 16000) active = false;
        return notice;
    }
}
