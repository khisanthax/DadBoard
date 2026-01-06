using System;
using System.IO;

namespace DadBoard.App;

static class DataPaths
{
    public static string ResolveBaseDir()
    {
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DadBoard");
        if (CanWrite(programData))
        {
            return programData;
        }

        return AppContext.BaseDirectory;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var test = Path.Combine(path, ".write_test");
            File.WriteAllText(test, "1");
            File.Delete(test);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
