namespace MyLocalBackup.Core.Storage;

internal static class AtomicFileCommit
{
    public static bool TryMoveNew(string stagingPath, string finalPath)
    {
        try
        {
            File.Move(stagingPath, finalPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            return false;
        }
    }
}
