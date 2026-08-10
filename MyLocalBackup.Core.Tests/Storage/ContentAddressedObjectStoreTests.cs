using System.Security.Cryptography;
using System.Text;
using MyLocalBackup.Core.Storage;

namespace MyLocalBackup.Core.Tests.Storage;

public sealed class ContentAddressedObjectStoreTests
{
    [Fact]
    public async Task PutAsync_SameLengthDifferentBytes_ProducesDifferentObjects()
    {
        using var temp = new TestDirectory();
        var store = new ContentAddressedObjectStore(temp.Path);

        var left = await PutTextAsync(store, "AAAA");
        var right = await PutTextAsync(store, "BBBB");

        Assert.Equal(left.Length, right.Length);
        Assert.NotEqual(left.Hash, right.Hash);
        Assert.NotEqual(store.GetObjectPath(left), store.GetObjectPath(right));
    }

    [Fact]
    public async Task PutAsync_IdenticalBytes_DeduplicatesToOneImmutableObject()
    {
        using var temp = new TestDirectory();
        var store = new ContentAddressedObjectStore(temp.Path);

        var first = await PutTextAsync(store, "same content");
        var second = await PutTextAsync(store, "same content");

        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(temp.GetPath("objects"), "*", SearchOption.AllDirectories));
        Assert.True(File.GetAttributes(store.GetObjectPath(first)).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task PutAsync_WhenSourceReadFails_LeavesNoCommittedOrStagedFile()
    {
        using var temp = new TestDirectory();
        var store = new ContentAddressedObjectStore(temp.Path);
        await using var source = new ThrowingReadStream(Encoding.UTF8.GetBytes("part then fail"), failAfterBytes: 4);

        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(source));

        Assert.Empty(Directory.EnumerateFiles(temp.GetPath("objects"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(temp.GetPath("staging"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PutAsync_WhenExistingHashPathIsCorrupt_RejectsInsteadOfOverwriting()
    {
        using var temp = new TestDirectory();
        var store = new ContentAddressedObjectStore(temp.Path);
        var bytes = Encoding.UTF8.GetBytes("expected bytes");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var descriptor = new StoredObject("sha256", hash, bytes.LongLength);
        var objectPath = store.GetObjectPath(descriptor);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllTextAsync(objectPath, "corrupt bytes!");

        await using var source = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<RepositoryCorruptionException>(() => store.PutAsync(source));

        Assert.Equal("corrupt bytes!", await File.ReadAllTextAsync(objectPath));
    }

    [Fact]
    public async Task VerifyAsync_DetectsChangedCommittedObject()
    {
        using var temp = new TestDirectory();
        var store = new ContentAddressedObjectStore(temp.Path);
        var stored = await PutTextAsync(store, "verified");
        var path = store.GetObjectPath(stored);
        File.SetAttributes(path, FileAttributes.Normal);
        await File.WriteAllTextAsync(path, "tampered");

        var result = await store.VerifyAsync(stored);

        Assert.False(result.IsValid);
        Assert.Contains("hash", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<StoredObject> PutTextAsync(ContentAddressedObjectStore store, string value)
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
        return await store.PutAsync(source);
    }

    private sealed class ThrowingReadStream(byte[] content, int failAfterBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfterBytes)
            {
                throw new IOException("Injected source read failure.");
            }

            var readable = Math.Min(Math.Min(count, failAfterBytes - _position), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, readable);
            _position += readable;
            return readable;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= failAfterBytes)
            {
                return ValueTask.FromException<int>(new IOException("Injected source read failure."));
            }

            var readable = Math.Min(Math.Min(buffer.Length, failAfterBytes - _position), content.Length - _position);
            content.AsMemory(_position, readable).CopyTo(buffer);
            _position += readable;
            return ValueTask.FromResult(readable);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
