namespace ClassroomCaption.Core;

public sealed record AudioPacket(byte[] Pcm);
public sealed class BoundedAudioBuffer(int maximumBytes = 160000)
{
    public void SetCapacity(int bytesLimit) { lock (gate) maximumBytes = bytesLimit; }
    public void Clear()
    {
        lock (gate) { while (packets.TryDequeue(out var packet)) Array.Clear(packet.Pcm); bytes = 0; }
    }
    private readonly object gate = new();
    private readonly Queue<AudioPacket> packets = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private int bytes;
    private long droppedBytes;
    public long DroppedBytes { get { lock(gate) return droppedBytes; } }
    public int BufferedBytes { get { lock (gate) return bytes; } }
    public void Enqueue(byte[] pcm)
    {
        lock (gate)
        {
            if (pcm.Length > maximumBytes) { droppedBytes += pcm.Length; return; }
            while (bytes + pcm.Length > maximumBytes && packets.TryDequeue(out var old)) { bytes -= old.Pcm.Length; droppedBytes += old.Pcm.Length; }
            packets.Enqueue(new(pcm)); bytes += pcm.Length;
            if (signal.CurrentCount == 0) signal.Release();
        }
    }
    public async Task<AudioPacket> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            lock (gate) { if (packets.TryDequeue(out var result)) { bytes -= result.Pcm.Length; return result; } }
            await signal.WaitAsync(token);
        }
    }
    public async Task<AudioPacket> TakeBatchAsync(int targetBytes, TimeSpan gatherTime, CancellationToken token, TimeSpan? firstPacketTimeout = null)
    {
        if (targetBytes <= 0 || gatherTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        AudioPacket first;
        using (var firstWait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (firstPacketTimeout.HasValue) firstWait.CancelAfter(firstPacketTimeout.Value);
            first = await TakeAsync(firstWait.Token);
        }
        if (first.Pcm.Length >= targetBytes) return first;
        using var joined = new MemoryStream();
        joined.Write(first.Pcm);
        using var gather = CancellationTokenSource.CreateLinkedTokenSource(token);
        gather.CancelAfter(gatherTime);
        while (joined.Length < targetBytes)
        {
            try { joined.Write((await TakeAsync(gather.Token)).Pcm); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
        }
        return new(joined.ToArray());
    }
}
