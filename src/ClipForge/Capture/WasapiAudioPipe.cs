using System.Buffers;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ClipForge.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipForge.Capture;

/// <summary>
/// Captures one WASAPI endpoint and streams its native PCM samples to FFmpeg
/// through a private named pipe. The bounded channel protects the audio callback
/// from a slow encoder without allowing unbounded memory growth.
/// </summary>
internal sealed class WasapiAudioPipe : IAsyncDisposable
{
    private const int MaximumQueuedSampleBlocks = 16;

    internal const PipeOptions ServerPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<AudioSampleBlock> _samples;
    private readonly CancellationTokenSource _disposeCancellation = new();

    private Task? _writerTask;
    private long _droppedSampleBlocks;
    private int _started;
    private int _disposed;

    public WasapiAudioPipe(AudioDeviceOption device, bool captureLoopback)
    {
        ArgumentNullException.ThrowIfNull(device);

        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(device.Id);
        _capture = captureLoopback
            ? new WasapiLoopbackCapture(_device)
            : new WasapiCapture(_device);

        var pipeName = $"clipforge-{Environment.ProcessId}-{Guid.NewGuid():N}";
        PipePath = $@"\\.\pipe\{pipeName}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            ServerPipeOptions,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024);

        _samples = Channel.CreateBounded<AudioSampleBlock>(
            new BoundedChannelOptions(MaximumQueuedSampleBlocks)
            {
                SingleReader = true,
                SingleWriter = false,
                // If scheduling pressure stalls the pipe writer, preserving
                // the newest samples bounds audio latency. The dropped-item
                // callback immediately returns the pooled buffer.
                FullMode = BoundedChannelFullMode.DropOldest
            },
            OnSampleBlockDropped);

        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;

        var waveFormat = _capture.WaveFormat;
        Specification = new AudioInputSpecification(
            PipePath,
            GetFfmpegSampleFormat(waveFormat),
            waveFormat.SampleRate,
            waveFormat.Channels);
    }

    public string PipePath { get; }

    public AudioInputSpecification Specification { get; }

    internal long DroppedSampleBlocks => Interlocked.Read(ref _droppedSampleBlocks);

    public Task Completion => _writerTask ?? Task.CompletedTask;

    public async Task ConnectAndStartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("This audio pipe has already been started.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);

        try
        {
            await _pipe.WaitForConnectionAsync(linkedCancellation.Token).ConfigureAwait(false);
            _writerTask = WriteSamplesAsync(_disposeCancellation.Token);
            _capture.StartRecording();
        }
        catch
        {
            _samples.Writer.TryComplete();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();

        try
        {
            _capture.StopRecording();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // The endpoint may already be stopped because FFmpeg closed its pipe.
        }

        _capture.DataAvailable -= Capture_DataAvailable;
        _capture.RecordingStopped -= Capture_RecordingStopped;
        _samples.Writer.TryComplete();

        if (_writerTask is not null)
        {
            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (IOException)
            {
                // FFmpeg normally closes the pipe before the producer is disposed.
            }
            catch (Exception) when (Volatile.Read(ref _disposed) != 0)
            {
                // Endpoint and channel faults are already being handled by shutdown.
            }
        }

        while (_samples.Reader.TryRead(out var remaining))
        {
            ArrayPool<byte>.Shared.Return(remaining.Buffer, clearArray: true);
        }

        _pipe.Dispose();
        _capture.Dispose();
        _device.Dispose();
        _disposeCancellation.Dispose();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(eventArgs.BytesRecorded);
        eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded).CopyTo(buffer);
        var sample = new AudioSampleBlock(buffer, eventArgs.BytesRecorded);
        if (!_samples.Writer.TryWrite(sample))
        {
            OnSampleBlockDropped(sample);
        }
    }

    private void OnSampleBlockDropped(AudioSampleBlock sample)
    {
        ArrayPool<byte>.Shared.Return(sample.Buffer, clearArray: true);
        Interlocked.Increment(ref _droppedSampleBlocks);
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs eventArgs) =>
        _samples.Writer.TryComplete(eventArgs.Exception);

    private async Task WriteSamplesAsync(CancellationToken cancellationToken)
    {
        var format = _capture.WaveFormat;
        var bytesPerTick = Math.Max(
            format.BlockAlign,
            format.AverageBytesPerSecond / 50 / format.BlockAlign * format.BlockAlign);
        var output = new byte[bytesPerTick];
        AudioSampleBlock? currentSample = null;
        var currentOffset = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Specification.FfmpegSampleFormat == "u8")
                {
                    Array.Fill(output, (byte)0x80);
                }
                else
                {
                    Array.Clear(output);
                }

                var outputOffset = 0;
                while (outputOffset < output.Length)
                {
                    if (currentSample is null)
                    {
                        if (!_samples.Reader.TryRead(out var sample))
                        {
                            break;
                        }

                        currentSample = sample;
                        currentOffset = 0;
                    }

                    var sampleBlock = currentSample.Value;
                    var bytesToCopy = Math.Min(
                        output.Length - outputOffset,
                        sampleBlock.Count - currentOffset);
                    sampleBlock.Buffer.AsSpan(currentOffset, bytesToCopy)
                        .CopyTo(output.AsSpan(outputOffset));
                    outputOffset += bytesToCopy;
                    currentOffset += bytesToCopy;

                    if (currentOffset >= sampleBlock.Count)
                    {
                        ArrayPool<byte>.Shared.Return(sampleBlock.Buffer, clearArray: true);
                        currentSample = null;
                        currentOffset = 0;
                    }
                }

                if (outputOffset == 0 &&
                    currentSample is null &&
                    _samples.Reader.Completion.IsCompleted)
                {
                    await _samples.Reader.Completion.ConfigureAwait(false);
                    return;
                }

                await _pipe.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (currentSample is { } sample)
            {
                ArrayPool<byte>.Shared.Return(sample.Buffer, clearArray: true);
            }

            while (_samples.Reader.TryRead(out var remaining))
            {
                ArrayPool<byte>.Shared.Return(remaining.Buffer, clearArray: true);
            }

            _pipe.Dispose();
        }
    }

    private readonly record struct AudioSampleBlock(byte[] Buffer, int Count);

    private static string GetFfmpegSampleFormat(WaveFormat format)
    {
        var encoding = format.Encoding;
        var isFloat = encoding == WaveFormatEncoding.IeeeFloat ||
                      format is WaveFormatExtensible extensibleFloat &&
                      extensibleFloat.SubFormat == IeeeFloatSubtype;
        if (isFloat)
        {
            return format.BitsPerSample switch
            {
                32 => "f32le",
                64 => "f64le",
                _ => throw new NotSupportedException(
                    $"The audio device uses an unsupported {format.BitsPerSample}-bit floating-point format.")
            };
        }

        var isPcm = encoding == WaveFormatEncoding.Pcm ||
                    format is WaveFormatExtensible extensiblePcm &&
                    extensiblePcm.SubFormat == PcmSubtype;
        if (!isPcm)
        {
            throw new NotSupportedException($"The audio device format {encoding} is not supported yet.");
        }

        return format.BitsPerSample switch
        {
            8 => "u8",
            16 => "s16le",
            24 => "s24le",
            32 => "s32le",
            _ => throw new NotSupportedException(
                $"The audio device uses an unsupported {format.BitsPerSample}-bit PCM format.")
        };
    }
}
