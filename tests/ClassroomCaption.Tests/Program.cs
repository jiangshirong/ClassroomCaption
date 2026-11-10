using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using ClassroomCaption.Core;
var performanceIndex=Array.IndexOf(args,"--state-benchmark");
if(performanceIndex>=0&&performanceIndex+1<args.Length)return PerformanceCheck.Run(args[performanceIndex+1]);
var liveIndex = Array.IndexOf(args, "--live-livetranslate");
if(liveIndex>=0 && liveIndex+1<args.Length) return await LiveTranslationCheck.CheckLiveTranslateAsync(args[liveIndex+1]);
var tests = new List<(string, Func<Task>)>();
void Test(string title, Action action) => tests.Add((title, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string title, Func<Task> action) => tests.Add((title, action));
void Assert(bool value, string text = "Assertion failed") { if (!value) throw new Exception(text); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var temp = Path.Combine(Path.GetTempPath(), "ClassroomCaption-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var keys = new ApiCredentials("synthetic-aliyun-key");
var summary = new List<object>();
LongClassroomTests.Run(Test,temp);
RecoveryTests.Run(AsyncTest,temp);
CaptionContinuationTests.Run(Test,temp);
Test("Unchanged caption snapshots are reused and unrelated protocol events cannot invalidate them",()=>
{
    var state=new LiveTranslateState();
    using var delta=JsonDocument.Parse("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"s","delta":"Привет"}""");
    state.Accept(delta.RootElement);var first=state.Snapshot();
    using var unused=JsonDocument.Parse("""{"type":"response.created","event_id":"other"}""");state.Accept(unused.RootElement);
    Assert(ReferenceEquals(first,state.Snapshot())&&first.UntranslatedRussian=="Привет");
    state.Accept(delta.RootElement);Assert(!ReferenceEquals(first,state.Snapshot())&&state.Snapshot().UntranslatedRussian=="ПриветПривет");
});
Test("Buffered trace IDs flush at final response and unchanged draft text is not written again",()=>
{
    using var store=new SessionStore(Path.Combine(temp,"buffered-trace"));
    store.Trace(new(1,"response.text.delta","session-test","","event-test","","","response-test","item-test",null));
    store.SaveDraft(new([],[],"Привет",0,1));store.SaveDraft(new([],[],"Привет",0,2));
    store.Trace(new(1,"response.done","session-test","","event-done","","","response-test","item-test",null));
    using var reader=new StreamReader(new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
    var records=reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries);
    Assert(records.Count(s=>s.Contains("draft_patch"))==1&&records.Any(s=>s.Contains("event-test"))&&records.Any(s=>s.Contains("event-done")));
});
Test("Vendor token details persist separately, deduplicate responses and preserve missing fields",()=>
{
    var usage=new LiveTranslateUsage();
    using var store=new SessionStore(Path.Combine(temp,"token-usage"));
    usage.Recorded+=store.TokenUsage;
    void Send(string json){using var doc=JsonDocument.Parse(json);usage.Accept(doc.RootElement);}
    var response="""{"type":"response.done","response":{"id":"r1","usage":{"input_tokens":99,"output_tokens":30,"input_tokens_details":{"audio_tokens":70,"text_tokens":29},"output_tokens_details":{"text_tokens":20,"audio_tokens":10}}}}""";
    Send(response);Send(response);
    Assert(usage.AudioInputTokens==70&&usage.ChineseOutputTokens==20&&usage.InputTokens==99);
    Send("""{"type":"response.done","response":{"id":"r2","usage":{"input_tokens":5,"output_tokens":8}}}""");
    Assert(usage.AudioInputTokens==70&&usage.ChineseOutputTokens==20&&usage.AudioInputMissingResponses==1&&usage.ChineseOutputMissingResponses==1);
    Send("""{"type":"response.done","response":{"id":"r3","usage":{"input_tokens_details":{"audio_tokens":0},"output_tokens_details":{"text_tokens":0}}}}""");
    using var reader=new StreamReader(new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
    var rows=reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>JsonDocument.Parse(s)).ToArray();
    try{var records=rows.Where(d=>d.RootElement.GetProperty("type").GetString()=="token_usage").ToArray();
        Assert(records.Length==3&&records[0].RootElement.GetProperty("audioInputTokens").GetInt64()==70);
        Assert(records[1].RootElement.GetProperty("chineseOutputTokens").ValueKind==JsonValueKind.Null);
        Assert(records[2].RootElement.GetProperty("chineseOutputTokens").GetInt64()==0);
    }finally{foreach(var row in rows)row.Dispose();}
    Assert(new LiveTranslateUsage().AudioInputTokens is null);
});
Test("Internal markers including split tokens never reach subtitles while diagnostics retain raw text",()=>
{
    var state=new LiveTranslateState();var raw=new List<string>();state.InternalMarkupObserved+=(_,s)=>raw.Add(s);
    void Send(string delta){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new {type="conversation.item.input_audio_transcription.delta",item_id="s",delta}));state.Accept(doc.RootElement);}
    Send("Привет <");Assert(!state.Snapshot().UntranslatedRussian.Contains('<'));
    Send("|readable_unit_");Assert(!state.Snapshot().UntranslatedRussian.Contains("readable"));
    Send("tag|>мир");Assert(state.Snapshot().UntranslatedRussian=="Привет мир"&&raw.Any(s=>s.Contains("<|readable_unit_tag|>")));
    Assert(SubtitleText.Clean("x < y > z")=="x < y > z");
    Assert(SubtitleText.Clean("<title>文本</title>")=="文本");
});
Test("Readable classroom text saves revisions immediately and keeps raw markers only in JSONL",()=>
{
    using var store=new SessionStore(Path.Combine(temp,"readable"));
    store.RawMarker("source","原文<|readable_unit_tag|>");
    var segment=new CaptionSegment(1,"Привет<|readable_unit_tag|>","你好",true,true,DateTimeOffset.Now,[]);
    store.SaveCommitted(segment);
    store.SaveDraft(new([],[],"Новая фраза",0,2));
    string Read(string path){using var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));return reader.ReadToEnd();}
    var txt=Read(store.ReadablePath);Assert(txt.Contains("Привет")&&txt.Contains("你好")&&txt.Contains("Новая фраза")&&!txt.Contains("readable_unit_tag"));
    Assert(Read(store.FilePath).Contains("readable_unit_tag"));
});
Test("Course terminology is embedded, cached and included without a settings editor",()=>
{
    Assert(CourseTerminology.Terms.Count>=100&&ReferenceEquals(CourseTerminology.Terms,CourseTerminology.Terms));
    Assert(CourseTerminology.Terms["бортовая качка"]=="横摇");
    Assert(CourseTerminology.Terms["АЧХ"]=="幅频特性");
    Assert(CourseTerminology.Terms["продольная качка"]=="纵向摇荡"&&CourseTerminology.Terms["килевая качка"]=="纵摇");
    Assert(CourseTerminology.Terms["поперечная качка"]=="横向摇荡"&&CourseTerminology.Terms["поперечно-горизонтальная качка"]=="横荡");
    Assert(CourseTerminology.Merge("демпфирование = 自定义")["демпфирование"]=="自定义");
    using var config=JsonDocument.Parse(LiveTranslateProtocol.Configure(""));
    Assert(config.RootElement.GetProperty("session").GetProperty("translation").GetProperty("corpus").GetProperty("phrases").EnumerateObject().Count()>=100);
    var phrases=config.RootElement.GetProperty("session").GetProperty("translation").GetProperty("corpus").GetProperty("phrases");
    Assert(phrases.GetProperty("кажущаяся частота").GetString()=="遭遇频率"&&phrases.GetProperty("модельные испытания").GetString()=="模型试验");
    Assert(phrases.EnumerateObject().Count()<=1000);
});
Test("Spoken Russian pairs only acquire subscripts or parentheses with a local cue", () =>
{
    string Caption(string text) => string.Concat(MathNotation.Parse(text).Select(run => run.Text));
    Assert(Caption("икс игрек") == "xy");
    Assert(Caption("икс с нижним индексом игрек") == "$x_{y}$");
    Assert(Caption("икс игрек, где игрек — индекс") == "$x_{y}$, где игрек — индекс");
    Assert(Caption("икс умножить на игрек") == "x·y");
    Assert(Caption("вэ тэта и vθ") == "vθ и vθ");
    Assert(Caption("вэ от тета") == "v(θ)");
    Assert(Caption("функция вэ тета") == "функция v(θ)");
    Assert(Caption("функция вэ тета. вэ тета") == "функция v(θ). vθ");
    Assert(Caption("$v(\\theta)$") == "$v(\\theta)$");
});
Test("Mathematical notation preserves operator scope and does not invent missing powers or roots",()=>
{
    string Formula(string s)=>string.Concat(MathNotation.Parse(s).Where(r=>r.Formula).Select(r=>r.Text));
    Assert(Formula("x/y + z")==@"$\frac{x}{y}$");
    Assert(Formula("x²/y²")==@"$\frac{x^2}{y^2}$");
    Assert(Formula("x/(y+z)")==@"$\frac{x}{y+z}$");
    Assert(Formula("x除以y")==@"$\frac{x}{y}$");
    Assert(Formula("2026/11/04")==""&&Formula("https://example.com/a/b")==""&&Formula("x/y/z")=="");
    Assert(!Formula("sin x/y").Contains("frac")&&!Formula("sin(x)/y").Contains("frac"));
    Assert(Formula("x²")=="$x^{2}$"&&Formula("z_g")=="$z_g$");
    Assert(Formula("乘以cosine(ωt)").Contains(@"\cos"));
    Assert(Formula(@"$\frac{x}{y}$")==@"$\frac{x}{y}$");
    Assert(!Formula("x/y").Contains("sqrt")&&!Formula("x/y").Contains('^'));
});
Test("LiveTranslate config requests Chinese text and validates paired terminology", () =>
{
    using var json = JsonDocument.Parse(LiveTranslateProtocol.Configure("остойчивость = 稳性"));
    var session = json.RootElement.GetProperty("session");
    Assert(session.GetProperty("output_modalities").GetArrayLength() == 1);
    Assert(session.GetProperty("translation").GetProperty("language").GetString() == "zh");
    Assert(LiveTranslateProtocol.Terms("").Count == 0);
    Throws<UserFacingException>(() => LiveTranslateProtocol.Terms("остойчивость"));
    Throws<UserFacingException>(() => LiveTranslateProtocol.Endpoint(new()));
});

Test("LiveTranslate joins by previous_item_id even if translations arrive first", () =>
{
    var state = new LiveTranslateState(); var saved = 0; state.Committed += _ => saved++;
    void Accept(string data) { using var json = JsonDocument.Parse(data); state.Accept(json.RootElement); }
    Accept("""{"type":"response.text.delta","event_id":"1","item_id":"t2","delta":"稳性"}""");
    Accept("""{"type":"response.text.delta","event_id":"1","item_id":"t2","delta":"稳性"}""");
    Accept("""{"type":"conversation.item.created","previous_item_id":"s2","item":{"id":"t2","role":"assistant"}}""");
    Accept("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"s1","transcript":"Добрый день."}""");
    Accept("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"s2","transcript":"Остойчивость."}""");
    Assert(state.Snapshot().Drafts.Single().Russian == "Остойчивость.");
    Assert(state.Snapshot().Drafts.Single().Chinese == "稳性");
    Accept("""{"type":"response.text.done","item_id":"t2","text":"稳性。"}""");
    Assert(saved == 0);
    Accept("""{"type":"response.done","response":{"status":"completed","output":[{"id":"t2"}]}}""");
    Assert(saved == 1 && state.Snapshot().Confirmed.Single().Chinese == "稳性。");
});

Test("Plaintext keys persist and reload without a password", () =>
{
    var store = new SettingsStore(Path.Combine(temp, "plaintext"));
    store.SaveCredentials(keys);
    Assert(new SettingsStore(store.Root).LoadCredentials() == keys);
    Assert(File.ReadAllText(store.CredentialsPath).Contains(keys.AliyunKey));
});

Test("Adaptive colors prioritize near-maximal contrast and use black on white for both languages", () =>
{
    var colors=new HashSet<AdaptiveInk>();
    foreach(var red in new byte[] {0,32,64,128,192,255})
    foreach(var green in new byte[] {0,32,64,128,192,255})
    foreach(var blue in new byte[] {0,32,64,128,192,255})
    foreach(var role in new[] {CaptionInkRole.Chinese,CaptionInkRole.Russian})
    {
        var ink=AdaptiveContrast.Select(red,green,blue,role);colors.Add(ink);
        var background=AdaptiveContrast.Luminance(red,green,blue);
        var contrast=AdaptiveContrast.Ratio(ink.Luminance,background);
        Assert(contrast>=4.5-1e-10);
        Assert(contrast>=.9*Math.Max(AdaptiveContrast.Ratio(0,background),AdaptiveContrast.Ratio(1,background))-1e-10);
    }
    Assert(colors.Count>30 && colors.Count(c=>c.Red!=c.Green||c.Green!=c.Blue)>20);
    foreach(var role in new[] {CaptionInkRole.Chinese,CaptionInkRole.Russian})
    foreach(var value in new byte[] {0,32,64,96,128,160,192,224,255})
        Assert(AdaptiveContrast.Select(value,value,value,role)==(AdaptiveContrast.UseBlack(value,value,value)?new AdaptiveInk(0,0,0):new AdaptiveInk(255,255,255)));
    var map=AdaptiveContrast.MapBgra(new byte[] {255,255,255,255,0,0,0,255});
    Assert(map[3]==255&&map[7]==255&&map[0]!=map[4]);
    Assert(map[0]==0&&map[1]==0&&map[2]==0&&map[4]==255&&map[5]==255&&map[6]==255);
    var samples=Enumerable.Range(0,512).SelectMany(n=>new byte[] {(byte)n,(byte)(n*17),(byte)(n*43),255}).ToArray();
    var chinese=new byte[samples.Length];var russian=new byte[samples.Length];AdaptiveContrast.MapPair(samples,chinese,russian);
    Assert(chinese.SequenceEqual(AdaptiveContrast.MapBgra(samples,CaptionInkRole.Chinese))&&russian.SequenceEqual(AdaptiveContrast.MapBgra(samples,CaptionInkRole.Russian)));
    Assert(new AppSettings {Overlay=new() {AdaptiveContrast=true}}.Clone().Overlay.AdaptiveContrast);
    Assert(new AppSettings().Overlay.TextBackgroundOpacity==.75);
    Assert(new AppSettings {Overlay=new() {TextBackgroundOpacity=.35}}.Clone().Overlay.TextBackgroundOpacity==.35);
});

Test("Recognition feedback distinguishes missing capture, silence, send failure and no result", () =>
{
    Assert(RecognitionFeedback.Describe(9, 0, 0, 0, false).Contains("没有送来"));
    Assert(RecognitionFeedback.Describe(9, 32000, 32000, 0, false).Contains("静音"));
    Assert(RecognitionFeedback.Describe(9, 32000, 0, .1, false).Contains("尚未发送"));
    Assert(RecognitionFeedback.Describe(9, 32000, 32000, .1, false).Contains("尚无识别结果"));
    Assert(RecognitionFeedback.Describe(9, 32000, 32000, .1, true) == "");
});

Test("Reading time begins only after caption animation has settled", () =>
{
    var now = DateTimeOffset.UtcNow;
    var first = new CaptionSegment(1, "Первое предложение", "第一句", true, true, now, [1]);
    var second = new CaptionSegment(2, "Второе предложение", "第二句", true, true, now.AddSeconds(1), [2]);
    var state = new CaptionState([first, second], [], "", 0, 1);
    var page = new CaptionPageComposer();
    page.Compose(state, (_, zh) => zh.Length <= 3, now, contentSettled: false);
    var pending = page.Compose(state, (_, zh) => zh.Length <= 3, now.AddSeconds(10), contentSettled: false);
    Assert(pending.Chinese == "第一句" && !pending.Turned);
    var reading = page.Compose(state, (_, zh) => zh.Length <= 3, now.AddSeconds(12), contentSettled: true);
    Assert(reading.Chinese == "第一句" && !reading.Turned);
    var done = page.Compose(state, (_, zh) => zh.Length <= 3, now.AddSeconds(13.1), contentSettled: true);
    Assert(done.Chinese == "第二句" && done.Turned);
});

Test("Saved history reloads structured bilingual rows with the latest draft", () =>
{
    var dir = Path.Combine(temp, "structured-history");
    string file;
    using (var store = new SessionStore(dir))
    {
        file = store.FilePath;
        store.SaveCommitted(new(1, "Первое", "第一句", true, true, DateTimeOffset.UtcNow, [1]));
        store.SaveDraft(new([], [new(2, "Второе", "旧译文", false, false, DateTimeOffset.UtcNow, [2])], "", 0, 1));
        store.SaveDraft(new([], [new(2, "Второе", "修订译文", false, false, DateTimeOffset.UtcNow, [2])], "", 0, 2));
    }
    var rows = SessionStore.ReadEntries(file);
    Assert(rows.Count == 2 && rows[0].Russian == "Первое" && rows[0].Chinese == "第一句");
    Assert(rows[1].Label == "暂译" && rows[1].Chinese == "修订译文");
});

Test("Credential ToString redacted", () => Assert(!keys.ToString().Contains(keys.AliyunKey)));

Test("Settings serialization has no credential properties", () => { var settings = new AppSettings(); var path = new SettingsStore(Path.Combine(temp, "config")); path.Save(settings); Assert(!File.ReadAllText(path.SettingsPath).Contains("Key", StringComparison.OrdinalIgnoreCase)); });

Test("An anomalous final does not leave an earlier provisional sentence blocking later confirmations", () =>
{
    var engine = new CaptionEngine(); engine.Update(new("old", "Если центр тяжести", false));
    var old = engine.CreateSnapshot()!; engine.Apply(old, [new(0, 3, "如果重心", false)]);
    engine.InterruptSentence("old");
    engine.Update(new("next", "Судно устойчиво.", true)); var next = engine.CreateSnapshot()!;
    Assert(engine.Apply(next, [new(0, 2, "船舶具有稳性。", true)]));
    Assert(engine.State().Confirmed.Count == 1 && engine.State().Interrupted!.Count == 1 && engine.State().PendingWords == 0);
    Assert(!engine.Apply(old, [new(0, 3, "过期译文", false)]));
});

Test("ASR revisions invalidate in-flight translation", () => { var e = new CaptionEngine(); e.Update(new("s", "Судно не имеет крена", false)); var s = e.CreateSnapshot()!; e.Update(new("s", "Судно имеет крен", false)); Assert(!e.Apply(s, [new(0, s.Tokens.Count, "船没有横倾", false)])); Assert(e.State().Drafts.Count == 0); });

Test("ASR tail revision retains only complete unchanged leading translation segments", () =>
{
    var e = new CaptionEngine(); e.Update(new("s", "Судно под водой дверь закрыта окно открыто", false));
    var snapshot = e.CreateSnapshot()!;
    e.Update(new("s", "Судно под водой дверь закрыта окно разбито", false));
    Assert(e.Apply(snapshot, [new(0, 3, "船在水下", false), new(3, 5, "门关着", false), new(5, 7, "窗开着", false)], out var applied));
    Assert(applied == 5 && e.State().Drafts.Count == 2 && e.State().UntranslatedRussian == "окно разбито");
    Assert(e.CreateSnapshot()!.Tokens.Any(t => t.Text == "разбито"));
});

Test("A revision inside one Chinese segment never applies its guessed partial translation", () =>
{
    var e = new CaptionEngine(); e.Update(new("s", "Судно не имеет крена", false)); var snapshot = e.CreateSnapshot()!;
    e.Update(new("s", "Судно не имеет дифферента", false));
    Assert(!e.Apply(snapshot, [new(0, 4, "船没有横倾", false)], out var applied) && applied == 0);
});

Test("Appended ASR text does not invalidate unchanged translated prefix", () => { var e = new CaptionEngine(); e.Update(new("s", "Если центр", false)); var s = e.CreateSnapshot()!; e.Update(new("s", "Если центр тяжести поднимается", false)); Assert(e.Apply(s, [new(0, 2, "如果重心", false)])); Assert(e.State().UntranslatedRussian == "тяжести поднимается"); });

Test("ASR final alone cannot commit incomplete meaning", () => { var e = new CaptionEngine(); e.Update(new("s", "Если центр тяжести", true)); var s = e.CreateSnapshot()!; e.Apply(s, [new(0, 3, "如果重心", false)]); Assert(e.State().Confirmed.Count == 0 && e.State().Drafts.Count == 1); });

Test("Semantic completion alone cannot commit provisional ASR", () => { var e = new CaptionEngine(); e.Update(new("s", "Судно устойчиво.", false)); var s = e.CreateSnapshot()!; e.Apply(s, [new(0, 2, "船舶具有稳性。", true)]); Assert(e.State().Confirmed.Count == 0); e.Update(new("s", "Судно устойчиво.", true)); Assert(e.State().Confirmed.Count == 1); });

Test("Out-of-order translation cannot overwrite newer result", () => { var e = new CaptionEngine(); e.Update(new("s", "Судно устойчиво", false)); var old = e.CreateSnapshot()!; var newer = e.CreateSnapshot()!; e.Apply(newer, [new(0, 2, "较新译文", false)]); Assert(!e.Apply(old, [new(0, 2, "旧译文", false)])); Assert(e.State().Drafts[0].Chinese == "较新译文"); });

Test("Overlapping or omitted source spans rejected", () => { var e = new CaptionEngine(); e.Update(new("s", "один два три", false)); var s = e.CreateSnapshot()!; Throws<UserFacingException>(() => e.Apply(s, [new(0, 1, "一", false), new(2, 3, "三", false)])); Throws<UserFacingException>(() => e.Apply(s, [new(0, 2, "一二", false), new(1, 3, "二三", false)])); });

Test("Draft invalidation includes unchanged prefix of affected span", () => { var e = new CaptionEngine(); e.Update(new("s", "Это пятнадцать тонн", false)); var s = e.CreateSnapshot()!; e.Apply(s, [new(0, 3, "这是十五吨", false)]); e.Update(new("s", "Это пятьдесят тонн", false)); var next = e.CreateSnapshot()!; Assert(next.Russian == "Это пятьдесят тонн" && e.State().Drafts.Count == 0); });

Test("Confirmed source cannot be silently rewritten", () => { var e = new CaptionEngine(); e.Update(new("s", "Это судно.", true)); var s = e.CreateSnapshot()!; e.Apply(s, [new(0, 2, "这是船。", true)]); e.Update(new("s", "Это лодка.", true)); Assert(e.State().Confirmed.Single().Russian == "Это судно."); });

Test("ASR disconnection archives unfinished source without blocking future commits", () =>
{
    var e = new CaptionEngine(); e.Update(new("old/1", "Если центр", false)); var old = e.CreateSnapshot()!;
    e.Apply(old, [new(0, 2, "如果重心", false)]); e.BreakConnection();
    Assert(e.State().Interrupted?.Count == 1 && e.State().Interrupted![0].Confirmed == false && e.State().PendingWords == 0);
    e.Update(new("new/1", "Судно устойчиво.", true)); var current = e.CreateSnapshot()!;
    Assert(!e.Apply(old, [new(0, 2, "旧译文", false)])); e.Apply(current, [new(0, 2, "船舶具有稳性。", true)]);
    Assert(e.State().Confirmed.Count == 1);
});

Test("Late events from a broken ASR task cannot restore abandoned text", () =>
{
    var e = new CaptionEngine();e.Update(new("old/1", "Если центр", false));e.BreakConnection();
    e.Update(new("old/1", "Если центр тяжести", true));Assert(e.State().PendingWords == 0 && e.State().Interrupted?.Count == 1);
});

Test("Long unbroken speech can continue beyond one request window", () =>
{
    var e = new CaptionEngine(); var text = string.Join(" ", Enumerable.Range(0, 500).Select(i => "слово" + i)); e.Update(new("long", text, false));
    var covered = new HashSet<long>();
    for (var i = 0; i < 6; i++) { var s = e.CreateSnapshot(); if (s is null) break; foreach (var token in s.Tokens) covered.Add(token.Id); e.Apply(s, [new(0, s.Tokens.Count, "暂译" + i, false)]); }
    Assert(covered.Count == 500, $"Only {covered.Count} words translated");
});

Test("Bounded audio buffer drops oldest data only", () => { var q = new BoundedAudioBuffer(100); q.Enqueue(new byte[60]); q.Enqueue(new byte[60]); Assert(q.BufferedBytes == 60 && q.DroppedBytes == 60); });

Test("Audio drop alerts aggregate an episode and never report positive loss as zero seconds", () =>
{
    var reporter = new AudioDropReporter(); var now = DateTimeOffset.UtcNow;
    Assert(reporter.Observe(320, 160000, now) is null);
    var first = reporter.Observe(320, 160000, now.AddSeconds(1.1));
    Assert(first is not null && first.Contains("不足 0.1") && !first.Contains("0.0"));
    for (var i = 1; i < 100; i++) Assert(reporter.Observe(320 + 320L * i, 160000, now.AddSeconds(1.1 + i * .01)) is null);
    Assert(reporter.Observe(32000, 0, now.AddSeconds(5)) is null);
    var second = reporter.Observe(64000, 160000, now.AddSeconds(6));
    Assert(second is not null && second.Contains("1.0"));
});

AsyncTest("Batch gathering preserves the tail after waiting for the first audio packet", async () =>
{
    var buffer = new BoundedAudioBuffer();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var take = buffer.TakeBatchAsync(3200, TimeSpan.FromMilliseconds(80), timeout.Token, TimeSpan.FromMilliseconds(200));
    await Task.Delay(160, timeout.Token);
    buffer.Enqueue(new byte[320]);
    var result = await take;
    Assert(result.Pcm.Length == 320 && buffer.BufferedBytes == 0 && buffer.DroppedBytes == 0);
});

AsyncTest("Audio buffer wakes sender and cancellation stops wait", async () => { var q = new BoundedAudioBuffer(); using var c = new CancellationTokenSource(2000); var read = q.TakeAsync(c.Token); q.Enqueue([1, 2]); Assert((await read).Pcm.SequenceEqual(new byte[] { 1, 2 })); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); try { await q.TakeAsync(cancelled.Token); } catch (OperationCanceledException) { return; } throw new Exception("Cancellation ignored"); });

Test("Session export preserves drafts and excludes stale snapshots", () =>
{
    string file; using (var session = new SessionStore(Path.Combine(temp, "sessions")))
    {
        file = session.FilePath; session.SaveCommitted(new(1, "Судно устойчиво.", "船舶具有稳性。", true, true, DateTimeOffset.UtcNow, [1, 2]));
        session.SaveDraft(new([], [new(2, "Если", "如果", false, false, DateTimeOffset.UtcNow, [3])], "", 1, 1));
        session.SaveDraft(new([], [], "Если центр", 2, 2)); session.Gap("短暂中断");
    }
    var output = Path.Combine(temp, "export.txt");SessionStore.ExportText(file, output);var text = File.ReadAllText(output); Assert(text.Contains("船舶具有稳性。") && text.Contains("Если центр") && !text.Contains("[暂译]"));
});

Test("Unwritable output fails explicitly without fallback", () => { var file = Path.Combine(temp, "not-a-directory"); File.WriteAllText(file, "x"); Throws<IOException>(() => new SessionStore(file)); });

Test("Export while recording can merge unsaved memory without duplicating saved segments", () =>
{
    using var session = new SessionStore(Path.Combine(temp, "active-export"));
    var saved = new CaptionSegment(1, "Это судно.", "这是船。", true, true, DateTimeOffset.UtcNow, [1, 2]);session.SaveCommitted(saved);
    var unsaved = new CaptionSegment(2, "Оно устойчиво.", "它具有稳性。", true, true, DateTimeOffset.UtcNow, [3, 4]);
    var target = Path.Combine(temp, "active-export.txt");SessionStore.ExportText(session.FilePath, target, new([saved, unsaved], [], "", 0, 2));
    var text=File.ReadAllText(target);Assert(text.Split("这是船。").Length == 2 && text.Contains("它具有稳性。"));
});

Test("Older draft snapshots cannot overwrite newer persisted revision", () =>
{
    using var session = new SessionStore(Path.Combine(temp, "revision-export"));session.SaveDraft(new([], [], "新俄语草稿", 1, 10));session.SaveDraft(new([], [], "旧草稿", 1, 9));
    var target=Path.Combine(temp,"revision-export.txt");SessionStore.ExportText(session.FilePath,target);Assert(File.ReadAllText(target).Contains("新俄语草稿") && !File.ReadAllText(target).Contains("旧草稿"));
});

Test("Crashed last JSONL record does not discard earlier saved content", () =>
{
    string path;using(var session=new SessionStore(Path.Combine(temp,"truncated"))) { path=session.FilePath;session.SaveDraft(new([],[],"Судно",1,1)); }
    File.AppendAllText(path,"{\"type\":");var target=Path.Combine(temp,"truncated.txt");SessionStore.ExportText(path,target);Assert(File.ReadAllText(target).Contains("Судно"));
});

Test("Unavailable original storage still allows explicit recent-memory recovery", () =>
{
    var state = new CaptionState([new(1,"Судно устойчиво.","船舶具有稳性。",true,true,DateTimeOffset.UtcNow,[1,2])],[],"",0,1);
    var target=Path.Combine(temp,"memory-recovery.txt");SessionStore.ExportText(Path.Combine(temp,"missing-session.jsonl"),target,state);
    var text=File.ReadAllText(target);Assert(text.Contains("船舶具有稳性。") && text.Contains("较早历史"));
});

Test("Reading page accumulates consecutive phrases and turns only when full after a reading interval", () =>
{
    var page = new CaptionPageComposer(); var now = DateTimeOffset.UtcNow;
    var segments = Enumerable.Range(0, 4).Select(i => new CaptionSegment(i + 1, "слово" + i, "片段" + i, true, true, now.AddMilliseconds(i), [i + 1])).ToArray();
    var initial = page.Compose(new(segments.Take(1).ToArray(), [], "", 0, 1), (_, zh) => zh.Length <= 6, now);
    Assert(initial.Chinese == "片段0");
    var growing = page.Compose(new(segments, [], "", 0, 2), (_, zh) => zh.Length <= 6, now.AddSeconds(1));
    Assert(growing.Chinese == "片段0片段1");
    var next = page.Compose(new(segments, [], "", 0, 2), (_, zh) => zh.Length <= 6, now.AddSeconds(3.1));
    Assert(next.Chinese == "片段2片段3");
    segments[2] = segments[2] with { Chinese = "修订2", TokenIds = [30] };
    var revised = page.Compose(new(segments, [], "", 0, 3), (_, zh) => zh.Length <= 6, now.AddSeconds(3.2));
    Assert(revised.Chinese.StartsWith("修订2") && !revised.Chinese.Contains("片段0"));
});

Test("An unread long page stays in place and can jump explicitly to the newest segment", () =>
{
    var page = new CaptionPageComposer(); var now = DateTimeOffset.UtcNow;
    var first = new CaptionSegment(1, "Длинный первый фрагмент", "很长的第一段译文", true, true, now, [1]);
    var second = new CaptionSegment(2, "Следующий фрагмент", "下一段译文", true, true, now.AddSeconds(1), [2]);
    var state = new CaptionState([first, second], [], "", 0, 1);
    static bool Fits(string _, string zh) => zh.Length <= 4;
    var initial = page.Compose(state, Fits, now);
    Assert(initial.Chinese == first.Chinese && initial.HasMore && !initial.Turned);
    var reading = page.Compose(state, Fits, now.AddSeconds(20), allowPageTurn: false);
    Assert(reading.Chinese == first.Chinese && reading.HasMore && !reading.Turned);
    var continued = page.Compose(state, Fits, now.AddSeconds(21));
    Assert(continued.Chinese == second.Chinese && continued.Turned);
    page.Reset(); page.Compose(state, Fits, now); page.JumpToLatest(state);
    Assert(page.Compose(state, Fits, now.AddSeconds(1)).Chinese == second.Chinese);
});

Test("ASR preview holds the beginning of a full page instead of sliding with every new word", () =>
{
    var page = new CaptionPageComposer(); var now = DateTimeOffset.UtcNow;
    var a = page.Compose(new([], [], "один два три четыре", 4, 1), (ru, _) => ru.Length <= 12, now);
    var b = page.Compose(new([], [], "один два три четыре пять шесть семь", 7, 2), (ru, _) => ru.Length <= 12, now.AddSeconds(1));
    Assert(a.Russian == b.Russian && a.Russian.StartsWith("один") && !a.Russian.Contains('…'));
});
AsyncTest("Realtime audio waits for session.updated and finishes after draining queued PCM", async () =>
{
    var socket = new LoopbackRealtimeSocket();
    await using var client = new LiveTranslateClient(new() { WorkspaceId = "ws-test", Glossary = "" }, "synthetic-key", () => socket);
    client.Enqueue(new byte[3200]);
    var starting = client.StartAsync(CancellationToken.None);
    await socket.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(socket.AudioBytes == 0 && !starting.IsCompleted);
    socket.Update(); await starting;
    await client.StopAsync();
    Assert(socket.AudioBytes == 3200 && socket.Finished && client.AudioBytes == 3200);
});
Test("Old settings extra fields do not affect Aliyun-only credential loading", () =>
{
    var c = JsonSerializer.Deserialize<ApiCredentials>("""{"AliyunKey":"test","unusedLegacySecret":"ignored"}""")!;
    Assert(c.Complete && !JsonSerializer.Serialize(c).Contains("unusedLegacySecret"));
});
Test("Continuous flow bounds rendering memory without splitting graphemes or losing complete transcripts", () =>
{
    var now=DateTimeOffset.UtcNow;
    var entries=Enumerable.Range(0,40).Select(i=>new CaptionSegment(i,"Фраза "+i+".","第"+i+"句。",true,true,now.AddSeconds(i),[i])).ToArray();
    var state=new CaptionState(entries,[],"Самая новая фраза.",1,1);
    var flow=CaptionFlowComposer.Compose(state);
    Assert(flow.Russian.EndsWith("Самая новая фраза.")&&flow.Chinese.EndsWith("第39句。")&&!flow.Turned&&!flow.HasMore);
    Assert(state.Confirmed.Count==40&&state.Confirmed[0].Chinese=="第0句。");
    var longState=new CaptionState([new(1,new string('я',1000),string.Concat(Enumerable.Repeat("好👩‍🔬",300)),true,true,now,[1])],[],"",0,2);
    var tail=CaptionFlowComposer.Compose(longState);
    Assert(System.Globalization.StringInfo.ParseCombiningCharacters(tail.Russian).Length==600);
    Assert(System.Globalization.StringInfo.ParseCombiningCharacters(tail.Chinese).Length==400&&!char.IsLowSurrogate(tail.Chinese[0]));
});
Test("Both languages fill horizontal space without hard breaks at segment or provider boundaries", () =>
{
    var now=DateTimeOffset.UtcNow;
    var state=new CaptionState([new(1,"Первая\nфраза.","第一\n句。",true,true,now,[1]),new(2,"Вторая фраза.","第二句。",true,true,now.AddSeconds(1),[2])],[],"Ещё\nслова.",0,1);
    var flow=CaptionFlowComposer.Compose(state);
    Assert(flow.Russian=="Первая фраза. Вторая фраза. Ещё слова.");
    Assert(flow.Chinese=="第一句。第二句。");
});

var failed = 0;
foreach(var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); summary.Add(new { name, passed = true }); }
    catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); summary.Add(new { name, passed = false, error = ex.GetType().Name }); }
}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} passed");
var resultIndex = Array.IndexOf(args,"--report");
if(resultIndex>=0 && resultIndex+1<args.Length) AtomicFile.Write(args[resultIndex+1],JsonSerializer.Serialize(new { at=DateTimeOffset.Now,total=tests.Count,failed,tests=summary },new JsonSerializerOptions { WriteIndented=true }));
// Deliberately leave the isolated test directory for reproducible inspection; no real credentials are used.
return failed==0 ? 0 : 1;

sealed class FakeHttpHandler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => Task.FromResult(respond(request));
}

sealed class LoopbackRealtimeSocket : IRealtimeSocket
{
    private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
    public readonly TaskCompletionSource Configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AudioBytes; public bool Finished;
    public void Update() => events.Writer.TryWrite(Encoding.UTF8.GetBytes("{\"type\":\"session.updated\"}"));
    public Task ConnectAsync(Uri uri, CancellationToken token) => Task.CompletedTask;
    public Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(data); var root = doc.RootElement;
        switch(root.GetProperty("type").GetString()) {
            case "session.update": Configured.TrySetResult(); break;
            case "input_audio_buffer.append": AudioBytes += Convert.FromBase64String(root.GetProperty("audio").GetString()!).Length; break;
            case "session.finish": Finished = true; events.Writer.TryWrite(Encoding.UTF8.GetBytes("{\"type\":\"session.finished\"}")); break;
        }
        return Task.CompletedTask;
    }
    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token) => await events.Reader.ReadAsync(token);
    public void Abort() { }
    public void Dispose() { }
}
