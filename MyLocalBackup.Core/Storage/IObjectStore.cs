namespace MyLocalBackup.Core.Storage;

public sealed record StoredObject(string Algorithm, string Hash, long Length);

public sealed record ObjectVerificationResult(bool IsValid, string? Error)
{
    public static ObjectVerificationResult Valid { get; } = new(true, null);

    public static ObjectVerificationResult Invalid(string error) => new(false, error);
}

public sealed class RepositoryCorruptionException : IOException
{
    public RepositoryCorruptionException(string message)
        : base(message)
    {
    }
}

public interface IObjectStore
{
    Task<StoredObject> PutAsync(Stream source, CancellationToken cancellationToken = default);

    Task<ObjectVerificationResult> VerifyAsync(StoredObject storedObject, CancellationToken cancellationToken = default);

    string GetObjectPath(StoredObject storedObject);
}
