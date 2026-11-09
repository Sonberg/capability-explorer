using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Interpretation.BoundedClassification;

namespace CapabilityExplorer.Cli;

internal static class ClassificationBenchmark
{
    private const int Warmup = 5_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Run(IReadOnlyDictionary<string, string> options, IReadOnlyList<string> files)
    {
        string[] required = ["artifact", "sha256", "corpus", "source", "samples", "concurrency", "json"];
        if (files.Count != 0 || options.Count != required.Length || required.Any(key => !options.ContainsKey(key)) ||
            !int.TryParse(options["samples"], NumberStyles.None, CultureInfo.InvariantCulture, out var samples) ||
            samples is < 1_000 or > 100_000 ||
            !int.TryParse(options["concurrency"], NumberStyles.None, CultureInfo.InvariantCulture, out var concurrency) ||
            concurrency is < 1 or > 4 || !Hex(options["source"], 40))
        {
            Console.Error.WriteLine("classification-benchmark: requires artifact, sha256, corpus, source commit, samples 1000..100000, concurrency 1..4 and a new json report path");
            return 2;
        }
        try
        {
            var artifactBytes = ReadBounded(options["artifact"]);
            var corpusBytes = ReadBounded(options["corpus"]);
            var coldStart = Stopwatch.GetTimestamp();
            var loaded = ClassificationArtifact<SyntheticLabel>.Load(artifactBytes, options["sha256"]);
            var coldMilliseconds = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
            if (loaded.Artifact is not { } artifact)
                throw new ArgumentException($"Artifact load failed: {loaded.Reason}");
            var corpus = ReadCorpus(corpusBytes);
            var slot = new ClassificationSlot<SyntheticLabel>();
            if (slot.TryActivate(artifact, null) != ClassificationReason.None)
                throw new InvalidOperationException("Initial artifact activation failed");
            var conformance = corpus.Cases.Select(item =>
            {
                var result = slot.Classify(ClassificationArtifact<SyntheticLabel>.InputSchema, artifact.ArtifactId, item.Features);
                if (result.Outcome.ToString() != item.Outcome || result.Reason.ToString() != item.Reason ||
                    result.SelectedClass?.ToString() != item.SelectedClass)
                    throw new ArgumentException($"Synthetic oracle disagreement: {item.Id}");
                return new { item.Id, result.Outcome, result.Reason, result.SelectedClass, result.Scores };
            }).ToArray();
            var warmStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < Warmup; i++)
                slot.Classify(ClassificationArtifact<SyntheticLabel>.InputSchema, artifact.ArtifactId, corpus.Cases[i % corpus.Cases.Length].Features);
            var warmupMilliseconds = Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var memoryBefore = process.WorkingSet64;
            int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            var measurements = new Measurement[samples];
            var workers = Enumerable.Range(0, concurrency).Select(_ => new WorkerTotals()).ToArray();
            using var ready = new Barrier(concurrency + 1);
            var tasks = Enumerable.Range(0, concurrency).Select(worker => Task.Factory.StartNew(() =>
            {
                workers[worker].ThreadId = Environment.CurrentManagedThreadId;
                ready.SignalAndWait();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = worker; i < samples; i += concurrency)
                {
                    var item = corpus.Cases[i % corpus.Cases.Length];
                    var tick = Stopwatch.GetTimestamp();
                    var result = slot.Classify(ClassificationArtifact<SyntheticLabel>.InputSchema, artifact.ArtifactId, item.Features);
                    var elapsed = Stopwatch.GetTimestamp() - tick;
                    measurements[i] = new(elapsed, result.Outcome, result.Reason);
                    if (result.Outcome is ClassificationOutcome.ValidationFailure or ClassificationOutcome.Unavailable)
                        workers[worker].Errors++;
                    if (result.Outcome == ClassificationOutcome.Abstain) workers[worker].Abstentions++;
                }
                workers[worker].AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var start = Stopwatch.GetTimestamp();
            ready.SignalAndWait();
            Task.WaitAll(tasks);
            var duration = Stopwatch.GetElapsedTime(start);
            process.Refresh();
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var memoryAfter = process.WorkingSet64;
            int[] gcAfter = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            var sorted = measurements.Select(item => item.Ticks * 1_000d / Stopwatch.Frequency).Order().ToArray();
            var p99 = Percentile(sorted, 0.99);
            var errors = workers.Sum(worker => worker.Errors);
            var report = new
            {
                schema = "ce.classification-benchmark.v1",
                purpose = "Synthetic software qualification; not product parity, calibration or production capacity",
                sourceRevisionDeclaredByCaller = options["source"],
                cliAssemblySha256 = HashAssembly(typeof(ClassificationBenchmark).Assembly.Location),
                classifierAssemblySha256 = HashAssembly(typeof(ClassificationSlot<SyntheticLabel>).Assembly.Location),
                artifactId = artifact.ArtifactId, corpusSha256 = Hash(corpusBytes),
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processors = Environment.ProcessorCount, precision = "scalar-f64",
                scoreKind = "normalized-score", numericTolerance = 1e-12,
                workload = new
                {
                    features = artifact.Features.Length, classes = artifact.Classes.Length, samples, concurrency,
                    workerThreads = workers.Select(worker => worker.ThreadId).Distinct().Count(),
                    offeredLoad = "closed-loop saturated, no rate-controlled arrival process",
                    warmupDecisions = Warmup, durationSeconds = duration.TotalSeconds,
                    boundary = "preparsed structured input validation -> atomic artifact snapshot -> scoring -> typed mapping",
                    excluded = "file reads, JSON parsing, source feature extraction, downstream integration, cold load and warmup",
                    percentileMethod = "nearest rank ceil(p*n), raw ticks retained",
                    stopwatchFrequency = Stopwatch.Frequency
                },
                cold = new { artifactLoadMilliseconds = coldMilliseconds, warmupMilliseconds },
                metrics = new
                {
                    p50Milliseconds = Percentile(sorted, 0.50), p95Milliseconds = Percentile(sorted, 0.95),
                    p99Milliseconds = p99, throughputPerSecond = samples / duration.TotalSeconds,
                    allocatedBytes = workers.Sum(worker => worker.AllocatedBytes),
                    allocatedBytesPerDecision = workers.Sum(worker => worker.AllocatedBytes) / (double)samples,
                    processCpuMilliseconds = cpuMilliseconds, workingSetBefore = memoryBefore, workingSetAfter = memoryAfter,
                    workingSetPeak = (long?)null, gcCollections = gcAfter.Zip(gcBefore, (after, before) => after - before).ToArray(),
                    errors, timeouts = 0, abstentions = workers.Sum(worker => worker.Abstentions)
                },
                quality = new { contractCases = conformance.Length, disagreements = 0, conformance,
                    representativeAccuracy = (double?)null, calibration = "not claimed", reference = "frozen synthetic expected outcomes" },
                adoption = "defer remote replacement: actual contract, representative corpus, owner approval and operating baseline unavailable",
                operatingCost = new { remoteBaseline = (double?)null, candidateHostingCost = (double?)null, savings = (double?)null },
                warmedTargetMilliseconds = 1, warmedTargetPassed = p99 < 1 && errors == 0,
                rawSamples = measurements
            };
            var output = Path.GetFullPath(options["json"]);
            var encoded = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(encoded);
            Console.WriteLine($"classification-benchmark: {samples} decisions, {concurrency} workers, p99 {p99:F6} ms, {errors} errors; synthetic scope only");
            return p99 < 1 && errors == 0 ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"classification-benchmark: {error.Message}");
            return 1;
        }
    }

    private static Corpus ReadCorpus(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        ValidateMembers(document.RootElement, ["schema", "purpose", "cases"]);
        if (document.RootElement.GetProperty("cases") is not { ValueKind: JsonValueKind.Array } cases ||
            cases.GetArrayLength() is < 1 or > 256) throw new JsonException("Corpus requires 1..256 cases");
        foreach (var item in cases.EnumerateArray())
        {
            ValidateMembers(item, ["id", "features", "outcome", "reason", "selectedClass"]);
            if (item.GetProperty("features") is not { ValueKind: JsonValueKind.Array } features ||
                features.GetArrayLength() is < 1 or > 128) throw new JsonException("Corpus vectors require 1..128 features");
        }
        var corpus = JsonSerializer.Deserialize<Corpus>(bytes, Json) ?? throw new JsonException("Corpus cannot be null");
        if (corpus.Schema != "ce.classification-corpus.v1" || string.IsNullOrWhiteSpace(corpus.Purpose) ||
            corpus.Cases.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.Features.Any(value => !double.IsFinite(value))) ||
            corpus.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != corpus.Cases.Length)
            throw new JsonException("Invalid synthetic corpus identity or values");
        return corpus;
    }

    private static void ValidateMembers(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a corpus object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name) ||
                property.Value.ValueKind == JsonValueKind.Null && property.Name != "selectedClass")
                throw new JsonException("Unknown, duplicate or null corpus property");
        if (seen.Count != names.Length) throw new JsonException("Missing corpus property");
    }

    private static byte[] ReadBounded(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (file.LinkTarget is not null || file.Length > 128 * 1024) throw new IOException("Input must be a regular file of at most 128 KiB");
        using var stream = file.OpenRead();
        var buffer = new byte[128 * 1024 + 1];
        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count == buffer.Length) throw new IOException("Input exceeds 128 KiB");
        return buffer[..count];
    }

    private static string HashAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Hex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static double Percentile(double[] sorted, double quantile) => sorted[(int)Math.Ceiling(quantile * sorted.Length) - 1];
    private enum SyntheticLabel { Alpha, Beta, Gamma }
    private sealed record Corpus(string Schema, string Purpose, CorpusCase[] Cases);
    private sealed record CorpusCase(string Id, double[] Features, string Outcome, string Reason, string? SelectedClass);
    private readonly record struct Measurement(long Ticks, ClassificationOutcome Outcome, ClassificationReason Reason);
    private sealed class WorkerTotals
    {
        public long AllocatedBytes { get; set; }
        public int ThreadId { get; set; }
        public int Errors { get; set; }
        public int Abstentions { get; set; }
    }
}
