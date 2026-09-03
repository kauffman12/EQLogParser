namespace EQLogParser
{
  /* Scratch storage shared by the store/util fixtures. Each fixture used to carry its own temp-dir
   * list, its own [TestCleanup] delete loop, its own CreateDirectory(Guid) line and (twice) its own
   * copy of the repository-file lookup below — all identical. One place, one behavior. */
  internal static class TestTemp
  {
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "eqlp-core-tests");

    /// <summary>Finds a file under the repository root (works no matter where the test bin lives).</summary>
    public static string RepoFile(string relativePath)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln")))
      {
        dir = dir.Parent;
      }

      return dir is null
        ? Path.Combine(Directory.GetCurrentDirectory(), relativePath)
        : Path.Combine(dir.FullName, relativePath);
    }
  }

  /* Base class for fixtures that create scratch databases/directories: creates uniquely named folders
   * under TestTemp.Root and deletes them (best effort) after each test. MSTest runs the inherited
   * [TestCleanup] after any cleanup declared on the derived fixture. */
  public abstract class TempDirFixture
  {
    private readonly List<string> _dirs = [];

    protected string NewTempDir()
    {
      var dir = Directory.CreateDirectory(Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"))).FullName;
      _dirs.Add(dir);
      return dir;
    }

    // For fixtures that compose the database path themselves instead of using NewTempDir.
    protected void TrackDir(string? dir)
    {
      if (!string.IsNullOrEmpty(dir))
      {
        _dirs.Add(dir);
      }
    }

    [TestCleanup]
    public void CleanupTempDirs()
    {
      foreach (var dir in _dirs)
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch
        {
          // best effort — temp dirs are harmless if a file handle is still open
        }
      }
    }
  }
}
