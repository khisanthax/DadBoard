using System;
using System.IO;

namespace DadBoard.Spine.Shared;

public static class DadBoardPaths
{
    public static string ProgramFilesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DadBoard");

    public static string InstalledExePath => Path.Combine(ProgramFilesDir, "DadBoard.exe");

    public static string RuntimeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard");

    public static string RuntimeExePath => Path.Combine(RuntimeDir, "DadBoard.exe");

    public static string UpdatesDir => Path.Combine(RuntimeDir, "updates");

    public static string UpdateNewExePath => Path.Combine(UpdatesDir, "DadBoard.new.exe");

    public static string UpdateOldExePath => Path.Combine(RuntimeDir, "DadBoard.old.exe");
}
