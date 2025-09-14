using EQLogParser;
using LiteDB;
using System.IO.Compression;
using System.Reflection;

if (args.Length == 0)
{
  ShowHelp();
  return;
}

for (var i = 0; i < args.Length; i++)
{
  var arg = args[i];

  if (arg is "--help" or "-h")
  {
    ShowHelp();
  }
  else if (arg == "--path" && i + 1 < args.Length)
  {
    var path = args[++i];
    HandlePath(path);
  }
  else
  {
    Console.WriteLine($@"Unknown argument: {arg}");
    ShowHelp();
  }
}

return;

void ShowHelp()
{
  Console.WriteLine(@"Usage:");
  Console.WriteLine(@"  BackupUtil [options]");
  Console.WriteLine();
  Console.WriteLine(@"Options:");
  Console.WriteLine(@"  --help, -h       Show help information");
  Console.WriteLine(@"  --path <path>    Specify location for the backup");
}

void HandlePath(string path)
{
  // Trim quotes from the input path
  var trimmed = path.Trim('"');

  if (Directory.Exists(trimmed))
  {
    // create checkpoint if triggers db file exists
    try
    {
      var databaseFile = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser\config\triggers.db");
      if (File.Exists(databaseFile))
      {
        var connString = new ConnectionString
        {
          Filename = databaseFile,
          Connection = ConnectionType.Shared
        };

        var db = new LiteDatabase(connString)
        {
          CheckpointSize = 10
        };

        db.Checkpoint();
        db.Dispose();
      }
    }
    catch (Exception)
    {
      // ignore
    }

    try
    {
      // Load the assembly by name
      var assembly = Assembly.Load("EQLogParser");

      // get file name
      var filename = FileUtil.BuildBackupFilename();
      Console.WriteLine($@"Creating Backup: {filename}");
      var fullPath = Path.Combine(trimmed, filename);
      var source = Environment.ExpandEnvironmentVariables(@"%AppData%\EQLogParser");

      if (File.Exists(fullPath))
      {
        File.Delete(fullPath);
      }

      ZipFile.CreateFromDirectory(source, fullPath, CompressionLevel.Optimal, false);
      Console.WriteLine($@"Backup Created: {fullPath}");
    }
    catch (Exception e)
    {
      Console.WriteLine($@"Error Creating Backup: {e.Message}");
    }
  }
  else
  {
    Console.WriteLine($@"Location does not exist: {trimmed}");
  }
}