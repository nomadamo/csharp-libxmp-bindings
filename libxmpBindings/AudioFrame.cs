namespace libxmpBindings;

/// <summary>
/// Represents a single audio frame with its associated metadata.
/// </summary>
public readonly struct AudioFrame
{
    /// <summary>
    /// Gets the raw audio data for this frame.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>
    /// Gets the metadata associated with this frame.
    /// </summary>
    public FrameMetadata Metadata { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the last frame (end of playback).
    /// </summary>
    public bool IsEndOfStream { get; init; }
}
