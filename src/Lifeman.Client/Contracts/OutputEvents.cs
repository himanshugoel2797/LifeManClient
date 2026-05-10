using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeman.Client.Contracts;

public sealed record OutputAction(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("invoke_tool")] string? InvokeTool,
    [property: JsonPropertyName("invoke_args")] JsonElement? InvokeArgs);

public sealed record OutputContent(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("body")] string? Body);

public sealed record OutputDeliver(
    [property: JsonPropertyName("output_id")] string OutputId,
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("urgency")] string Urgency,
    [property: JsonPropertyName("content")] OutputContent Content,
    [property: JsonPropertyName("actions")] IReadOnlyList<OutputAction> Actions,
    [property: JsonPropertyName("source_tool")] string? SourceTool,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("_seq")] long? Seq);

public sealed record OutputCancel(
    [property: JsonPropertyName("output_id")] string OutputId,
    [property: JsonPropertyName("delivery_id")] string? DeliveryId,
    [property: JsonPropertyName("device_id")] string? DeviceId,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("_seq")] long? Seq);

public sealed record PendingOutputsResponse(
    [property: JsonPropertyName("events")] IReadOnlyList<OutputDeliver> Events,
    [property: JsonPropertyName("cursor")] string? Cursor);

public static class LifemanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
