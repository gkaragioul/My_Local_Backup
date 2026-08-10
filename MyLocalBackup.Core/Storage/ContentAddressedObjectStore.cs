using System.Buffers;
using System.Security.Cryptography;

namespace MyLocalBackup.Core.Storage;

public sealed class ContentAddressedObjectStore : IObjectStore
{
    private const string Algorithm = "sha256";
    private const int BufferSize = 1024 * 1024;
    private readonly string _objectsRoot;
    private readonly string _stagingRoot;

    public ContentAddressedObjectStore(string repositoryRoot)
    {
        var normalizedRoot = PathRules.NormalizeAbsolutePath(repositoryRoot);
        _objectsRoot = Path.Combine(normalizedRoot, "objects", Algorithm);
        _stagingRoot = Path.Combine(normalizedRoot, "staging", "objects");

        Directory.CreateDirectory(_objectsRoot);
        Directory.CreateDirectory(_stagingRoot);
    }

    public async Task<StoredObject> PutAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        var stagingPath = Path.Combine(_stagingRoot, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var storedObject = await WriteStagingObjectAsync(source, stagingPath, cancellationToken).ConfigureAwait(false);
            var stagedVerification = await VerifyFileAsync(stagingPath, storedObject, cancellationToken).ConfigureAwait(false);
            if (!stagedVerification.IsValid)
            {
                throw new RepositoryCorruptionException(
                    $"Staged object '{storedObject.Hash}' failed verification: {stagedVerification.Error}");
            }

            var finalPath = GetObjectPath(storedObject);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(finalPath))
            {
                await RequireValidExistingObjectAsync(finalPath, storedObject, cancellationToken).ConfigureAwait(false);
                return storedObject;
            }

            if (!AtomicFileCommit.TryMoveNew(stagingPath, finalPath))
            {
                await RequireValidExistingObjectAsync(finalPath, storedObject, cancellationToken).ConfigureAwait(false);
                return storedObject;
            }

            File.SetAttributes(finalPath, File.GetAttributes(finalPath) | FileAttributes.ReadOnly);
            return storedObject;
        }
        finally
        {
            TryDeleteStagingFile(stagingPath);
        }
    }

    public Task<ObjectVerificationResult> VerifyAsync(
        StoredObject storedObject,
        CancellationToken cancellationToken = default)
    {
        return VerifyFileAsync(GetObjectPath(storedObject), storedObject, cancellationToken);
    }

    public string GetObjectPath(StoredObject storedObject)
    {
        ArgumentNullException.ThrowIfNull(storedObject);
        ValidateDescriptor(storedObject);

        var path = Path.Combine(_objectsRoot, storedObject.Hash[..2], storedObject.Hash);
        if (!PathRules.IsSameOrDescendant(path, _objectsRoot))
        {
            throw new ArgumentException("The object descriptor resolves outside the object store.", nameof(storedObject));
        }

        return path;
    }

    private static async Task<StoredObject> WriteStagingObjectAsync(
        Stream source,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                length = checked(length + read);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);

            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return new StoredObject(Algorithm, hash, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<ObjectVerificationResult> VerifyFileAsync(
        string path,
        StoredObject storedObject,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ObjectVerificationResult.Invalid("The object file is missing.");
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length != storedObject.Length)
        {
            return ObjectVerificationResult.Invalid(
                $"The object length is {fileInfo.Length}, expected {storedObject.Length}.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

        return string.Equals(actualHash, storedObject.Hash, StringComparison.Ordinal)
            ? ObjectVerificationResult.Valid
            : ObjectVerificationResult.Invalid(
                $"The object hash is '{actualHash}', expected '{storedObject.Hash}'.");
    }

    private static void ValidateDescriptor(StoredObject storedObject)
    {
        if (!string.Equals(storedObject.Algorithm, Algorithm, StringComparison.Ordinal) || storedObject.Length < 0)
        {
            throw new ArgumentException("The object descriptor is invalid.", nameof(storedObject));
        }

        if (storedObject.Hash.Length != 64 ||
            storedObject.Hash.Any(character => !char.IsAsciiHexDigit(character)) ||
            !string.Equals(storedObject.Hash, storedObject.Hash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The object hash must be a lowercase SHA-256 value.", nameof(storedObject));
        }
    }

    private static async Task RequireValidExistingObjectAsync(
        string finalPath,
        StoredObject storedObject,
        CancellationToken cancellationToken)
    {
        var verification = await VerifyFileAsync(finalPath, storedObject, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw new RepositoryCorruptionException(
                $"Existing object '{storedObject.Hash}' is corrupt and was not overwritten: {verification.Error}");
        }
    }

    private static void TryDeleteStagingFile(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.SetAttributes(stagingPath, FileAttributes.Normal);
                File.Delete(stagingPath);
            }
        }
        catch
        {
            // Startup staging recovery handles any file that could not be removed here.
        }
    }
}
