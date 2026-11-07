namespace IConnect.Core;

/// <summary>Commits a flushed private state file without discarding the earlier checkpoint.</summary>
public static class AtomicStateFile
{
    // Callers serialize writes and flush/close the staged file before calling this method.
    // A reader without FILE_SHARE_DELETE can briefly prevent the same-volume rename on Windows.
    public static void Commit(string staged, string destination)
    {
        ReadOnlySpan<int> delays = [10, 25, 50, 100, 200];
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(staged, destination, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var code = ex.HResult & 0xffff;
                if (OperatingSystem.IsWindows() && code is 5 or 32 or 33 && attempt < delays.Length)
                { Thread.Sleep(delays[attempt]); continue; }
                throw new IOException("Local progress could not be committed. The earlier saved state was kept; retry after checking storage access.", ex);
            }
        }
    }
}
