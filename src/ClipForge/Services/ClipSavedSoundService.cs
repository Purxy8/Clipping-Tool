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
    /// Produces a deterministic 200 ms, 48 kHz mono PCM16 WAV. Keeping this pure
    /// makes the generated asset straightforward to validate without audio hardware.
    /// </summary>
    internal static byte[] CreateChimeWave()
    {
        const int sampleRate = 48_000;
        const double durationSeconds = 0.2;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const double peakAmplitude = 0.12;
        const double attackSeconds = 0.012;
        const double releaseSeconds = 0.055;

        var sampleCount = checked((int)Math.Round(sampleRate * durationSeconds));
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = checked(sampleCount * channelCount * bytesPerSample);

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

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var time = sampleIndex / (double)sampleRate;
            var remaining = durationSeconds - time;
            var fadeIn = Math.Min(1, time / attackSeconds);
            var fadeOut = Math.Clamp(remaining / releaseSeconds, 0, 1);
            var decay = Math.Exp(-2.8 * time / durationSeconds);
            var envelope = fadeIn * fadeOut * decay;

            // Two soft partials with a slight downward glide form a short,
            // unobtrusive confirmation sound without using an external asset.
            var glide = -110.0 / durationSeconds;
            var primaryPhase = 2 * Math.PI * (760 * time + 0.5 * glide * time * time);
            var harmonicPhase = 2 * Math.PI * (1_140 * time + 0.5 * glide * 1.25 * time * time);
            var signal = 0.76 * Math.Sin(primaryPhase) + 0.24 * Math.Sin(harmonicPhase + 0.18);
            var normalized = Math.Clamp(signal * envelope * peakAmplitude, -1, 1);
            writer.Write((short)Math.Round(normalized * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
