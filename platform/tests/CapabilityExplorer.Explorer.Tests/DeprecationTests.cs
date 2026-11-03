using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class DeprecationTests
{
    [Fact]
    public async Task Database_failure_is_propagated_without_attempting_a_file_fallback()
    {
        var service = new DeprecationService(new UnavailableRepository());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync());
    }

    [Fact]
    public async Task Version_is_returned_as_part_of_the_API_mark_contract()
    {
        var service = new DeprecationService(new SingleMarkRepository());

        var mark = Assert.Single(await service.ListAsync());

        Assert.Equal("deprecated", mark.Status);
        Assert.Equal(7, mark.Version);
        Assert.Null(mark.Removal);
    }

    private sealed class UnavailableRepository : IDeprecationRepository
    {
        public Task<IReadOnlyList<DeprecationMark>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<DeprecationMark> UpsertAsync(DeprecationUpsert value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task DeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<DeprecationImportReport> ImportLegacyAsync(Stream json, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database unavailable");
    }

    private sealed class SingleMarkRepository : IDeprecationRepository
    {
        public Task<IReadOnlyList<DeprecationMark>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeprecationMark>>(
                [new("group|http|Klarna", DeprecationStatus.Deprecated, "moving to Adyen", DateTimeOffset.UnixEpoch, 7)]);

        public Task<DeprecationMark> UpsertAsync(DeprecationUpsert value, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<DeprecationImportReport> ImportLegacyAsync(Stream json, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
