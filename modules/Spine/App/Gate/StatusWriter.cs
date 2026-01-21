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
                schemaVersion = snapshot.SchemaVersion,
                gatePort = snapshot.GatePort,
                discoveryPort = snapshot.DiscoveryPort,
                pcId = snapshot.PcId,
                role = snapshot.EffectiveRole.ToString(),
                leaderId = snapshot.LeaderId,
                coCaptainId = snapshot.CoCaptainId,
                talking = snapshot.Talking,
                talkStart = snapshot.TalkStart.ToString("O"),
                allowed = snapshot.Allowed,
                gated = snapshot.Gated,
                micScalar = snapshot.MicScalar,
                baselineVolume = snapshot.BaselineVolume,
                selectedDeviceId = snapshot.SelectedDeviceId,
                selectedDeviceName = snapshot.SelectedDeviceName,
                lastRoleChange = snapshot.LastRoleChange.ToString("O"),
                lastFloorOwner = snapshot.FloorOwner,
                lastPeerSeenSeconds = snapshot.LastPeerSeenSeconds,
                peerCount = snapshot.PeerCount,
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
