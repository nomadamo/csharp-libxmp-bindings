namespace libxmpBindings;

/// <summary>
/// Result of a buffer fill operation.
/// </summary>
public readonly struct BufferFillResult
{
    /// <summary>
    /// Gets the number of bytes written to the buffer.
    /// </summary>
    public int BytesWritten { get; init; }

    /// <summary>
    /// Gets a value indicating whether playback is complete (end of module or loop point reached).
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Gets the metadata associated with this buffer fill operation.
    /// </summary>
    public FrameMetadata Metadata { get; init; }
}
