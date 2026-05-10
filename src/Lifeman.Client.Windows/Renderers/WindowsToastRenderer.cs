using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Renderers;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace Lifeman.Client.Windows.Renderers;

/// Renders kernel `output.deliver` events as native Windows toasts.
/// Action buttons are wired to fire HTTP requests back to
/// /api/outputs/{id}/respond via OutputResponseClient.
///
/// Uses the legacy ToastNotificationManagerCompat surface from
/// Microsoft.Toolkit.Uwp.Notifications because it works **unpackaged**
/// (sideloaded console exe) — WinAppSDK's AppNotificationManager
/// requires either an MSIX or explicit COM registration we'd otherwise
/// have to wire by hand.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsToastRenderer : IRenderer
{
    private readonly OutputResponseClient _responses;

    // Track the toast tag for each delivered output so we can dismiss on cancel.
    private readonly ConcurrentDictionary<string, string> _tags = new();

    public WindowsToastRenderer(OutputResponseClient responses)
    {
        _responses = responses;

        // Each toast carries its output_id + action_label in the argument
        // string. We listen once at startup; the toolkit dispatches
        // activations even when the app was launched by COM activation
        // (i.e. when a user clicks a toast that arrived while the app was
        // not running and Windows auto-starts the registered exe).
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    public Task ShowAsync(OutputDeliver deliver, CancellationToken ct)
    {
        var builder = new ToastContentBuilder()
            .AddText(deliver.Content.Title ?? deliver.Category)
            .AddText(deliver.Content.Body ?? string.Empty);

        foreach (var action in deliver.Actions)
        {
            // Argument shape: "outputId=…&action=…". Keep it compact to
            // stay under the toast XML argument-length limits.
            var args = new ToastArguments()
                .Add("outputId", deliver.OutputId)
                .Add("action", action.Label);
            builder.AddButton(new ToastButton().SetContent(action.Label).AddArgument("outputId", deliver.OutputId).AddArgument("action", action.Label));
        }

        // Use the output_id as both Tag and Group so cancel can dismiss
        // by these identifiers without remembering the toast object.
        var tag = ShortTag(deliver.OutputId);
        _tags[deliver.OutputId] = tag;
        builder.Show(toast =>
        {
            toast.Tag = tag;
            toast.Group = "lifeman";
            if (deliver.ExpiresAt is { } exp) toast.ExpirationTime = exp;
        });
        return Task.CompletedTask;
    }

    public Task DismissAsync(string outputId, CancellationToken ct)
    {
        if (_tags.TryRemove(outputId, out var tag))
        {
            try { ToastNotificationManagerCompat.History.Remove(tag, "lifeman"); }
            catch { /* nothing we can do; toast may already be gone */ }
        }
        return Task.CompletedTask;
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Toolkit decodes our query-style argument string into ToastArguments.
        var args = ToastArguments.Parse(e.Argument);
        var outputId = args.Get("outputId");
        var actionLabel = args.Get("action");
        if (string.IsNullOrEmpty(outputId) || string.IsNullOrEmpty(actionLabel)) return;

        // Fire and forget — the renderer doesn't own a cancellation
        // token from the host loop here (toast activations can fire
        // even from a separately-launched activation process), so we
        // use the response client with its own timeout instead.
        _ = Task.Run(async () =>
        {
            try { await _responses.RespondAsync(outputId, actionLabel).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[toast] respond failed: {ex.Message}"); }
        });
    }

    private static string ShortTag(string outputId)
        // ToastNotification.Tag is capped at 64 chars; output_ids are typically
        // already short, but truncate just in case (and prefer the prefix —
        // matches the kernel UI's display of the id).
        => outputId.Length <= 64 ? outputId : outputId[..64];
}
