namespace EagleDeskPet.Mcp;

/// <summary>Stops oversized newline-framed JSON-RPC messages before the SDK accumulates them.</summary>
internal sealed class BoundedStdinStream(Stream inner) : Stream
{
    private int _lineBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var read = await inner.ReadAsync(buffer, token).AsTask().WaitAsync(token).ConfigureAwait(false);
        Check(buffer[..read]);
        return read;
    }
    private void Check(ReadOnlyMemory<byte> memory)
    {
        foreach (var value in memory.Span)
        {
            if (value == (byte)'\n') _lineBytes = 0;
            else if (++_lineBytes > 64 * 1024) throw new InvalidDataException("MCP input line exceeds 64 KiB.");
        }
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
