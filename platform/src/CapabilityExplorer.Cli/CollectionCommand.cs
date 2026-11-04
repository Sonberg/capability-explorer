using System.Text.Json;
using CapabilityExplorer.Contracts.Collection;

namespace CapabilityExplorer.Cli;

internal static class CollectionCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string command,
        IReadOnlyDictionary<string, string> options, IReadOnlyList<string> files)
    {
        var export = command == "collect";
        if (export
            ? files.Count != 0 || options.Count != 2 || !options.ContainsKey("plan") || !options.ContainsKey("out")
            : files.Count != 1 || options.Count != 0)
        {
            Console.Error.WriteLine("usage: ce collect --plan <plan.json> --out <directory> | ce validate-collection <directory>");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var result = export
                ? await CollectionExporter.ExportAsync(Path.GetFullPath(options["plan"]),
                    Path.GetFullPath(options["out"]), cancellation.Token)
                : await CollectionExporter.ValidateAsync(Path.GetFullPath(files[0]), cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            return result.IsValid ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"{command}: cancelled; no successful collection receipt");
            return 130;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"{command}: failed ({error.GetType().Name}); verify bounded local paths and the declared collection plan");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
