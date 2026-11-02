namespace CapabilityExplorer.Capabilities;

public sealed record PackageFeedDescriptor(
    string Id,
    string Kind,
    string Name,
    string Url,
    string? Scope,
    string? Username,
    bool HasCredential);

public sealed record PackageFeedCatalog(int Version, IReadOnlyList<PackageFeedDescriptor> Feeds);
