using System;
using NAudio.CoreAudioApi;

namespace GateAgent;

sealed class MicController : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private float _baseline;
    private bool _hasBaseline;
    private bool _isGated;
    private DateTime _lastSetAt;

    public event Action<float>? VolumeChanged;

    public void Initialize()
    {
        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
        _endpoint = _device.AudioEndpointVolume;
        _baseline = _endpoint.MasterVolumeLevelScalar;
        _hasBaseline = true;
        _endpoint.OnVolumeNotification += OnVolumeNotification;
    }

    public float CurrentVolume
    {
        get
        {
            if (_endpoint == null) return 0f;
            return _endpoint.MasterVolumeLevelScalar;
        }
    }

    public float BaselineVolume => _baseline;

    public void SetGated(bool gated, float gateLevel)
    {
        if (_endpoint == null || !_hasBaseline)
        {
            return;
        }

        if (gated == _isGated)
        {
            return;
        }

        if (gated)
        {
            SetVolume(gateLevel);
        }
        else
        {
            SetVolume(_baseline);
        }

        _isGated = gated;
    }

    public void RestoreBaseline()
    {
        if (_endpoint == null || !_hasBaseline)
        {
            return;
        }

        SetVolume(_baseline);
        _isGated = false;
    }

    private void SetVolume(float level)
    {
        if (_endpoint == null)
        {
            return;
        }

        _lastSetAt = DateTime.UtcNow;
        _endpoint.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
        VolumeChanged?.Invoke(_endpoint.MasterVolumeLevelScalar);
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        var now = DateTime.UtcNow;
        if (_isGated)
        {
            return;
        }

        if ((now - _lastSetAt).TotalMilliseconds < 250)
        {
            return;
        }

        _baseline = data.MasterVolume;
        _hasBaseline = true;
        VolumeChanged?.Invoke(_baseline);
    }

    public void Dispose()
    {
        if (_endpoint != null)
        {
            _endpoint.OnVolumeNotification -= OnVolumeNotification;
        }
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
