using System;

namespace GateAgent;

sealed class GateSettings
{
    public double Sensitivity { get; set; } = 0.02;
    public int AttackMs { get; set; } = 50;
    public int ReleaseMs { get; set; } = 300;
    public int LeaseMs { get; set; } = 600;
    public double GateLevel { get; set; } = 0.05;
    public Role DesiredRole { get; set; } = Role.Normal;
    public int RoleEpoch { get; set; } = 0;
}

sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public GateSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(_path))
            {
                return new GateSettings();
            }

            var json = System.IO.File.ReadAllText(_path);
            var settings = System.Text.Json.JsonSerializer.Deserialize<GateSettings>(json);
            return settings ?? new GateSettings();
        }
        catch
        {
            return new GateSettings();
        }
    }

    public void Save(GateSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            System.IO.File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }
}
