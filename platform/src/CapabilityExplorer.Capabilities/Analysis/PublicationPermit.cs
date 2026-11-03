namespace CapabilityExplorer.Capabilities.Analysis;

/// <summary>Serializes cancellation with all publication effects. Disposing incomplete work preserves ambiguity.</summary>
public sealed class PublicationPermit : IDisposable
{
    private readonly FileRunStore _store;
    private readonly WorkerOwnership _owner;
    private readonly FileStream _gate;
    private AnalysisRun _run;
    private bool _closed;
    internal PublicationPermit(FileRunStore store, WorkerOwnership owner, AnalysisRun run, FileStream gate)
    { _store = store; _owner = owner; _run = run; _gate = gate; }
    private void Validate()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _store.Owner(_owner);
        var current = _store.Attempt(new(_run.RunId, _run.AttemptId!));
        if (current.State != "publishing" || current.ManifestHash != _run.ManifestHash || current.AcceptedInputsHash != _run.AcceptedInputsHash)
            throw new InvalidOperationException("publication ownership or inputs changed");
        _store.VerifyAccepted(current);
        var resolved = Path.Combine(_store.RunDirectory(_run.RunId), "resolved.json");
        if (AnalysisPaths.Hash(AnalysisPaths.ReadBounded(resolved)) != _run.ManifestHash)
            throw new InvalidDataException("resolved inputs changed during publication");
    }
    /// <summary>Binds the caller's publication evidence to this permit before any external effect.</summary>
    public void RequireIdentity(AttemptIdentity identity, string acceptedInputsHash, string manifestHash)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.RunId != _run.RunId || identity.AttemptId != _run.AttemptId ||
            acceptedInputsHash != _run.AcceptedInputsHash || manifestHash != _run.ManifestHash)
            throw new InvalidOperationException("publication evidence belongs to another run, attempt or immutable input manifest");
    }
    public void Intent(string surface, string destination)
    {
        Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (surface.Length > 100 || destination.Length > 4096) throw new ArgumentException("publication effect exceeds bounds");
        var effects = _run.Effects ?? [];
        if (effects.Count >= 1000 || effects.Any(effect => effect.Surface == surface && effect.Destination == destination))
            throw new InvalidOperationException("duplicate or excessive publication effect");
        _run = _run with { Effects = [.. effects, new PublicationEffect(surface, destination, "pending", null)] };
        _store.Save(_run);
    }
    public void Confirm(string surface, string destination, string evidenceHash)
    {
        Validate();
        if (evidenceHash.Length != 64 || !evidenceHash.All(char.IsAsciiHexDigit)) throw new ArgumentException("expected SHA-256 evidence hash", nameof(evidenceHash));
        var effects = (_run.Effects ?? []).ToList();
        var index = effects.FindIndex(effect => effect.Surface == surface && effect.Destination == destination && effect.State == "pending");
        if (index < 0) throw new InvalidOperationException("publication intent missing or already confirmed");
        effects[index] = effects[index] with { State = "confirmed", EvidenceHash = evidenceHash };
        _run = _run with { Effects = effects };
        _store.Save(_run);
    }
    public AnalysisRun Complete(string? detail = null)
    {
        Validate();
        if (_run.Effects is not { Count: > 0 } || _run.Effects.Any(effect => effect.State != "confirmed"))
            throw new InvalidOperationException("publication effects are incomplete");
        _run = _run with { State = "done", Detail = detail };
        _store.Save(_run);
        _closed = true;
        _owner.Release(new(_run.RunId, _run.AttemptId!));
        _gate.Dispose();
        return _run;
    }
    public void Dispose()
    {
        if (_closed) return;
        try
        {
            // Save only if the current record still belongs to this publishing attempt.
            var current = _store.Attempt(new(_run.RunId, _run.AttemptId!));
            if (current.State == "publishing")
                _store.Save(current with { State = "unknown", Detail = "publication interrupted; effects require reconciliation" });
        }
        finally
        {
            _owner.Release(new(_run.RunId, _run.AttemptId!));
            _closed = true;
            _gate.Dispose();
        }
    }
}
