using System.Net.WebSockets;

namespace ClassroomCaption.Core;

public interface IRealtimeSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken token);
    Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token);
    Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token);
    void Abort();
}

internal sealed class OfficialRealtimeSocket : IRealtimeSocket
{
    private readonly ClientWebSocket socket = new();
    private readonly byte[] receiveBuffer = new byte[8192];
    public OfficialRealtimeSocket(string key)
    {
        socket.Options.Proxy = new System.Net.WebProxy();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        socket.Options.CollectHttpResponseDetails = true;
    }
    public async Task ConnectAsync(Uri uri, CancellationToken token)
    {
        try { await socket.ConnectAsync(uri, token).ConfigureAwait(false); }
        catch(WebSocketException) when((int)socket.HttpStatusCode >= 300)
        {
            var code=(int)socket.HttpStatusCode;
            throw new RealtimeServiceException(SafeErrors.HttpStatus(code),code is 408 or 429 || code>=500);
        }
    }
    public Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token) => socket.SendAsync(data, type, true, token).AsTask();
    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException();
            if (stream.Length + result.Count > 524288) throw new UserFacingException("语音服务响应异常过大，连接已停止。");
            stream.Write(receiveBuffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return stream.ToArray();
    }
    public void Abort() => socket.Abort();
    public void Dispose() => socket.Dispose();
}
