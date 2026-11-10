using System.Text.Json.Serialization;

namespace ClassroomCaption.Core;

// Only explicit identifiers and anomalous subtitle text, never headers or credentials.
public sealed record TranslationTrace(int Connection, string EventType, string SessionId,
    string ConversationId, string EventId, string RequestId, string TaskId,
    string ResponseId, string ItemId, string? MarkerText,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ErrorType = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ErrorDetail = null);
