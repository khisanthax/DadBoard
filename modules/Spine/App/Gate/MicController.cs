using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace DadBoard.Gate;

sealed class MicController : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private float _baseline;
    private bool _hasBaseline;
    private bool _isGated;
    private float _gainScalar = 1f;
    private DateTime _lastSetAt;
    private Dictionary<string, float> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private MicLevelsStore? _store;
    private string? _deviceId;

    public event Action<float>? VolumeChanged;
    public event Action<string?, float>? BaselineChanged;

    public void Initialize(string? deviceId, MicLevelsStore store)
    {
        _store = store;
        _baselines = store.Load();
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? GateAudioDevices.GetDefaultDeviceId() : deviceId;
        SwitchDevice(_deviceId);
    }

    public void SwitchDevice(string? deviceId)
    {
        if (_endpoint != null)
        {
            _endpoint.OnVolumeNotification -= OnVolumeNotification;
        }
        _endpoint = null;
        _device?.Dispose();

        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? GateAudioDevices.GetDefaultDeviceId() : deviceId;
        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            return;
        }

        _device = _enumerator.GetDevice(_deviceId);
        _endpoint = _device.AudioEndpointVolume;
        if (_baselines.TryGetValue(_deviceId, out var baseline))
        {
            _baseline = baseline;
            _hasBaseline = true;
            SetVolume(_baseline);
        }
        else
        {
            _baseline = _endpoint.MasterVolumeLevelScalar;
            _hasBaseline = true;
            SaveBaseline();
        }
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
    public float GainScalar => _gainScalar;

    public string? DeviceId => _deviceId;

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
            SetVolume(ComputeAppliedBaseline());
        }

        _isGated = gated;
    }

    public void RestoreBaseline()
    {
        if (_endpoint == null || !_hasBaseline)
        {
            return;
        }

        SetVolume(ComputeAppliedBaseline());
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
        SaveBaseline();
        VolumeChanged?.Invoke(_baseline);
    }

    public void SetBaseline(float value)
    {
        _baseline = Math.Clamp(value, 0f, 1f);
        _hasBaseline = true;
        SaveBaseline();
        SetVolume(ComputeAppliedBaseline());
    }

    public void SetGainScalar(float scalar)
    {
        _gainScalar = Math.Clamp(scalar, 0.3f, 2.0f);
        if (_endpoint == null || !_hasBaseline)
        {
            return;
        }

        if (!_isGated)
        {
            SetVolume(ComputeAppliedBaseline());
        }
    }

    private float ComputeAppliedBaseline()
    {
        var applied = _baseline * _gainScalar;
        return Math.Clamp(applied, 0.10f, 0.95f);
    }

    private void SaveBaseline()
    {
        if (string.IsNullOrWhiteSpace(_deviceId) || _store == null)
        {
            return;
        }

        _baselines[_deviceId] = _baseline;
        _store.Save(_baselines);
        BaselineChanged?.Invoke(_deviceId, _baseline);
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
