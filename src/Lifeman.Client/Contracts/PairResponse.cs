using System.Text.Json.Serialization;

namespace Lifeman.Client.Contracts;

public sealed record PairResponse(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
