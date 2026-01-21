using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DadBoard.Gate;

sealed class AudioSampler : IDisposable
{
    private WasapiCapture? _capture;
    private double _sumSquares;
    private int _samples;
    private readonly object _lock = new();

    public async Task<double> SampleRmsAsync(string? deviceId, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        using var device = ResolveDevice(deviceId);
        _capture = device == null ? new WasapiCapture() : new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();

        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < duration)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct).ConfigureAwait(false);
            progress?.Report(GetRms());
        }

        _capture.StopRecording();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _capture = null;

        return GetRms();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _capture == null)
        {
            return;
        }

        var rms = ComputeRms(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
        lock (_lock)
        {
            _sumSquares += rms * rms;
            _samples++;
        }
    }

    private double GetRms()
    {
        lock (_lock)
        {
            if (_samples == 0)
            {
                return 0;
            }
            return Math.Sqrt(_sumSquares / _samples);
        }
    }

    public void Dispose()
    {
        if (_capture != null)
        {
            _capture.Dispose();
        }
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
        else
        {
            for (int i = 0; i < bytesRecorded; i += bytesPerSample)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }
        }

        double mean = sumSquares / sampleCount;
        return Math.Sqrt(mean);
    }
}
