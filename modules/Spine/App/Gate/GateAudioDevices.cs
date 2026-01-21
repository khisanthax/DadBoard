using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using CoreAudioRole = NAudio.CoreAudioApi.Role;

namespace DadBoard.Gate;

sealed record GateAudioDevice(string Id, string Name);

static class GateAudioDevices
{
    public static List<GateAudioDevice> GetCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new GateAudioDevice(d.ID, d.FriendlyName))
            .ToList();
    }

    public static string? GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, CoreAudioRole.Communications).ID;
        }
        catch
        {
            return null;
        }
    }
}
