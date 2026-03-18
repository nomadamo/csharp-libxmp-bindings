namespace libxmpBindings;

/// <summary>
/// Information about the audio format of the currently loaded module.
/// </summary>
public sealed class AudioFormatInfo
{
    /// <summary>
    /// Gets the sample rate in Hz (e.g., 44100).
    /// </summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// Gets the number of audio channels (1 for mono, 2 for stereo).
    /// </summary>
    public int Channels { get; init; }

    /// <summary>
    /// Gets the bits per sample (8 or 16).
    /// </summary>
    public int BitsPerSample { get; init; }

    /// <summary>
    /// Gets the XMP audio format flags.
    /// </summary>
    public XmpFormat Format { get; init; }

    /// <summary>
    /// Gets the estimated total duration of the module.
    /// This is an estimate and may not be accurate for all module types.
    /// </summary>
    public TimeSpan? EstimatedDuration { get; init; }

    /// <summary>
    /// Gets the bytes per sample (calculated from BitsPerSample).
    /// </summary>
    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>
    /// Gets the block alignment (bytes per sample frame across all channels).
    /// </summary>
    public int BlockAlign => Channels * BytesPerSample;

    /// <summary>
    /// Gets the average bytes per second.
    /// </summary>
    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    /// <summary>
    /// Gets a value indicating whether the format is 8-bit.
    /// </summary>
    public bool Is8Bit => Format.HasFlag(XmpFormat.Eightbit);

    /// <summary>
    /// Gets a value indicating whether the format is unsigned.
    /// </summary>
    public bool IsUnsigned => Format.HasFlag(XmpFormat.Unsigned);

    /// <summary>
    /// Gets a value indicating whether the format is mono.
    /// </summary>
    public bool IsMono => Format.HasFlag(XmpFormat.Mono);
}
