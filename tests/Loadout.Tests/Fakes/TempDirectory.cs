namespace Loadout.Fakes;

/// <summary>
/// Deletes a test's temporary folder. A virus scanner can keep a file the test just
/// closed open for a few milliseconds, so the delete is retried for up to a second.
/// </summary>
internal static class TempDirectory
{
    public static void Delete(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }
}
