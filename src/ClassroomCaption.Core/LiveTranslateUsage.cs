using System.Text.Json;

namespace ClassroomCaption.Core;

public sealed record TranslationUsage(string ResponseId, long? AudioInputTokens, long? ChineseOutputTokens, int Connection = 1);

// Missing vendor fields remain unknown; never substitute totals or duration estimates.
public sealed class LiveTranslateUsage
{
    private readonly HashSet<string> seen = [];
    private int connection = 1;
    public void BeginConnection() { seen.Clear(); connection++; }
    public long? AudioInputTokens { get; private set; }
    public long? ChineseOutputTokens { get; private set; }
    public int AudioInputMissingResponses { get; private set; }
    public int ChineseOutputMissingResponses { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public event Action<TranslationUsage>? Recorded;

    public void Accept(JsonElement e)
    {
        if (!e.TryGetProperty("type", out var type) || type.GetString() != "response.done" ||
            !e.TryGetProperty("response", out var response)) return;
        var id = response.TryGetProperty("id", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(id) || !seen.Add(id)) return;
        response.TryGetProperty("usage", out var usage);
        long? Number(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var result) && result >= 0 ? result : null;
        long? Detail(string group, string name) => usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty(group, out var details) ? Number(details, name) : null;
        InputTokens += Number(usage, "input_tokens") ?? 0;
        OutputTokens += Number(usage, "output_tokens") ?? 0;
        var audio = Detail("input_tokens_details", "audio_tokens");
        var chinese = Detail("output_tokens_details", "text_tokens");
        if (audio is { } a) AudioInputTokens = (AudioInputTokens ?? 0) + a;
        else AudioInputMissingResponses++;
        if (chinese is { } c) ChineseOutputTokens = (ChineseOutputTokens ?? 0) + c;
        else ChineseOutputMissingResponses++;
        Recorded?.Invoke(new(id, audio, chinese, connection));
    }
}
