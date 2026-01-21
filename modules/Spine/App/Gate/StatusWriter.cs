using System;
using System.IO;
using System.Text.Json;

namespace DadBoard.Gate;

sealed class StatusWriter
{
    private readonly string _path;
    private readonly object _lock = new();

    public StatusWriter(string path)
    {
        _path = path;
    }

    public void Write(GateSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new
            {
                pcId = snapshot.PcId,
                role = snapshot.EffectiveRole.ToString(),
                talking = snapshot.Talking,
                allowed = snapshot.Allowed,
                gated = snapshot.Gated,
                micScalar = snapshot.MicScalar,
                lastRoleChange = snapshot.LastRoleChange.ToString("O"),
                lastFloorOwner = snapshot.FloorOwner,
                lastUpdate = snapshot.LastUpdate.ToString("O"),
                peers = snapshot.Peers
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }
        catch
        {
        }
    }
}
