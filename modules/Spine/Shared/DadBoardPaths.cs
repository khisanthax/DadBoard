using System;
using System.IO;

namespace DadBoard.Spine.Shared;

public static class DadBoardPaths
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DadBoard");

    public static string InstalledExePath => Path.Combine(InstallDir, "DadBoard.exe");

    public static string RuntimeDir => InstallDir;

    public static string RuntimeExePath => InstalledExePath;

    public static string UpdatesDir => Path.Combine(RuntimeDir, "updates");

    public static string UpdateNewExePath => Path.Combine(UpdatesDir, "DadBoard.new.exe");

    public static string UpdateOldExePath => Path.Combine(RuntimeDir, "DadBoard.old.exe");

    public static string UpdateSourceDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "updates");

    public static string SetupLogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DadBoard", "logs");

    public static string SetupExePath => Path.Combine(InstallDir, "DadBoardSetup.exe");
}
