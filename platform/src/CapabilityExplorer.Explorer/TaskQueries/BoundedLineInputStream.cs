namespace CapabilityExplorer.Explorer;

/// <summary>Wraps stdin so an MCP stdio session is faulted the moment a single line exceeds the bound, instead of
/// buffering an unbounded line the way the SDK's own <c>StreamServerTransport.ReadLineAsync</c> does. Cancellation
/// and end-of-stream pass straight through to the underlying stream; this only ever adds a bound, never changes
/// what a read returns when it is within bounds.</summary>
public sealed class BoundedLineInputStream : Stream
{
    public const int DefaultMaxLineBytes = 65536;
    private readonly Stream inner;
    private readonly int maxLineBytes;
    private int _currentLineBytes;

    public BoundedLineInputStream(Stream inner) : this(inner, DefaultMaxLineBytes) { }

    public BoundedLineInputStream(Stream inner, int maxLineBytes)
    {
        this.inner = inner;
        this.maxLineBytes = maxLineBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    private void Track(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b == (byte)'\n') { _currentLineBytes = 0; continue; }
            if (++_currentLineBytes > maxLineBytes)
                throw new IOException($"stdio request line exceeds the {maxLineBytes}-byte bound; the session is faulted");
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
