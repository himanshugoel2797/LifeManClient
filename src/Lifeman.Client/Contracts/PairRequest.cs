using System.Text.Json.Serialization;

namespace Lifeman.Client.Contracts;

public sealed record PairRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("capabilities")] DeviceCapabilities Capabilities);
