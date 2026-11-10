using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClassroomCaption.Core;

internal static class RecoveryTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
    private static readonly LiveRecoveryOptions Fast = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(20), MaximumDelay = TimeSpan.FromMilliseconds(40),
        ConnectTimeout = TimeSpan.FromSeconds(1), SendTimeout = TimeSpan.FromSeconds(1)
    };
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    public static void Run(Action<string, Func<Task>> test, string root)
    {
        test("Classroom schedule pauses only for the confirmed 60-second error outside class", async () =>
        {
            const string silenceError = "no semantic result after merged audio exceeded 60s";
            foreach (var (hour, minute, message, shouldPause) in new[]
            {
                (8, 6, silenceError, true),
                (8, 7, silenceError, false),
                (9, 47, silenceError, false),
                (9, 48, silenceError, true),
                (9, 50, "unrelated service error", false)
            })
            {
                var first = new RecoverySocket();
                var calls = 0;
                await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic",
                    () => Interlocked.Increment(ref calls) == 1 ? first : new RecoverySocket(),
                    Fast with
                    {
                        KeepTrying = true,
                        PauseOnNoSemanticResultOutsideClass = true,
                        Clock = new FixedClock(new DateTimeOffset(2026, 11, 6, hour, minute, 0, TimeSpan.FromHours(8)))
                    });
                var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.ConnectionChanged += change =>
                {
                    if (change.Code == "auto_paused_no_semantic_result") paused.TrySetResult();
                };
                await client.StartAsync(CancellationToken.None);
                first.Emit(new { type = "error", error = new { code = "invalid_request", message } });
                await Until(() => paused.Task.IsCompleted || calls > 1);
                Assert(paused.Task.IsCompleted == shouldPause, "Unexpected schedule decision");
                if (shouldPause) Assert(calls == 1, "Paused session attempted to reconnect");
                await client.StopAsync();
            }
        });
        test("Classroom mode retries rejected sessions beyond the old limit and stops on request", async () =>
        {
            var sockets = new ConcurrentQueue<RecoverySocket>();
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic",
                () => { var socket = new RecoverySocket(); sockets.Enqueue(socket); return socket; },
                Fast with { KeepTrying = true, MaxRetries = 1 });
            var faults = new ConcurrentQueue<string>();
            client.Fault += faults.Enqueue;
            await client.StartAsync(CancellationToken.None, waitForConnection: false);
            foreach (var code in new[] { "invalid_api_key", "insufficient_quota", "invalid_value" })
            {
                await Until(() => !sockets.IsEmpty && client.IsConnected);
                Assert(sockets.TryDequeue(out var socket), "Expected an active socket");
                socket!.Emit(new { type = "error", error = new { code } });
            }
            await Until(() => !sockets.IsEmpty && client.IsConnected);
            Assert(faults.IsEmpty, "An error ended the classroom");
            await client.StopAsync();
            Assert(!client.IsConnected && !client.IsReconnecting, "Manual stop left recovery active");
        });
        test("Reconnect preserves captions, namespaces reused IDs and saves vendor tokens across sessions", async () =>
        {
            var first = new RecoverySocket(); var second = new RecoverySocket { AutoReady = false };
            var sockets = new ConcurrentQueue<RecoverySocket>([first, second]);
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => sockets.TryDequeue(out var s) ? s : throw new Exception("Extra reconnect"), Fast);
            using var store = new SessionStore(Path.Combine(root, "reconnect"));
            client.State.Committed += store.SaveCommitted; client.State.Archived += store.SaveArchived;
            client.Gap += store.Gap; client.Usage.Recorded += store.TokenUsage;
            client.Trace += store.Trace;
            var faults = new ConcurrentQueue<string>(); client.Fault += faults.Enqueue;
            await client.StartAsync(CancellationToken.None);
            first.Emit(new{type="session.created",session=new{id="session-one"}});
            first.Emit(new{type="conversation.item.input_audio_transcription.delta",item_id="tail",event_id="marker-event",request_id="marker-request",delta="<|readable_unit_tag|>"});
            first.Pair("r1", "t1", "resp1", "Первый", "第一句", 7, 2);
            first.Emit(new { type = "conversation.item.input_audio_transcription.delta", item_id = "tail", delta = "Незакончено" });
            await Until(() => client.State.Snapshot().Confirmed.Count == 1 && client.State.Snapshot().UntranslatedRussian.Length > 0);
            client.Enqueue([1, 2]); await Until(() => first.Audio.Count == 1);
            first.Fail(new WebSocketException());
            await second.Configured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(first.Disposed && first.Aborted && client.IsReconnecting, "Old connection not closed before reconnect");
            client.Enqueue([3, 4]); client.Enqueue([5, 6]);
            Assert(second.Audio.IsEmpty, "Audio sent before session.updated");
            second.Ready();
            await Until(() => second.Audio.Count == 2 && client.IsConnected);
            second.Pair("r1", "t1", "resp1", "Второй", "第二句", 14, 3);
            await Until(() => client.State.Snapshot().Confirmed.Count == 2);
            await client.StopAsync();
            Assert(second.Audio.SelectMany(x => x).SequenceEqual(new byte[] { 3, 4, 5, 6 }), "Sent audio was replayed or queued audio was lost");
            var state = client.State.Snapshot();
            Assert(state.Confirmed.Select(x => x.Chinese).SequenceEqual(new[] { "第一句", "第二句" }), "Old captions were replaced");
            Assert(state.Interrupted?.Single().Russian == "Незакончено" && state.UntranslatedRussian.Length == 0, "Interrupted draft leaked into new source");
            Assert(client.Usage.AudioInputTokens == 21 && client.Usage.ChineseOutputTokens == 5, "Usage was reset or duplicated");
            Assert(faults.IsEmpty, "Transient failure ended classroom");
            var history = SessionStore.ReadEntries(store.FilePath);
            using(var traceReader=new StreamReader(new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)))
            {
                var traceText=traceReader.ReadToEnd();
                Assert(traceText.Contains("session-one")&&traceText.Contains("marker-event")&&traceText.Contains("marker-request")&&traceText.Contains("provider_marker"),"Buffered diagnostics lost anomalous event identifiers");
            }
            Assert(history.Any(x => x.Russian == "Незакончено") && history.Any(x => x.Chinese == "第二句"), "Recovered history was not saved");
            using var reader = new StreamReader(new FileStream(store.ReadablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert(reader.ReadToEnd().Contains("音频缺口"), "Readable log omitted disconnection");
        });
        test("Reconnect buffer retains at most twenty seconds and explicitly records lost audio", async () =>
        {
            var first = new RecoverySocket(); var second = new RecoverySocket { AutoReady = false }; var calls = 0;
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => Interlocked.Increment(ref calls) == 1 ? first : second, Fast);
            var gaps = new ConcurrentQueue<string>(); client.Gap += gaps.Enqueue;
            await client.StartAsync(CancellationToken.None); first.Fail(new IOException());
            await second.Configured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var n = 0; n < 25; n++) client.Enqueue(Enumerable.Repeat((byte)n, 32000).ToArray());
            second.Ready(); await Until(() => second.Audio.Count == 20); await client.StopAsync();
            Assert(client.DroppedBytes == 160000 && second.Audio.First()[0] == 5 && second.Audio.Last()[0] == 24, "Buffer limit or order incorrect");
            Assert(gaps.Any(x => x.Contains("未发送")), "Overflow was silent");
        });
        test("An ambiguous audio send is not replayed and its uncertainty is recorded", async () =>
        {
            var first = new RecoverySocket { AudioFailure = new IOException() }; var second = new RecoverySocket(); var calls = 0;
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => Interlocked.Increment(ref calls) == 1 ? first : second, Fast);
            var gaps = new ConcurrentQueue<string>(); client.Gap += gaps.Enqueue;
            await client.StartAsync(CancellationToken.None); client.Enqueue([9, 8]);
            await Until(() => calls == 2 && client.IsConnected);
            client.Enqueue([7, 6]); await Until(() => second.Audio.Count == 1); await client.StopAsync();
            Assert(second.Audio.Single().SequenceEqual(new byte[] { 7, 6 }) && client.UncertainAudioBytes == 2, "Unacknowledged send was replayed");
            Assert(gaps.Any(x => x.Contains("无法确认")), "Ambiguous delivery missing from diagnostics");
        });
        test("Stopping during reconnect delay is immediate and prevents further connection attempts", async () =>
        {
            var first = new RecoverySocket(); var calls = 0;
            var policy = Fast with { InitialDelay = TimeSpan.FromSeconds(10), MaximumDelay = TimeSpan.FromSeconds(10) };
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => { calls++; return first; }, policy);
            var faults = new ConcurrentQueue<string>(); client.Fault += faults.Enqueue;
            await client.StartAsync(CancellationToken.None); first.Fail(new WebSocketException());
            await Until(() => client.IsReconnecting);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert(calls == 1 && !client.IsConnected && !client.IsReconnecting && faults.IsEmpty, "Stop resumed a cancelled classroom");
        });
        test("Invalid credentials do not retry or leak server error details", async () =>
        {
            var socket = new RecoverySocket { RejectKey = true }; var calls = 0;
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => { calls++; return socket; }, Fast);
            var faults = new ConcurrentQueue<string>(); client.Fault += faults.Enqueue;
            try { await client.StartAsync(CancellationToken.None); throw new Exception("Invalid key accepted"); }
            catch (UserFacingException ex) { Assert(!ex.Message.Contains("private-error-secret"), "Error leaked vendor content"); }
            await client.StopAsync();
            Assert(calls == 1 && faults.Count == 1 && !faults.Single().Contains("private-error-secret"), "Authentication failure retried or leaked");
        });
        test("Repeated transient failures stop at the retry budget without growing tasks", async () =>
        {
            var calls = 0;
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => { calls++; return new RecoverySocket { ConnectFailure = new HttpRequestException("synthetic", null, HttpStatusCode.ServiceUnavailable) }; }, Fast with { MaxRetries = 2 });
            try { await client.StartAsync(CancellationToken.None); throw new Exception("Expected retry exhaustion"); }
            catch (UserFacingException) { }
            await client.StopAsync(); Assert(calls == 3, "Retry budget was not enforced");
        });
        test("An unknown runtime service error recovers without losing captions and records only structured error identifiers",async()=>
        {
            var first=new RecoverySocket();var second=new RecoverySocket();var calls=0;
            await using var client=new LiveTranslateClient(new(){WorkspaceId="ws-test"},"synthetic",()=>Interlocked.Increment(ref calls)==1?first:second,Fast);
            using var store=new SessionStore(Path.Combine(root,"runtime-error"));client.Trace+=store.Trace;
            var faults=new ConcurrentQueue<string>();client.Fault+=faults.Enqueue;
            await client.StartAsync(CancellationToken.None);
            first.Pair("source","target","response","Старая фраза.","原有翻译。",12,5);
            await Until(()=>client.State.Snapshot().Confirmed.Count==1);
            first.Emit(new{type="error",event_id="runtime-failure",error=new{type="invalid_request_error",code="new_session_runtime_error",message="no semantic result after merged audio exceeded 60s; private-error-secret"}});
            await Until(()=>calls==2&&client.IsConnected);
            second.Pair("source","target","response","Новая фраза.","继续翻译。",8,4);
            await Until(()=>client.State.Snapshot().Confirmed.Count==2);
            Assert(faults.IsEmpty,"Recoverable runtime error stopped the classroom");
            await client.StopAsync();
            using var reader=new StreamReader(new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
            var log=reader.ReadToEnd();
            Assert(log.Contains("new_session_runtime_error")&&log.Contains("invalid_request_error")
                &&log.Contains("no semantic result after merged audio exceeded 60s")
                &&!log.Contains("private-error-secret"),"Error diagnostics lost the reason or leaked raw text");
        });
        test("Unknown runtime errors cannot bypass the reconnect budget",async()=>
        {
            var sockets=new[]{new RecoverySocket(),new RecoverySocket(),new RecoverySocket()};var calls=0;
            await using var client=new LiveTranslateClient(new(){WorkspaceId="ws-test"},"synthetic",()=>sockets[Interlocked.Increment(ref calls)-1],Fast with{MaxRetries=2});
            var faults=new ConcurrentQueue<string>();client.Fault+=faults.Enqueue;
            await client.StartAsync(CancellationToken.None);
            for(var i=0;i<3;i++)
            {
                await Until(()=>calls==i+1&&client.IsConnected);
                sockets[i].Emit(new{type="error",error=new{code="unrecognized_session_failure",message="private-error-secret"}});
                if(i<2)await Until(()=>calls==i+2&&client.IsConnected);
            }
            await Until(()=>!faults.IsEmpty);await client.StopAsync();
            Assert(calls==3&&faults.Count==1&&!faults.Single().Contains("private-error-secret"),"Runtime retry budget or redaction failed");
        });
        test("Explicit account and settings errors never retry an active session",async()=>
        {
            foreach(var code in new[]{"invalid_api_key","AccessDenied","insufficient_quota","invalid_value"})
            {
                var socket=new RecoverySocket();var calls=0;
                await using var client=new LiveTranslateClient(new(){WorkspaceId="ws-test"},"synthetic",()=>{calls++;return socket;},Fast);
                var faults=new ConcurrentQueue<string>();client.Fault+=faults.Enqueue;
                await client.StartAsync(CancellationToken.None);socket.Emit(new{type="error",error=new{code,message="private-error-secret"}});
                await Until(()=>!faults.IsEmpty);await client.StopAsync();
                Assert(calls==1&&!faults.Single().Contains("private-error-secret"),"Permanent error was retried or leaked");
            }
        });
        test("An unknown setup rejection fails once instead of repeatedly opening sessions",async()=>
        {
            var socket=new RecoverySocket{AutoReady=false};var calls=0;
            await using var client=new LiveTranslateClient(new(){WorkspaceId="ws-test"},"synthetic",()=>{calls++;return socket;},Fast);
            var start=client.StartAsync(CancellationToken.None);await socket.Configured.Task;
            socket.Emit(new{type="error",error=new{code="new_configuration_error",message="private-error-secret"}});
            try{await start;throw new Exception("Setup error accepted");}catch(UserFacingException ex){Assert(!ex.Message.Contains("private-error-secret"),"Error leaked vendor text");}
            await client.StopAsync();Assert(calls==1,"Unknown setup error retried");
        });
        test("Unexpected server session completion automatically establishes a new session", async () =>
        {
            var first = new RecoverySocket(); var second = new RecoverySocket(); var calls = 0;
            await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test" }, "synthetic", () => Interlocked.Increment(ref calls) == 1 ? first : second, Fast);
            await client.StartAsync(CancellationToken.None); first.Emit(new { type = "session.finished" });
            await Until(() => calls == 2 && client.IsConnected); await client.StopAsync();
            Assert(second.Finished, "Recovered session did not finish gracefully");
        });
    }
}

internal sealed class RecoverySocket : IRealtimeSocket
{
    private readonly Channel<object> incoming = Channel.CreateUnbounded<object>();
    public readonly TaskCompletionSource Configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly ConcurrentQueue<byte[]> Audio = new();
    public bool AutoReady = true, RejectKey;
    public volatile bool Disposed, Aborted, Finished;
    public Exception? ConnectFailure, AudioFailure;
    public void Ready() => Emit(new { type = "session.updated" });
    public void Emit(object data) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(data));
    public void Fail(Exception ex) => incoming.Writer.TryWrite(ex);
    public void Pair(string source, string target, string response, string ru, string zh, int audio, int text)
    {
        Emit(new { type = "conversation.item.input_audio_transcription.completed", item_id = source, transcript = ru });
        Emit(new { type = "conversation.item.created", previous_item_id = source, item = new { id = target, role = "assistant" } });
        Emit(new { type = "response.text.done", item_id = target, text = zh });
        Emit(new { type = "response.done", response = new { id = response, status = "completed", output = new[] { new { id = target } },
            usage = new { input_tokens_details = new { audio_tokens = audio }, output_tokens_details = new { text_tokens = text } } } });
    }
    public Task ConnectAsync(Uri uri, CancellationToken token) => ConnectFailure is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    public Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(data); var e = doc.RootElement;
        switch (e.GetProperty("type").GetString())
        {
            case "session.update":
                Configured.TrySetResult();
                if (RejectKey) Emit(new { type = "error", error = new { code = "invalid_api_key", message = "private-error-secret" } });
                else if (AutoReady) Ready();
                break;
            case "input_audio_buffer.append":
                Audio.Enqueue(Convert.FromBase64String(e.GetProperty("audio").GetString()!));
                if (AudioFailure is { } ex) return Task.FromException(ex);
                break;
            case "session.finish": Finished = true; Emit(new { type = "session.finished" }); break;
        }
        return Task.CompletedTask;
    }
    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token)
    {
        var value = await incoming.Reader.ReadAsync(token);
        if (value is Exception ex) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        return (byte[])value;
    }
    public void Abort() { Aborted = true; }
    public void Dispose() { Disposed = true; }
}
