using System.Text.Json.Serialization;

namespace Lifeman.Client.Contracts;

public sealed record DeviceCapabilities(
    [property: JsonPropertyName("rich_content")] bool RichContent,
    [property: JsonPropertyName("images")] bool Images,
    [property: JsonPropertyName("actions")] bool Actions,
    [property: JsonPropertyName("persistence")] bool Persistence,
    [property: JsonPropertyName("interruption_level")] string InterruptionLevel,
    [property: JsonPropertyName("typical_latency_ms")] int TypicalLatencyMs);
