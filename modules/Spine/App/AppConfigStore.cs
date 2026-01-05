using System;
using System.IO;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

sealed class AppConfigStore
{
    private readonly string _path;

    public AppConfigStore(string path)
    {
        _path = path;
    }

    public AgentConfig Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaultConfig = new AgentConfig
                {
                    PcId = Guid.NewGuid().ToString("N"),
                    DisplayName = Environment.MachineName
                };
                Save(defaultConfig);
                return defaultConfig;
            }

            var json = File.ReadAllText(_path);
            var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonUtil.Options) ?? new AgentConfig();
            if (string.IsNullOrWhiteSpace(config.PcId))
            {
                config.PcId = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(config.DisplayName))
            {
                config.DisplayName = Environment.MachineName;
            }

            Save(config);
            return config;
        }
        catch
        {
            return new AgentConfig
            {
                PcId = Guid.NewGuid().ToString("N"),
                DisplayName = Environment.MachineName
            };
        }
    }

    public void Save(AgentConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(config, JsonUtil.Options);
            File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }
}
