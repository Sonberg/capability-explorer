using System.Data;
using System.Text.Json;
using Npgsql;

namespace CapabilityExplorer.Store;

/// <summary>Human assertions only. No imported artifact, node or evidence row is changed.</summary>
public sealed partial class GlossaryStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<GlossaryMutationResult<GlossaryTerm>> SaveTermAsync(GlossaryTermWrite value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Term(value);
        return SaveAsync(Entity.Term, value.Id, null, value, value.Reviewer, expectedVersion, Term, cancellationToken);
    }

    public Task<GlossaryMutationResult<GlossaryLink>> SaveLinkAsync(GlossaryLinkWrite value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Link(value);
        return SaveAsync(Entity.Link, value.Id, value.TermId, value, value.Reviewer, expectedVersion, Link, cancellationToken);
    }

    public Task<GlossaryMutationResult<GlossaryIssue>> SaveIssueAsync(GlossaryIssueWrite value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Issue(value);
        return SaveAsync(Entity.Issue, value.Id, value.TermId, value, value.Reviewer, expectedVersion, Issue, cancellationToken);
    }

    public Task<GlossaryMutationResult<GlossaryTerm>> DeleteTermAsync(Guid id, long expectedVersion, string reviewer, CancellationToken cancellationToken = default) =>
        DeleteAsync(Entity.Term, id, expectedVersion, reviewer, Term, cancellationToken);

    public Task<GlossaryMutationResult<GlossaryLink>> DeleteLinkAsync(Guid id, long expectedVersion, string reviewer, CancellationToken cancellationToken = default) =>
        DeleteAsync(Entity.Link, id, expectedVersion, reviewer, Link, cancellationToken);

    public Task<GlossaryMutationResult<GlossaryIssue>> DeleteIssueAsync(Guid id, long expectedVersion, string reviewer, CancellationToken cancellationToken = default) =>
        DeleteAsync(Entity.Issue, id, expectedVersion, reviewer, Issue, cancellationToken);

    /// <summary>Returns the retained row, including its Deleted flag; null means the ID never existed.</summary>
    public Task<GlossaryTerm?> GetTermAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync(Entity.Term, id, Term, cancellationToken);

    /// <summary>Returns the retained row, including its Deleted flag; null means the ID never existed.</summary>
    public Task<GlossaryLink?> GetLinkAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync(Entity.Link, id, Link, cancellationToken);

    /// <summary>Returns the retained row, including its Deleted flag; null means the ID never existed.</summary>
    public Task<GlossaryIssue?> GetIssueAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync(Entity.Issue, id, Issue, cancellationToken);

    /// <summary>Each collection has its own bound and truncation flag. Deleted records remain in audit, not active lists.</summary>
    public async Task<GlossarySnapshot> GetSnapshotAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Limit(limit);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var terms = await ListAsync(connection, tx, Entity.Term, limit, Term, cancellationToken);
        var links = await ListAsync(connection, tx, Entity.Link, limit, Link, cancellationToken);
        var issues = await ListAsync(connection, tx, Entity.Issue, limit, Issue, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(terms.Items, links.Items, issues.Items, limit, terms.Truncated, links.Truncated, issues.Truncated);
    }

    public async Task<GlossaryAuditPage> GetAuditAsync(Guid entityId, int limit = 1000, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Id(entityId);
        GlossaryValidation.Limit(limit);
        await using var command = dataSource.CreateCommand("select sequence, entity_type, entity_id, version, action, actor, data::text, recorded_at from glossary_audit where entity_id = $1 order by sequence limit $2");
        command.Parameters.AddWithValue(entityId);
        command.Parameters.AddWithValue(limit + 1);
        var events = new List<GlossaryAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        var truncated = events.Count > limit;
        if (truncated) events.RemoveAt(limit);
        return new(events, limit, truncated);
    }

    private static GlossaryTerm Term(Row row)
    {
        var v = JsonSerializer.Deserialize<GlossaryTermWrite>(row.Json, JsonOptions)!;
        return new(v.Id, v.Name, v.Definition, v.Group, v.Reviewer, row.Version, row.Deleted);
    }

    private static GlossaryLink Link(Row row)
    {
        var v = JsonSerializer.Deserialize<GlossaryLinkWrite>(row.Json, JsonOptions)!;
        return new(v.Id, v.TermId, v.RepositoryId, v.UnitPath, v.SymbolKey, v.Role, v.Note, v.Observed, v.Reviewer, row.Version, row.Deleted);
    }

    private static GlossaryIssue Issue(Row row)
    {
        var v = JsonSerializer.Deserialize<GlossaryIssueWrite>(row.Json, JsonOptions)!;
        return new(v.Id, v.TermId, v.Kind, v.Summary, v.Note, v.Status, v.Reviewer, row.Version, row.Deleted);
    }
}
