namespace libxmpBindings;

/// <summary>
/// Metadata information about an audio frame from the XMP module.
/// </summary>
public readonly struct FrameMetadata
{
    /// <summary>
    /// Gets the current loop count. 0 indicates no loop has occurred yet.
    /// </summary>
    public int LoopCount { get; init; }

    /// <summary>
    /// Gets the current playback position in the module.
    /// </summary>
    public TimeSpan Position { get; init; }

    /// <summary>
    /// Gets the current pattern number being played.
    /// </summary>
    public int Pattern { get; init; }

    /// <summary>
    /// Gets the current row within the pattern.
    /// </summary>
    public int Row { get; init; }

    /// <summary>
    /// Gets the current frame number.
    /// </summary>
    public int Frame { get; init; }

    /// <summary>
    /// Gets the current tempo (speed).
    /// </summary>
    public int Speed { get; init; }

    /// <summary>
    /// Gets the current beats per minute.
    /// </summary>
    public int Bpm { get; init; }

    /// <summary>
    /// Gets the total playback time elapsed in milliseconds.
    /// </summary>
    public int TotalTimeMs { get; init; }

    /// <summary>
    /// Gets the current sequence number.
    /// </summary>
    public int Sequence { get; init; }

    /// <summary>
    /// Gets the number of virtual channels in use.
    /// </summary>
    public int VirtualChannelsUsed { get; init; }
}
