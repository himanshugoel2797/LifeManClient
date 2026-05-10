using Lifeman.Client.Contracts;

namespace Lifeman.Client.Renderers;

/// Maps a delivered output to a platform notification. The platform head
/// owns the impl (NotificationCompat.Builder on Android, AppNotification on
/// Windows). The shared core only deals in the abstract contract.
public interface IRenderer
{
    Task ShowAsync(OutputDeliver deliver, CancellationToken ct);
    Task DismissAsync(string outputId, CancellationToken ct);
}
