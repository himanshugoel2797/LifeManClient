using Lifeman.Client.Outbox;

namespace Lifeman.Client.Windows;

/// Tiny holder for state the host loop wants to expose to event
/// handlers (e.g. WindowsToastRenderer's Dismissed callback) without
/// plumbing references through constructors. Mirrors the Android head's
/// LifemanService.CurrentOutbox pattern.
public static class RuntimeState
{
    public static IOutbox? CurrentOutbox { get; set; }
}
