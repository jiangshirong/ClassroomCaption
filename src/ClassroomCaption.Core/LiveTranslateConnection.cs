using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace ClassroomCaption.Core;

public sealed record LiveConnectionChange(string Code, string Message);

public sealed record LiveRecoveryOptions
{
    public int MaxRetries { get; init; } = 6;
    public bool KeepTrying { get; init; }
    public bool PauseOnNoSemanticResultOutsideClass { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(18);
    public TimeSpan StableConnection { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed class RealtimeServiceException(string message, bool retryable, bool noSemanticResult = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
    public bool NoSemanticResult { get; } = noSemanticResult;
}

public sealed class LiveTranslateClient(AppSettings settings, string key, Func<IRealtimeSocket>? factory = null,
    LiveRecoveryOptions? recovery = null) : IAsyncDisposable
{
    private const string NoSemanticResultMessage = "no semantic result after merged audio exceeded 60s";
    private readonly LiveRecoveryOptions policy = recovery ?? new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly BoundedAudioBuffer buffer = new(640000);
    private readonly AudioDropReporter drops = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loop;
    private volatile bool stopping, connected, reconnecting;
    private long audioBytes, uncertainBytes;
    private DateTimeOffset connectedAt;
    private bool everConnected;
    public bool IsConnected => connected;
    public bool IsReconnecting => reconnecting;
    public long AudioBytes => Interlocked.Read(ref audioBytes);
    public long UncertainAudioBytes => Interlocked.Read(ref uncertainBytes);
    public long DroppedBytes => buffer.DroppedBytes;
    public LiveTranslateUsage Usage { get; } = new();
    public long InputTokens => Usage.InputTokens;
    public long OutputTokens => Usage.OutputTokens;
    public LiveTranslateState State { get; } = new();
    public event Action<string>? Fault;
    public event Action<string>? Gap;
    public event Action<LiveConnectionChange>? ConnectionChanged;
    public event Action<TranslationTrace>? Trace;
    private int connectionNumber;

    public async Task StartAsync(CancellationToken token, bool waitForConnection = true)
    {
        _ = LiveTranslateProtocol.Endpoint(settings);
        _ = LiveTranslateProtocol.Terms(settings.Glossary);
        if (loop is not null) throw new InvalidOperationException("Already started.");
        token.ThrowIfCancellationRequested();
        // Network parsing, state updates and file logging must not capture WPF's dispatcher.
        loop = Task.Run(RunAsync);
        if (waitForConnection) await ready.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public void Enqueue(byte[] pcm)
    {
        if (!stopping && !lifetime.IsCancellationRequested) buffer.Enqueue(pcm);
    }

    private async Task RunAsync()
    {
        var retries = 0;
        try
        {
            while (!stopping && !lifetime.IsCancellationRequested)
            {
                try
                {
                    using var socket = factory?.Invoke() ?? new OfficialRealtimeSocket(key);
                    await RunConnectionAsync(socket).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!stopping && !lifetime.IsCancellationRequested && (policy.KeepTrying || Retryable(ex)))
                {
                    // Only a stable connection resets the retry budget, so immediate
                    // connect/disconnect flapping cannot create an endless request loop.
                    if (connectedAt != default && DateTimeOffset.UtcNow - connectedAt >= policy.StableConnection) retries = 0;
                    connectedAt = default;
                    State.InterruptConnection();
                    if (policy.PauseOnNoSemanticResultOutsideClass &&
                        ex is RealtimeServiceException { NoSemanticResult: true } &&
                        !ClassroomSchedule.IsClassTime(policy.Clock.GetUtcNow()))
                    {
                        ConnectionChanged?.Invoke(new("auto_paused_no_semantic_result", "课外未检测到有效语音，已暂停翻译。"));
                        break;
                    }
                    if (everConnected) Gap?.Invoke("同传连接中断，断点附近的原文或译文可能不完整。已发送的音频不重传。");
                    if (!policy.KeepTrying && retries >= policy.MaxRetries)
                        throw new UserFacingException("翻译连接多次恢复失败，请稍后重新开始。");
                    reconnecting = true;
                    var delay = policy.KeepTrying && retries == 0 ? TimeSpan.Zero :
                        TimeSpan.FromMilliseconds(Math.Min(policy.MaximumDelay.TotalMilliseconds,
                            policy.InitialDelay.TotalMilliseconds * Math.Pow(2, retries)));
                    retries = Math.Min(retries + 1, 30);
                    var reason = ex is RealtimeServiceException ? ex.Message : SafeErrors.Describe(ex);
                    ConnectionChanged?.Invoke(new("reconnecting", policy.KeepTrying
                        ? $"正在自动恢复翻译。{reason}"
                        : $"连接中断，正在自动恢复（{retries}/{policy.MaxRetries}）…"));
                    ReportDroppedAudio();
                    await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                    Usage.BeginConnection();
                }
            }
        }
        catch (OperationCanceledException) when (stopping || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ready.TrySetException(ex is RealtimeServiceException ? new UserFacingException(ex.Message) : ex);
            if (!stopping) Fault?.Invoke(ex is RealtimeServiceException ? ex.Message : SafeErrors.Describe(ex));
        }
        finally
        {
            connected = reconnecting = false;
            ready.TrySetCanceled();
            lifetime.Cancel();
            State.InterruptConnection();
            ReportDroppedAudio(true);
            var remaining = buffer.BufferedBytes;
            buffer.Clear();
            if (remaining > 0) Gap?.Invoke($"本次结束时仍有 {remaining / 32000.0:F2} 秒缓存音频未发送。");
        }
    }

    private async Task RunConnectionAsync(IRealtimeSocket socket)
    {
        var connectionId=++connectionNumber;
        string sessionId="",conversationId="";
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var start = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        start.CancelAfter(policy.ConnectTimeout);
        Task? sender = null;
        Exception? failure = null;
        try
        {
            await socket.ConnectAsync(LiveTranslateProtocol.Endpoint(settings), start.Token).ConfigureAwait(false);
            await socket.SendAsync(LiveTranslateProtocol.Configure(settings.Glossary), WebSocketMessageType.Text, start.Token).ConfigureAwait(false);
            while (!connection.IsCancellationRequested)
            {
                using var doc = JsonDocument.Parse(await socket.ReceiveAsync(connected ? connection.Token : start.Token).ConfigureAwait(false));
                var e = doc.RootElement;
                var type = e.GetProperty("type").GetString();
                string Id(JsonElement element,string name)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
                if(e.TryGetProperty("session",out var session))sessionId=Id(session,"id");
                var response=e.TryGetProperty("response",out var r)?r:default;
                var conversation=Id(response,"conversation_id");if(conversation.Length>0)conversationId=conversation;
                var text=Id(e,"delta")+Id(e,"transcript")+Id(e,"text");
                var marker=SubtitleText.Clean(text)!=text;
                var error=e.TryGetProperty("error",out var detail)?detail:default;
                Trace?.Invoke(new(connectionId,type??"",sessionId,conversationId,Id(e,"event_id"),
                    Id(e,"request_id"),Id(e,"task_id"),Id(e,"response_id") is {Length:>0} rid?rid:Id(response,"id"),Id(e,"item_id"),marker?text:null,
                    type=="error"?ErrorIdentifier(error,"code"):null,type=="error"?ErrorIdentifier(error,"type"):null,
                    type=="error"?DiagnosticErrorDetail(Id(error,"message")):null));
                if (type == "error") throw ServiceError(error, connected, Id(error, "message"));
                if (type == "session.updated" && !connected)
                {
                    var recovered = reconnecting;
                    connected = true; reconnecting = false; everConnected = true; connectedAt = DateTimeOffset.UtcNow;
                    ready.TrySetResult();
                    ConnectionChanged?.Invoke(new(recovered ? "reconnected" : "connected", recovered ? "连接已恢复，继续翻译。" : "同传连接成功。"));
                    ReportDroppedAudio();
                    sender = SendGuardedAsync(socket, connection);
                }
                Usage.Accept(e);
                State.Accept(e);
                if (type == "session.finished")
                {
                    if (!stopping) throw new WebSocketException("Unexpected session end.");
                    break;
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            connected = false;
            connection.Cancel(); socket.Abort();
            if (sender is not null)
                try { await sender.ConfigureAwait(false); }
                catch (Exception ex) { if (failure is null || failure is OperationCanceledException) failure = ex; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task SendGuardedAsync(IRealtimeSocket socket, CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                ReportDroppedAudio();
                if (stopping && buffer.BufferedBytes == 0)
                {
                    using var finish = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    finish.CancelAfter(policy.SendTimeout);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "session.finish" }), WebSocketMessageType.Text, finish.Token).ConfigureAwait(false);
                    return;
                }
                AudioPacket packet;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                {
                    wait.CancelAfter(100);
                    try { packet = await buffer.TakeAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!connection.IsCancellationRequested) { continue; }
                }
                try
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    send.CancelAfter(policy.SendTimeout);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(packet.Pcm) }),
                        WebSocketMessageType.Text, send.Token).ConfigureAwait(false);
                    Interlocked.Add(ref audioBytes, packet.Pcm.Length);
                }
                catch
                {
                    Interlocked.Add(ref uncertainBytes, packet.Pcm.Length);
                    Gap?.Invoke($"连接异常时有 {packet.Pcm.Length / 32000.0:F2} 秒音频送达状态无法确认，为避免重复未重传。");
                    throw;
                }
            }
        }
        catch { connection.Cancel(); socket.Abort(); throw; }
    }

    private void ReportDroppedAudio(bool flush = false)
    {
        var notice = drops.Observe(buffer.DroppedBytes, buffer.BufferedBytes, DateTimeOffset.UtcNow, flush);
        if (notice is not null) Gap?.Invoke(notice);
    }

    private static bool Retryable(Exception error) => error switch
    {
        RealtimeServiceException e => e.Retryable,
        HttpRequestException e => e.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)e.StatusCode >= 500,
        WebSocketException or SocketException or IOException or OperationCanceledException or TimeoutException => true,
        _ => false
    };

    internal static string DiagnosticErrorDetail(string message)
    {
        if (message.Contains(NoSemanticResultMessage, StringComparison.OrdinalIgnoreCase))
            return NoSemanticResultMessage;
        return string.IsNullOrWhiteSpace(message) ? "message_missing" : "message_omitted";
    }

    private static string ErrorIdentifier(JsonElement error,string field)
    {
        if(error.ValueKind!=JsonValueKind.Object||!error.TryGetProperty(field,out var value))return "";
        var text=value.ValueKind==JsonValueKind.String?value.GetString()??"":value.ValueKind==JsonValueKind.Number?value.GetRawText():"";
        // Record structured identifiers only. Vendor messages may echo credentials.
        return text.Length<=80&&!text.StartsWith("sk-",StringComparison.OrdinalIgnoreCase)&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.')?text:"unrecognized";
    }
    private static Exception ServiceError(JsonElement error,bool configured,string detail)
    {
        string Normalize(string value)=>value.Replace("_","").Replace("-","").Replace(".","").ToLowerInvariant();
        var code=Normalize(ErrorIdentifier(error,"code"));
        var type=Normalize(ErrorIdentifier(error,"type"));
        var authentication=code is "invalidapikey" or "invalidkey" or "unauthorized" or "authenticationfailed" or "accessdenied" or "forbidden" or "permissiondenied"
            ||type is "authenticationerror" or "permissionerror";
        var quota=code is "insufficientquota" or "quotaexceeded" or "arrearage" or "accountoverdue";
        var settings=code is "invalidvalue" or "invalidparameter" or "invalidargument" or "invalidrequest" or "badrequest" or "modelnotfound" or "unsupportedmodel";
        var transient=code is "servererror" or "serviceunavailable" or "ratelimitexceeded" or "internalerror" or "sessionexpired" or "sessiontimeout" or "sessionlimitexceeded" or "contextlengthexceeded"
            ||type=="servererror";
        // An already working session can fail with a new/undocumented runtime
        // code. Try a fresh session within the existing retry budget; do not
        // misclassify every unknown failure as a bad key or exhausted account.
        var retry=!authentication&&!quota&&!settings&&(transient||configured);
        var message=retry?"翻译服务暂时中断，正在尝试恢复。":authentication?"翻译服务拒绝连接，请检查密钥与权限。":quota?"翻译服务额度不足，请检查账户。":settings?"翻译服务拒绝当前设置，请检查连接设置。":"翻译服务返回错误，请重新开始翻译。";
        return new RealtimeServiceException(message,retry,
            detail.Contains(NoSemanticResultMessage, StringComparison.OrdinalIgnoreCase));
    }

    public async Task StopAsync()
    {
        stopping = true;
        if (!connected) lifetime.Cancel();
        if (loop is null) return;
        try { await loop.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lifetime.Cancel(); await loop.ConfigureAwait(false);
            throw new UserFacingException("同传结束等待超时，最后一段可能尚未完整返回。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true; lifetime.Cancel();
        if (loop is not null) await loop.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
