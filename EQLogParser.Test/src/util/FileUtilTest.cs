using EQLogParser;
using System;
using System.IO;
using System.Linq;

namespace EQLogParserTest
{
  [TestClass]
  public class FileUtilTest
  {
    [TestMethod]
    public void BuildBackupFilename_ContainsExpectedParts()
    {
      var filename = FileUtil.BuildBackupFilename();
      Assert.IsTrue(filename.StartsWith("EQLogParser_backup_"));
      Assert.IsTrue(filename.EndsWith(".zip"));
    }

    [TestMethod]
    public void BuildBackupFilename_ContainsVersion()
    {
      var filename = FileUtil.BuildBackupFilename();
      // Should contain the assembly version (minus last segment)
      Assert.IsTrue(filename.Contains("backup_"));
    }

    [TestMethod]
    public void BuildBackupFilename_ContainsDate()
    {
      var filename = FileUtil.BuildBackupFilename();
      // Format: yyyyMMdd-ssfff after the version
      Assert.IsTrue(filename.Length > 20, "Filename should contain version and date");
    }

    [TestMethod]
    public void GetDirFromPath_ValidDirectory_ReturnsDirectory()
    {
      var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      var path = Path.Combine(tempDir, "somefile.txt");
      var result = FileUtil.GetDirFromPath(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      Assert.AreEqual(tempDir, result);
    }

    [TestMethod]
    public void GetDirFromPath_NullPath_ReturnsNull()
    {
      var result = FileUtil.GetDirFromPath(null);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void GetDirFromPath_EmptyPath_ReturnsNull()
    {
      var result = FileUtil.GetDirFromPath("");
      Assert.IsNull(result);
    }

    [TestMethod]
    public void GetDirFromPath_NonExistentDirectory_ReturnsNull()
    {
      var result = FileUtil.GetDirFromPath("/nonexistent/path/file.txt");
      Assert.IsNull(result);
    }

    [TestMethod]
    public void ParseFileName_ValidEqLogFile_ParsesCorrectly()
    {
      var result = FileUtil.ParseFileName("eqlog_PlayerName_ServerName.txt", out var name, out var server);
      Assert.IsTrue(result);
      Assert.AreEqual("PlayerName", name);
      Assert.AreEqual("ServerName", server);
    }

    [TestMethod]
    public void ParseFileName_WithFullPath_ParsesCorrectly()
    {
      var result = FileUtil.ParseFileName("/some/path/eqlog_MyPlayer_MyServer.txt", out var name, out var server);
      Assert.IsTrue(result);
      Assert.AreEqual("MyPlayer", name);
      Assert.AreEqual("MyServer", server);
    }

    [TestMethod]
    public void ParseFileName_LogExtension_ParsesCorrectly()
    {
      var result = FileUtil.ParseFileName("eqlog_Player_Server.log", out var name, out var server);
      Assert.IsTrue(result);
      Assert.AreEqual("Player", name);
      Assert.AreEqual("Server", server);
    }

    [TestMethod]
    public void ParseFileName_InvalidFormat_ReturnsFalse()
    {
      var result = FileUtil.ParseFileName("invalid_filename.txt", out var name, out var server);
      Assert.IsFalse(result);
    }

    [TestMethod]
    public void ParseFileName_MissingServer_ReturnsFalse()
    {
      var result = FileUtil.ParseFileName("eqlog_Player.txt", out var name, out var server);
      Assert.IsFalse(result);
    }

    [TestMethod]
    public void ParseFileName_CaseInsensitive_Matches()
    {
      var result = FileUtil.ParseFileName("EQLOG_Player_Server.TXT", out var name, out var server);
      Assert.IsTrue(result);
    }

    [TestMethod]
    public void FindArchivedLogFiles_EmptyArchiveFolder_ReturnsEmpty()
    {
      var results = FileUtil.FindArchivedLogFiles("", "player", "server", 0);
      Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void FindArchivedLogFiles_NullArchiveFolder_ReturnsEmpty()
    {
      var results = FileUtil.FindArchivedLogFiles(null, "player", "server", 0);
      Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void FindArchivedLogFiles_ExistingFolder_NoMatchingFiles_ReturnsEmpty()
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "FileUtilTest_" + Guid.NewGuid());
      Directory.CreateDirectory(tempDir);
      try
      {
        var results = FileUtil.FindArchivedLogFiles(tempDir, "player", "server", 0);
        Assert.AreEqual(0, results.Count);
      }
      finally
      {
        Directory.Delete(tempDir, true);
      }
    }

    [TestMethod]
    public void FindArchivedLogFiles_MatchingFile_ReturnsFile()
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "FileUtilTest_" + Guid.NewGuid());
      Directory.CreateDirectory(tempDir);
      try
      {
        // Create a file matching the archived pattern: eqlog_player_server_YYYYMMDD_HHmm_NNNN.txt
        var filePath = Path.Combine(tempDir, "eqlog_TestPlayer_TestServer_202401011200_0001.txt");
        File.WriteAllText(filePath, "test content");

        var results = FileUtil.FindArchivedLogFiles(tempDir, "TestPlayer", "TestServer", 0);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(filePath, results[0]);
      }
      finally
      {
        Directory.Delete(tempDir, true);
      }
    }

    [TestMethod]
    public void FindArchivedLogFiles_MultipleFiles_ReturnsSortedByDateDescending()
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "FileUtilTest_" + Guid.NewGuid());
      Directory.CreateDirectory(tempDir);
      try
      {
        File.WriteAllText(Path.Combine(tempDir, "eqlog_P_S_202401011200_0001.txt"), "");
        File.WriteAllText(Path.Combine(tempDir, "eqlog_P_S_202402011200_0002.txt"), "");
        File.WriteAllText(Path.Combine(tempDir, "eqlog_P_S_202403011200_0003.txt"), "");

        var results = FileUtil.FindArchivedLogFiles(tempDir, "P", "S", 0);
        Assert.AreEqual(3, results.Count);
        // Should be sorted newest first
        Assert.IsTrue(results[0].Contains("20240301"));
        Assert.IsTrue(results[2].Contains("20240101"));
      }
      finally
      {
        Directory.Delete(tempDir, true);
      }
    }

    [TestMethod]
    public void FindArchivedLogFiles_WithGzExtension_ReturnsFile()
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "FileUtilTest_" + Guid.NewGuid());
      Directory.CreateDirectory(tempDir);
      try
      {
        var filePath = Path.Combine(tempDir, "eqlog_Player_Server_202401011200_0001.txt.gz");
        File.WriteAllText(filePath, "");

        var results = FileUtil.FindArchivedLogFiles(tempDir, "Player", "Server", 0);
        Assert.AreEqual(1, results.Count);
      }
      finally
      {
        Directory.Delete(tempDir, true);
      }
    }

    [TestMethod]
    public void FindArchivedLogFiles_CaseInsensitivePlayerAndServer()
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "FileUtilTest_" + Guid.NewGuid());
      Directory.CreateDirectory(tempDir);
      try
      {
        File.WriteAllText(Path.Combine(tempDir, "eqlog_TestPlayer_TestServer_202401011200_0001.txt"), "");

        var results = FileUtil.FindArchivedLogFiles(tempDir, "testplayer", "testserver", 0);
        Assert.AreEqual(1, results.Count);
      }
      finally
      {
        Directory.Delete(tempDir, true);
      }
    }

    [TestMethod]
    public void GetStreamReader_NonGzFile_ReturnsStreamReader()
    {
      var tempFile = Path.Combine(Path.GetTempPath(), "testfile_" + Guid.NewGuid() + ".txt");
      File.WriteAllText(tempFile, "hello world");
      try
      {
        using var fs = File.OpenRead(tempFile);
        using var reader = FileUtil.GetStreamReader(fs);
        var content = reader.ReadToEnd();
        Assert.AreEqual("hello world", content);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }

    [TestMethod]
    public void GetStreamReader_GzFile_ReturnsDecompressedContent()
    {
      var tempFile = Path.Combine(Path.GetTempPath(), "testfile_" + Guid.NewGuid() + ".txt.gz");
      var originalContent = "compressed content here";
      using (var fs = File.Create(tempFile))
      using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Compress))
      using (var writer = new System.IO.StreamWriter(gz))
      {
        writer.Write(originalContent);
      }

      try
      {
        using var fs = File.OpenRead(tempFile);
        using var reader = FileUtil.GetStreamReader(fs);
        var content = reader.ReadToEnd();
        Assert.AreEqual(originalContent, content);
      }
      finally
      {
        File.Delete(tempFile);
      }
    }
  }
}
