using System.Media;
using System.Text;

namespace ClipForge.Services;

/// <summary>
/// Plays a small, original confirmation chime without sharing any objects with
/// the recording audio pipeline. Playback feedback is always best-effort.
/// </summary>
internal sealed class ClipSavedSoundService : IDisposable
{
    private readonly object _gate = new();
    private MemoryStream? _waveStream;
    private SoundPlayer? _player;
    private bool _disposed;

    public ClipSavedSoundService()
    {
        MemoryStream? waveStream = null;
        SoundPlayer? player = null;

        try
        {
            waveStream = new MemoryStream(CreateChimeWave(), writable: false);
            player = new SoundPlayer(waveStream);
            // Preload without blocking WPF's first UI frame. Some Windows audio
            // configurations can hold synchronous SoundPlayer.Load until a message
            // pump is fully available during application startup.
            player.LoadAsync();

            _waveStream = waveStream;
            _player = player;
        }
        catch
        {
            // Missing or unavailable playback must never affect capture or saving.
            player?.Dispose();
            waveStream?.Dispose();
        }
    }

    public void TryPlay(bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _player is null)
            {
                return;
            }

            try
            {
                // SoundPlayer.Play is asynchronous, so the save completion path
                // is not held up by the short acknowledgement sound.
                _player.Play();
            }
            catch
            {
                // A muted, removed, or unavailable output device is non-fatal.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _player?.Stop();
            }
            catch
            {
                // Shutdown remains best-effort when the audio device disappeared.
            }

            try
            {
                _player?.Dispose();
            }
            catch
            {
                // Disposal feedback must not interfere with application shutdown.
            }

            _player = null;
            _waveStream?.Dispose();
            _waveStream = null;
        }
    }

    /// <summary>
    /// Produces a deterministic 210 ms, 48 kHz mono PCM16 WAV. Keeping this pure
    /// makes the generated asset straightforward to validate without audio hardware.
    /// </summary>
    internal static byte[] CreateChimeWave()
    {
        const int sampleRate = 48_000;
        const double durationSeconds = 0.21;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const double targetPeakAmplitude = 0.27;
        const double attackSeconds = 0.006;
        const double releaseSeconds = 0.07;

        var sampleCount = checked((int)Math.Round(sampleRate * durationSeconds));
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = checked(sampleCount * channelCount * bytesPerSample);

        var samples = new double[sampleCount];
        var unscaledPeak = 0d;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var time = sampleIndex / (double)sampleRate;
            var remaining = durationSeconds - time;
            var fadeIn = Math.Min(1, time / attackSeconds);
            var fadeOut = Math.Clamp(remaining / releaseSeconds, 0, 1);
            var decay = Math.Exp(-3.5 * time / durationSeconds);
            var envelope = fadeIn * fadeOut * decay;

            // A single warm, lower-pitched UI pop. The octave reinforcement adds
            // body on small speakers without the high two-note glide that made the
            // previous confirmation resemble a bird call.
            var fundamental = Math.Sin(2 * Math.PI * 390 * time);
            var lowerOctave = Math.Sin((2 * Math.PI * 195 * time) + 0.32);
            var sample = ((0.84 * fundamental) + (0.16 * lowerOctave)) * envelope;
            samples[sampleIndex] = sample;
            unscaledPeak = Math.Max(unscaledPeak, Math.Abs(sample));
        }

        var amplitudeScale = unscaledPeak > 0
            ? targetPeakAmplitude / unscaledPeak
            : 0;

        using var stream = new MemoryStream(capacity: 44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channelCount * bytesPerSample);
        writer.Write((short)(channelCount * bytesPerSample));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            var normalized = Math.Clamp(samples[sampleIndex] * amplitudeScale, -1, 1);
            writer.Write((short)Math.Round(normalized * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
