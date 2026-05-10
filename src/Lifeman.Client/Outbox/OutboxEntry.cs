using Lifeman.Client.Contracts;

namespace Lifeman.Client.Outbox;

public sealed record OutboxEntry(
    long Id,
    string Surface,
    string PayloadJson,
    DateTimeOffset EmittedAt,
    int Attempts,
    string? LastError)
{
    public InputEvent ToInputEvent(string? source) => new(
        Surface: Surface,
        RawPayload: PayloadJson,
        Source: source);
}
