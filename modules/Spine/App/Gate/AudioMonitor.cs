using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DadBoard.Gate;

sealed class AudioMonitor : IDisposable
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private double _threshold;
    private int _attackMs;
    private int _releaseMs;
    private bool _talking;
    private DateTime _attackStart;
    private DateTime _lastAbove;
    private MMDevice? _device;

    public event Action<AudioState>? AudioStateUpdated;

    public AudioMonitor(GateSettings settings)
    {
        _threshold = settings.Sensitivity;
        _attackMs = settings.AttackMs;
        _releaseMs = settings.ReleaseMs;
    }

    public void UpdateSettings(GateSettings settings)
    {
        lock (_lock)
        {
            _threshold = settings.Sensitivity;
            _attackMs = settings.AttackMs;
            _releaseMs = settings.ReleaseMs;
        }
    }

    public void Start(string? deviceId)
    {
        _device?.Dispose();
        _device = ResolveDevice(deviceId);
        _capture = _device == null ? new WasapiCapture() : new WasapiCapture(_device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
    }

    public void Restart(string? deviceId)
    {
        Stop();
        Start(deviceId);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var rms = ComputeRms(e.Buffer, e.BytesRecorded, _capture!.WaveFormat);
        bool talkingChanged = false;
        bool talking;
        double threshold;
        int attackMs;
        int releaseMs;

        lock (_lock)
        {
            threshold = _threshold;
            attackMs = _attackMs;
            releaseMs = _releaseMs;
            talking = _talking;

            if (rms >= threshold)
            {
                _lastAbove = now;
                if (!talking)
                {
                    if (_attackStart == default)
                    {
                        _attackStart = now;
                    }
                    if ((now - _attackStart).TotalMilliseconds >= attackMs)
                    {
                        talking = true;
                        _talking = true;
                        _attackStart = default;
                        talkingChanged = true;
                    }
                }
            }
            else
            {
                _attackStart = default;
                if (talking && (now - _lastAbove).TotalMilliseconds >= releaseMs)
                {
                    talking = false;
                    _talking = false;
                    talkingChanged = true;
                }
            }
        }

        AudioStateUpdated?.Invoke(new AudioState
        {
            Level = rms,
            Talking = talking,
            TalkingChanged = talkingChanged,
            Timestamp = now
        });
    }

    private static double ComputeRms(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
        {
            return 0;
        }

        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;
        if (sampleCount == 0)
        {
            return 0;
        }

        double sumSquares = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (int i = 0; i < bytesRecorded; i += 4)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                sumSquares += sample * sample;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < bytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }
        }
        else
        {
            for (int i = 0; i < bytesRecorded; i += bytesPerSample)
            {
                int sample = 0;
                if (bytesPerSample >= 2)
                {
                    sample = BitConverter.ToInt16(buffer, i);
                }
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }
        }

        double mean = sumSquares / sampleCount;
        return Math.Sqrt(mean);
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
    }

    private static MMDevice? ResolveDevice(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return enumerator.GetDevice(deviceId);
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            return null;
        }
    }
}

sealed class AudioState
{
    public double Level { get; set; }
    public bool Talking { get; set; }
    public bool TalkingChanged { get; set; }
    public DateTime Timestamp { get; set; }
}
