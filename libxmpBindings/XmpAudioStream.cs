namespace libxmpBindings;

using System.Runtime.InteropServices;

/// <summary>
/// A Stream wrapper around XMP audio data for easy integration with audio libraries.
/// Supports full time-based seeking using XMP's built-in seeking functionality.
/// Note: Seeking accuracy is limited by module structure (pattern/row boundaries).
/// </summary>
public sealed class XmpAudioStream(Xmp xmp, bool loop = false, int bufferSize = 8192) : Stream
{
    private readonly Xmp _xmp = xmp ?? throw new ArgumentNullException(nameof(xmp));
    private readonly bool _loop = loop;
    private int _bufferSize = bufferSize;
    private byte[] _internalBuffer = new byte[bufferSize];
    private int _bufferPosition = 0;
    private int _bufferLength = 0;
    private long _position = 0;
    private bool _isComplete = false;
    private bool _hasStartedPlayer = false;
    private bool _hasDetectedFrameSize = false;

    public override bool CanRead => !_isComplete;
    public override bool CanSeek => true; // Full time-based seeking support via XMP
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the estimated length of the stream in bytes.
    /// This is calculated from the module's estimated duration and audio format.
    /// Note: May not be exact due to tempo changes or dynamic module features.
    /// </summary>
    public override long Length
    {
        get
        {
            var formatInfo = _xmp.GetAudioFormat();
            if (formatInfo?.EstimatedDuration != null)
            {
                // Calculate: bytes = duration_seconds * bytes_per_second
                var durationSeconds = formatInfo.EstimatedDuration.Value.TotalSeconds;
                var bytesPerSecond = formatInfo.AverageBytesPerSecond;
                return (long)(durationSeconds * bytesPerSecond);
            }
            throw new NotSupportedException("Cannot determine stream length - format info unavailable.");
        }
    }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
            }

            Seek(value, SeekOrigin.Begin);
        }
    }

    private void EnsurePlayerStarted()
    {
        if (!_hasStartedPlayer && _xmp.GetPlayerState() == XmpPlayerStates.Loaded)
        {
            _xmp.StartPlayer();
            _hasStartedPlayer = _xmp.GetPlayerState() == XmpPlayerStates.Playing;
            if (_hasStartedPlayer) { System.Diagnostics.Debug.WriteLine("[XmpAudioStream] Player started successfully."); } else { System.Diagnostics.Debug.WriteLine("[XmpAudioStream] Warning: Player state after start attempt is not Playing."); }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");
        }

        if (offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "Value exceeds buffer length");
        }

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        EnsurePlayerStarted();

        if (_isComplete)
        {
            return 0;
        }

        var totalRead = 0;
        var readCalls = 0;

        while (totalRead < buffer.Length && !_isComplete)
        {
            // If we have data in our internal buffer, copy it
            if (_bufferPosition < _bufferLength)
            {
                var availableInBuffer = _bufferLength - _bufferPosition;
                var toCopy = Math.Min(availableInBuffer, buffer.Length - totalRead);

                _internalBuffer.AsSpan(_bufferPosition, toCopy).CopyTo(buffer[totalRead..]);

                _bufferPosition += toCopy;
                totalRead += toCopy;
                _position += toCopy;
            }
            else
            {
                // Need to fetch more data
                readCalls++;
                var bytesWritten = _xmp.ReadBufferWithSize(_internalBuffer, _loop);

                if (bytesWritten <= 0)
                {
                    _isComplete = true;
                    System.Diagnostics.Debug.WriteLine($"[XmpAudioStream] End of stream after {readCalls} ReadBuffer calls, totalRead={totalRead}");
                    break;
                }

                // ADAPTIVE BUFFER SIZING: After first read, optimize buffer to XMP's natural frame size
                // XMP renders in frames (typically 3528 bytes @ 44100Hz stereo for 50fps modules)
                // This eliminates the mismatch between requested buffer size and actual data produced
                if (!_hasDetectedFrameSize && bytesWritten < _bufferSize)
                {
                    _bufferSize = bytesWritten;
                    Array.Resize(ref _internalBuffer, _bufferSize);
                    _hasDetectedFrameSize = true;
                    System.Diagnostics.Debug.WriteLine($"[XmpAudioStream] Detected XMP frame size: {bytesWritten} bytes, resized buffer from 8192 to {_bufferSize}");
                }

                _bufferPosition = 0;
                _bufferLength = bytesWritten; // Use actual bytes written, not buffer capacity!
            }
        }

        return totalRead;
    }

    public async override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return
            offset < 0
            ? throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative")
            : count < 0
            ? throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative")
            : offset + count > buffer.Length
            ? throw new ArgumentOutOfRangeException(nameof(buffer), "Value exceeds buffer length")
            : await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public async override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // For now, just wrap the synchronous Read in a Task
        // XMP library operations are mostly synchronous
        return await Task.Run(() => Read(buffer.Span), cancellationToken);
    }

    public override void Flush()
    {
        // No-op for read-only stream
    }

    /// <summary>
    /// Seeks to a specific position in the module stream using time-based seeking.
    /// Converts byte positions to time and uses XMP's xmp_seek_time functionality.
    /// Note: Seeking is not sample-accurate - XMP seeks to pattern/row boundaries.
    /// </summary>
    /// <param name="offset">The offset in bytes</param>
    /// <param name="origin">The origin for seeking (Begin, Current, or End)</param>
    /// <returns>The new position in the stream (approximate)</returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsurePlayerStarted();

        // Calculate target position in bytes
        var targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset, // offset should be negative for SeekOrigin.End
            _ => throw new ArgumentException("Invalid SeekOrigin value.", nameof(origin))
        };

        if (targetPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Seek position cannot be negative.");
        }

        // Get format info for byte→time conversion
        var formatInfo = _xmp.GetAudioFormat() ?? throw new InvalidOperationException("Cannot seek - format info unavailable.");

        // Convert byte position to milliseconds
        // time_seconds = byte_position / bytes_per_second
        // time_milliseconds = time_seconds * 1000
        var timeSeconds = (double)targetPosition / formatInfo.AverageBytesPerSecond;
        var timeMilliseconds = (int)(timeSeconds * 1000);

        // Clamp to valid range
        if (formatInfo.EstimatedDuration != null)
        {
            var maxMilliseconds = (int)formatInfo.EstimatedDuration.Value.TotalMilliseconds;
            timeMilliseconds = Math.Clamp(timeMilliseconds, 0, maxMilliseconds);
        }
        else
        {
            // No duration info, just ensure non-negative
            timeMilliseconds = Math.Max(0, timeMilliseconds);
        }

        // Use XMP's time-based seeking
        if (!_xmp.SkipToPosition(timeMilliseconds))
        {
            throw new IOException($"XMP seeking failed for position {timeMilliseconds}ms");
        }

        // Reset internal buffer state after seek
        _bufferPosition = 0;
        _bufferLength = 0;
        _isComplete = false;

        // Calculate actual byte position after seek
        // Note: XMP may not seek to exact position due to module structure
        _position = (long)(timeMilliseconds / 1000.0 * formatInfo.AverageBytesPerSecond);

        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("XmpAudioStream does not support setting length.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("XmpAudioStream is read-only.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isComplete)
        {
            _isComplete = true;
        }
        
        base.Dispose(disposing);
    }
}
