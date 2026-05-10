using System.Text.Json.Serialization;

namespace Lifeman.Client.Contracts;

public sealed record InputEvent(
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("raw_payload")] string RawPayload,
    [property: JsonPropertyName("intent_hint")] string? IntentHint = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("sensitivity")] string? Sensitivity = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record InputBatchRequest(
    [property: JsonPropertyName("events")] IReadOnlyList<InputEvent> Events);

public sealed record InputBatchItemResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("response")] InputAcceptedResponse? Response);

public sealed record InputBatchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<InputBatchItemResult> Results);

public sealed record InputAcceptedResponse(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("dispatched")] IReadOnlyList<string> Dispatched,
    [property: JsonPropertyName("dropped")] IReadOnlyList<string> Dropped,
    [property: JsonPropertyName("expired")] bool Expired);
