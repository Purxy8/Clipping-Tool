namespace ClipForge.Capture;

internal sealed record AudioInputSpecification(
    string PipePath,
    string FfmpegSampleFormat,
    int SampleRate,
    int Channels);
