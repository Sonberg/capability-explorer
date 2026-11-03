using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace CapabilityExplorer.Store;

public sealed record GlossaryProposalReviewWrite(Guid Id, string InputHash, string Decision, string Reviewer, JsonElement Data);

public sealed partial class GlossaryStore
{
    public async Task<GlossaryProposalReviewWrite?> GetProposalReviewAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GlossaryValidation.Id(id);
        await using var command = dataSource.CreateCommand("select input_hash, decision, reviewer, data::text from glossary_proposal_review where id = $1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        using var document = JsonDocument.Parse(reader.GetString(3));
        return new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), document.RootElement.Clone());
    }

    /// <summary>A decision and its accepted term/links commit together; rejection creates no assertions.</summary>
    public async Task<GlossaryMutationResult<GlossaryProposalReviewWrite>> ReviewProposalAsync(
        GlossaryProposalReviewWrite review, GlossaryTermWrite? term, IReadOnlyList<GlossaryLinkWrite> links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(links);
        GlossaryValidation.Id(review.Id);
        GlossaryValidation.Reviewer(review.Reviewer);
        if (review.InputHash is null || review.InputHash.Length != 64 || review.InputHash.Any(c => !char.IsAsciiHexDigitLower(c))
            || review.Decision is not ("accepted" or "rejected") || review.Data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid proposal review.", nameof(review));
        if (review.Decision == "rejected" ? term is not null || links.Count != 0 : term is null || links.Count is < 1 or > 200)
            throw new ArgumentException("Acceptance requires a term and reviewed links; rejection cannot create assertions.", nameof(review));
        if (term is not null)
        {
            GlossaryValidation.Term(term);
            if (term.Id != review.Id || term.Reviewer != review.Reviewer)
                throw new ArgumentException("Term must match the review identity and reviewer.", nameof(term));
        }
        foreach (var link in links)
        {
            GlossaryValidation.Link(link);
            if (link.TermId != review.Id || link.Reviewer != review.Reviewer)
                throw new ArgumentException("Links must match the review term and reviewer.", nameof(links));
        }
        if (links.Select(l => l.Id).Distinct().Count() != links.Count
            || links.Select(l => l.Observed.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != links.Count)
            throw new ArgumentException("Reviewed links must be distinct.", nameof(links));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, tx, cancellationToken);
        await using (var existing = new NpgsqlCommand("select input_hash, decision, reviewer, data::text from glossary_proposal_review where id = $1", connection, tx))
        {
            existing.Parameters.AddWithValue(review.Id);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var same = reader.GetString(0) == review.InputHash && reader.GetString(1) == review.Decision
                    && reader.GetString(2) == review.Reviewer
                    && JsonNode.DeepEquals(JsonNode.Parse(reader.GetString(3)), JsonNode.Parse(review.Data.GetRawText()));
                return new(same ? GlossaryMutationStatus.Unchanged : GlossaryMutationStatus.Conflict, review);
            }
        }
        if (term is not null)
        {
            if (await ReadAsync(connection, tx, Entity.Term, term.Id, cancellationToken) is not null)
                return new(GlossaryMutationStatus.Conflict, review);
            foreach (var link in links)
                if (await ReadAsync(connection, tx, Entity.Link, link.Id, cancellationToken) is not null)
                    return new(GlossaryMutationStatus.Conflict, review);
            var row = new Row(JsonSerializer.Serialize(term, JsonOptions), 1, false, null);
            await WriteAsync(connection, tx, Entity.Term, term.Id, row, true, cancellationToken);
            await AuditAsync(connection, tx, Entity.Term, term.Id, row, "create", review.Reviewer, cancellationToken);
            foreach (var link in links)
            {
                var child = new Row(JsonSerializer.Serialize(link, JsonOptions), 1, false, term.Id);
                await WriteAsync(connection, tx, Entity.Link, link.Id, child, true, cancellationToken);
                await AuditAsync(connection, tx, Entity.Link, link.Id, child, "create", review.Reviewer, cancellationToken);
            }
        }
        await using var command = new NpgsqlCommand(
            "insert into glossary_proposal_review (id, input_hash, decision, reviewer, data) values ($1, $2, $3, $4, $5)", connection, tx);
        command.Parameters.AddWithValue(review.Id);
        command.Parameters.AddWithValue(review.InputHash);
        command.Parameters.AddWithValue(review.Decision);
        command.Parameters.AddWithValue(review.Reviewer);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = review.Data.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(GlossaryMutationStatus.Created, review);
    }
}
