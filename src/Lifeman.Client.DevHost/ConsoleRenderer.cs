using Lifeman.Client.Contracts;
using Lifeman.Client.Renderers;

namespace Lifeman.Client.DevHost;

/// Logs each delivered output to stdout. Native notifications come once the
/// MAUI heads land; for the end-to-end smoke test the console is enough.
public sealed class ConsoleRenderer : IRenderer
{
    public Task ShowAsync(OutputDeliver deliver, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"=== output.deliver [{deliver.Urgency}/{deliver.Category}] {deliver.OutputId}");
        if (!string.IsNullOrEmpty(deliver.Content.Title)) Console.WriteLine($"  title: {deliver.Content.Title}");
        if (!string.IsNullOrEmpty(deliver.Content.Body)) Console.WriteLine($"  body:  {deliver.Content.Body}");
        if (deliver.SourceTool is not null) Console.WriteLine($"  tool:  {deliver.SourceTool}");
        foreach (var action in deliver.Actions)
            Console.WriteLine($"  action: {action.Label}");
        Console.WriteLine();
        return Task.CompletedTask;
    }

    public Task DismissAsync(string outputId, CancellationToken ct)
    {
        Console.WriteLine($"=== output.cancel {outputId}");
        return Task.CompletedTask;
    }
}
