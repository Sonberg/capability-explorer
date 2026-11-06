using System.Text.Json;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Cli;

internal static class NativeAdapterPlanningCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> options,
        IReadOnlyList<string> files)
    {
        if (files.Count != 0 || options.Count != 5 ||
            !options.TryGetValue("root", out var root) ||
            !options.TryGetValue("manifest", out var manifestPath) ||
            !options.TryGetValue("repository", out var repositoryId) ||
            !options.TryGetValue("commit", out var commitSha) ||
            !options.TryGetValue("tree", out var treeSha))
        {
            Console.Error.WriteLine("usage: ce adapter-plan --root <source-dir> --manifest <manifest.json> --repository <id> --commit <sha> --tree <sha>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var manifest = await NativeAdapterPlanner.ReadManifestAsync(
                Path.GetFullPath(manifestPath), cancellation.Token).ConfigureAwait(false);
            var plan = await NativeAdapterPlanner.PlanAsync(
                Path.GetFullPath(root), repositoryId, commitSha, treeSha, manifest, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(plan, Json));
            return plan.EnumerationComplete && plan.UnknownCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("adapter-plan: cancelled; no complete plan was produced");
            return 130;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"adapter-plan: failed ({error.GetType().Name}); verify the bounded manifest and local source root");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
