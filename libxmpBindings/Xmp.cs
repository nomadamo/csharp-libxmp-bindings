namespace libxmpBindings;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

using NativeBindings;

//using NAudio.Wave;

public sealed class Xmp : IDisposable
{
    public readonly unsafe sbyte* Context;
    private readonly int _rate;
    private readonly XmpFormat _format;
    private readonly float _refillRatio;
    private bool _hasBeenDisposed = false;

    public unsafe Xmp(int rate = 44100, XmpFormat format = XmpFormat.None, float refillRatio = 0.75f)
    {
        _format = format;
        _refillRatio = refillRatio;
        _rate = rate;

        try
        {
            Context = libxmp.xmp_create_context();
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("The libxmp library could not be loaded. If you are running under Linux, try installing libxmp4 / libxmp-dev via your distribution's package manager.");
            throw;
        }
    }

    #region Play
    [Obsolete("PlayListWithTimeoutAsync contains playback orchestration logic which is not audio-engine agnostic. Use GetAudioChunksAsync and handle timing/buffering in your audio engine instead.")]
    public async Task PlayListWithTimeoutAsync(string[] pathsArray, TimeSpan timeout, bool loop = true, CancellationToken cancellationToken = default)
    {
        do
        {
            foreach (string path in pathsArray)
            {
                var songPlayTime = GetEstimatedTotalPlayTime(path);
                var playingStart = DateTime.UtcNow;
                await ReadAsync(path, false, cancellationToken: cancellationToken);
                var played = DateTime.UtcNow - playingStart;
                try
                {
                    await Task.Delay(songPlayTime - played + timeout, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    //ignored
                }
                finally
                {
                    //Player.Stop();
                }
            }
        } while (loop && !cancellationToken.IsCancellationRequested);
    }
    public async Task<bool> ReadAsync(string path, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        await foreach (var _ in ReadInternal(loop, cancellationToken))
        {
            // Consume the async enumerable
        }
        return true;
    }
    public async Task ReadAsync(bool loop = true, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in ReadInternal(loop, cancellationToken))
        {
            // Consume the async enumerable
        }
    }
    public bool PlayBlocking(string path, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        ReadBlocking(loop, cancellationToken);
        return true;
    }
    public void ReadBlocking(bool loop = true, CancellationToken cancellationToken = default)
    {
        foreach (var _ in ReadInternal(loop, cancellationToken).ToBlockingEnumerable(cancellationToken))
        {
            // Consume frames
        }
    }
    public async Task<bool> PlayAsync(string path, int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;

        await foreach (var _ in GetAudioChunksAsync(buffersize, loop, cancellationToken))
        {
            // Consume the async enumerable
        }
        return true;
    }
    public bool PlayBlocking(string path, int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        ReadBlocking(buffersize, loop, cancellationToken);
        return true;
    }
    public void ReadBlocking(int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        foreach (var _ in ReadInternal(buffersize, loop, cancellationToken).ToBlockingEnumerable(cancellationToken))
        {
            // Consume buffers
        }
    }

    private async IAsyncEnumerable<byte[]> ReadInternal(bool loop, CancellationToken cancellationToken)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }

        byte[] buffer = new byte[(int)XmpLimits.MaxFramesize];

        while (GetFrame(buffer, out int length, out int loopcounter) && (loop || loopcounter == 0) && !cancellationToken.IsCancellationRequested)
        {
            yield return buffer;
        }
        EndPlayer();
        ReleaseModule();
    }

    private async IAsyncEnumerable<byte[]> ReadInternal(int buffersize, bool loop, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }

        byte[] buffer = new byte[buffersize];

        while (ReadBuffer(buffer, loop) && !cancellationToken.IsCancellationRequested)
        {
            yield return buffer;
        }
        EndPlayer();
        ReleaseModule();
    }

    /// <summary>
    /// Gets audio chunks asynchronously. This is the recommended method for audio engine integration.
    /// </summary>
    /// <param name="buffersize">Size of each audio chunk in bytes</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of audio data chunks</returns>
    public async IAsyncEnumerable<byte[]> GetAudioChunksAsync(int buffersize, bool loop = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var buffer in ReadInternal(buffersize, loop, cancellationToken))
        {
            yield return buffer;
        }
    }

    /// <summary>
    /// Gets audio frames with metadata asynchronously.
    /// </summary>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of audio frames with metadata</returns>
    public async IAsyncEnumerable<AudioFrame> GetFramesAsync(bool loop = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!GetFrame(out var fi))
                break;

            if (!loop && fi.loop_count > 0)
                break;

            var metadata = CreateMetadataFromFrameInfo(fi);
            var data = new byte[fi.buffer_size];
            unsafe
            {
                fixed (byte* dest = data)
                {
                    NativeMemory.Copy(fi.buffer, dest, (nuint)fi.buffer_size);
                }
            }

            yield return new AudioFrame
            {
                Data = data,
                Metadata = metadata,
                IsEndOfStream = false
            };
        }

        EndPlayer();
        ReleaseModule();
    }

    /// <summary>
    /// Fills a provided buffer with audio data.
    /// </summary>
    /// <param name="buffer">The buffer to fill</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="bytesWritten">Number of bytes actually written to the buffer</param>
    /// <param name="metadata">Metadata about the current playback position</param>
    /// <returns>True if more data is available, false if end of stream</returns>
    public unsafe bool FillBuffer(Span<byte> buffer, bool loop, out int bytesWritten, out FrameMetadata metadata)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }

        bool hasMore = GetFrame(out var fi);

        if (!hasMore || (!loop && fi.loop_count > 0))
        {
            bytesWritten = 0;
            metadata = CreateMetadataFromFrameInfo(fi);
            if (!hasMore)
            {
                EndPlayer();
                ReleaseModule();
            }
            return false;
        }

        int bytesToCopy = Math.Min(buffer.Length, fi.buffer_size);
        fixed (byte* dest = buffer)
        {
            NativeMemory.Copy(fi.buffer, dest, (nuint)bytesToCopy);
        }

        bytesWritten = bytesToCopy;
        metadata = CreateMetadataFromFrameInfo(fi);
        return true;
    }

    /// <summary>
    /// Fills a provided buffer with audio data asynchronously.
    /// </summary>
    /// <param name="buffer">The buffer to fill</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Buffer fill result with metadata</returns>
    public async ValueTask<BufferFillResult> FillBufferAsync(Memory<byte> buffer, bool loop, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            bool hasMore = FillBuffer(buffer.Span, loop, out int bytesWritten, out var metadata);
            return new BufferFillResult
            {
                BytesWritten = bytesWritten,
                IsComplete = !hasMore,
                Metadata = metadata
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the next frame and returns it with metadata.
    /// </summary>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="frame">The audio frame if available</param>
    /// <returns>True if a frame was retrieved, false if end of stream</returns>
    public unsafe bool GetNextFrame(bool loop, out AudioFrame frame)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }

        bool hasMore = GetFrame(out var fi);

        if (!hasMore || (!loop && fi.loop_count > 0))
        {
            frame = new AudioFrame
            {
                Data = ReadOnlyMemory<byte>.Empty,
                Metadata = CreateMetadataFromFrameInfo(fi),
                IsEndOfStream = true
            };

            if (!hasMore)
            {
                EndPlayer();
                ReleaseModule();
            }
            return false;
        }

        var data = new byte[fi.buffer_size];
        fixed (byte* dest = data)
        {
            NativeMemory.Copy(fi.buffer, dest, (nuint)fi.buffer_size);
        }

        frame = new AudioFrame
        {
            Data = data,
            Metadata = CreateMetadataFromFrameInfo(fi),
            IsEndOfStream = false
        };

        return true;
    }

    private static FrameMetadata CreateMetadataFromFrameInfo(xmp_frame_info fi)
    {
        return new FrameMetadata
        {
            LoopCount = fi.loop_count,
            Position = TimeSpan.FromMilliseconds(fi.time),
            Pattern = fi.pattern,
            Row = fi.row,
            Frame = fi.frame,
            Speed = fi.speed,
            Bpm = fi.bpm,
            TotalTimeMs = fi.total_time,
            Sequence = fi.sequence,
            VirtualChannelsUsed = fi.virt_used
        };
    }

    /// <summary>
    /// Gets audio format information for the file specified by the filepath.
    /// The module will be loaded and the player will be started to extract format information,
    /// but playback will be reset to the beginning before returning.
    /// </summary>
    /// <returns>Audio format information, or null if module is invalid</returns>
    public unsafe AudioFormatInfo? GetAudioFormatFromFile(string filepath)
    {
        LoadModule(filepath);
        var state = GetPlayerState();
        if (state == XmpPlayerStates.Unloaded)
            return null;

        // Get a frame to extract format information
        var fi = new xmp_frame_info();

        if (state == XmpPlayerStates.Loaded)
        {
            StartPlayer();
            int error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return null;
            libxmp.xmp_get_frame_info(Context, &fi);
            // Reset playback position
            libxmp.xmp_seek_time(Context, 0);
        }
        else // Playing state
        {
            int error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return null;
            libxmp.xmp_get_frame_info(Context, &fi);
        }

        // XmpFormat.None defaults to stereo 16-bit signed output
        // Only mono if XmpFormat.Mono flag is explicitly set
        int outputChannels = _format.HasFlag(XmpFormat.Mono) ? 1 : 2;
        int bitsPerSample = _format.HasFlag(XmpFormat.Eightbit) ? 8 : 16;

        ReleaseModule();

        return new AudioFormatInfo
        {
            SampleRate = _rate,
            Channels = outputChannels,
            BitsPerSample = bitsPerSample,
            Format = _format,
            EstimatedDuration = TimeSpan.FromMilliseconds(fi.total_time)
        };
    }

    /// <summary>
    /// Gets audio format information for the currently loaded module.
    /// The module must be loaded and the player must be started before calling this method.
    /// </summary>
    /// <returns>Audio format information, or null if no module is loaded</returns>
    public unsafe AudioFormatInfo? GetAudioFormat()
    {
        var state = GetPlayerState();
        if (state == XmpPlayerStates.Unloaded)
            return null;

        // Get a frame to extract format information
        var fi = new xmp_frame_info();

        if (state == XmpPlayerStates.Loaded)
        {
            StartPlayer();
            int error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return null;
            libxmp.xmp_get_frame_info(Context, &fi);
            // Reset playback position
            libxmp.xmp_seek_time(Context, 0);
        }
        else // Playing state
        {
            int error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return null;
            libxmp.xmp_get_frame_info(Context, &fi);
        }

        // XmpFormat.None defaults to stereo 16-bit signed output
        // Only mono if XmpFormat.Mono flag is explicitly set
        int outputChannels = _format.HasFlag(XmpFormat.Mono) ? 1 : 2;
        int bitsPerSample = _format.HasFlag(XmpFormat.Eightbit) ? 8 : 16;

        return new AudioFormatInfo
        {
            SampleRate = _rate,
            Channels = outputChannels,
            BitsPerSample = bitsPerSample,
            Format = _format,
            EstimatedDuration = TimeSpan.FromMilliseconds(fi.total_time)
        };
    }

    /// <summary>
    /// Opens an audio stream for the currently loaded module.
    /// This provides standard .NET Stream access to audio data for maximum compatibility.
    /// </summary>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="bufferSize">Internal buffer size for the stream</param>
    /// <returns>A readable stream of audio data</returns>
    public Stream OpenAudioStream(bool loop = false, int bufferSize = 8192)
    {
        return new XmpAudioStream(this, loop, bufferSize);
    }

    /// <summary>
    /// Opens an audio stream for the specified module file.
    /// This provides standard .NET Stream access to audio data for maximum compatibility.
    /// </summary>
    /// <param name="path">Path to the module file</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="bufferSize">Internal buffer size for the stream</param>
    /// <returns>A readable stream of audio data, or null if the module failed to load</returns>
    public Stream? OpenAudioStream(string path, bool loop = false, int bufferSize = 8192)
    {
        if (!LoadModule(path))
            return null;

        return new XmpAudioStream(this, loop, bufferSize);
    }

    /// <summary>
    /// Opens an audio stream asynchronously for the specified module file.
    /// </summary>
    /// <param name="path">Path to the module file</param>
    /// <param name="loop">Whether to loop playback</param>
    /// <param name="bufferSize">Internal buffer size for the stream</param>
    /// <returns>A readable stream of audio data, or null if the module failed to load</returns>
    public Task<Stream?> OpenAudioStreamAsync(string path, bool loop = false, int bufferSize = 8192)
    {
        return Task.Run(() => OpenAudioStream(path, loop, bufferSize));
    }

    /// <summary>
    /// Gets the next audio frame and copies it to a new Span.
    /// </summary>
    /// <param name="buffer">Output span containing the frame data</param>
    /// <param name="length">Number of bytes in the frame</param>
    /// <param name="loopcounter">Current loop count</param>
    /// <returns>True if a frame was retrieved, false if end of stream</returns>
    public unsafe bool GetFrame(out Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = GetFrame(out var fi);
        buffer = new Span<byte>(fi.buffer, fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    /// <summary>
    /// Gets the next audio frame and copies it to the provided Span.
    /// </summary>
    /// <param name="buffer">Buffer to copy the frame data into</param>
    /// <param name="length">Number of bytes copied</param>
    /// <param name="loopcounter">Current loop count</param>
    /// <returns>True if a frame was retrieved, false if end of stream</returns>
    public unsafe bool GetFrame(Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = GetFrame(out var fi);
        fixed (byte* ptr = buffer)
            NativeMemory.Copy(fi.buffer, ptr, (nuint) fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    /// <summary>
    /// Gets the next audio frame as a raw pointer.
    /// </summary>
    /// <param name="buffer">Pointer to the frame data</param>
    /// <param name="bufferSize">Size of the frame in bytes</param>
    /// <param name="loopcounter">Current loop count</param>
    /// <returns>True if a frame was retrieved, false if end of stream</returns>
    public unsafe bool GetFrame(out void* buffer, out int bufferSize, out int loopcounter)
    {
        var fi = new xmp_frame_info();
        int error = libxmp.xmp_play_frame(Context);
        bool ret = !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
        libxmp.xmp_get_frame_info(Context, &fi);
        buffer = fi.buffer;
        bufferSize = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    private unsafe bool GetFrame(out xmp_frame_info frameInfo)
    {
        var fi = new xmp_frame_info();
        int error = libxmp.xmp_play_frame(Context);
        bool ret = !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
        libxmp.xmp_get_frame_info(Context, &fi);
        frameInfo = fi;
        return ret;
    }

    [Obsolete("Use GetFrame instead. PlayFrame will be removed in a future version.")]
    public unsafe bool PlayFrame(out Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = GetFrame(out var fi);
        buffer = new Span<byte>(fi.buffer, fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    [Obsolete("Use GetFrame instead. PlayFrame will be removed in a future version.")]
    public unsafe bool PlayFrame(Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = GetFrame(out var fi);
        fixed (byte* ptr = buffer)
            NativeMemory.Copy(fi.buffer, ptr, (nuint) fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    [Obsolete("Use GetFrame instead. PlayFrame will be removed in a future version.")]
    public unsafe bool PlayFrame(out void* buffer, out int bufferSize, out int loopcounter)
    {
        var fi = new xmp_frame_info();
        int error = libxmp.xmp_play_frame(Context);
        bool ret = !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
        libxmp.xmp_get_frame_info(Context, &fi);
        buffer = fi.buffer;
        bufferSize = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }

    [Obsolete("Use GetFrame instead. PlayFrame will be removed in a future version.")]
    private unsafe bool PlayFrame(out xmp_frame_info frameInfo)
    {
        var fi = new xmp_frame_info();
        int error = libxmp.xmp_play_frame(Context);
        bool ret = !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
        libxmp.xmp_get_frame_info(Context, &fi);
        frameInfo = fi;
        return ret;
    }
    private int _readBufferCallCount = 0;

    public unsafe bool ReadBuffer(byte[] buffer, bool loop)
    {
        int error;

        fixed (byte* ptr = &buffer[0])
            error = libxmp.xmp_play_buffer(Context, ptr, buffer.Length, loop ? 0 : 1);

        return !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
    }

    /// <summary>
    /// Reads audio buffer from XMP and returns the actual number of bytes filled.
    /// Use this instead of ReadBuffer when you need to know exactly how many bytes were written.
    /// </summary>
    /// <param name="buffer">The buffer to fill with audio data</param>
    /// <param name="loop">Whether to loop the module</param>
    /// <returns>Number of bytes actually written to the buffer, or -1 if end of playback</returns>
    public unsafe int ReadBufferWithSize(byte[] buffer, bool loop)
    {
        int error;

        fixed (byte* ptr = &buffer[0])
            error = libxmp.xmp_play_buffer(Context, ptr, buffer.Length, loop ? 0 : 1);

        // Check if playback ended or error occurred
        if (IsInInvalidState(error) || error == (int)XmpErrorCodes.End)
            return -1;

        // Get frame info to see how much buffer was actually filled
        var frameInfo = new xmp_frame_info();
        libxmp.xmp_get_frame_info(Context, &frameInfo);

        // Log diagnostics
        if (_readBufferCallCount < 10 || _readBufferCallCount % 100 == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[XMP] ReadBuffer #{_readBufferCallCount}: bufferRequested={buffer.Length}, bufferFilled={frameInfo.buffer_size}, error={error}");
            System.Diagnostics.Debug.WriteLine($"      Position: pos={frameInfo.pos}, pattern={frameInfo.pattern}, row={frameInfo.row}, frame={frameInfo.frame}, time={frameInfo.time}ms");
            System.Diagnostics.Debug.WriteLine($"      Tempo: {frameInfo.bpm} BPM, speed={frameInfo.speed}, frame_time={frameInfo.frame_time}µs");
        }

        _readBufferCallCount++;

        return frameInfo.buffer_size;
    }
#endregion
    public XmpPlayerStates GetPlayerState()
    {
        return (XmpPlayerStates) GetParameter(XmpPlayerParameters.State);
    }
    public unsafe bool StartPlayer()
    {
        if (GetPlayerState() != XmpPlayerStates.Loaded)
        {
            throw new XmpIllegalStateException(XmpErrorCodes.State, "You MUST load a module before starting the player!");
        }
        int error = libxmp.xmp_start_player(Context, _rate, (int)_format);
        return !IsInInvalidState(error);
    }
    public unsafe void EndPlayer()
    {
        libxmp.xmp_end_player(Context);
    }
    private unsafe void ReleaseModule()
    {
        libxmp.xmp_release_module(Context);
    }
    public unsafe bool LoadModule(string path)
    {
        var error = libxmp.xmp_load_module(Context, path);
        return !IsInInvalidState(error);
    }
    public unsafe int GetParameter(XmpPlayerParameters param)
    {
        var error = libxmp.xmp_get_player(Context, (int) param);
        _ = IsInInvalidState(error);
        return error;
    }
#region SetVolume
    public int SetVolume(int volume)
    {
        return SetParameter(XmpPlayerParameters.Volume, Math.Clamp(volume, 0, 100));
    }
    public int SetVolume(uint volume)
    {
        return SetParameter(XmpPlayerParameters.Volume, (int)Math.Clamp(volume, 0u, 100u));
    }
    public int SetVolume(float volume)
    {
        return SetParameter(XmpPlayerParameters.Volume, Math.Clamp((int)MathF.Round(volume * 100f), 0, 100));
    }
    public int SetVolume(double volume)
    {
        return SetParameter(XmpPlayerParameters.Volume, Math.Clamp((int)Math.Round(volume * 100d), 0, 100));
    }
#endregion
    /**
     * WARNING, CHECK DOKUMENTATION BEFORE USE!
     */
    public unsafe int SetParameter(XmpPlayerParameters param, int value)
    {
        if (GetPlayerState() != XmpPlayerStates.Playing)
        {
            throw new XmpIllegalStateException(XmpErrorCodes.State, "Player must be STARTED before you can set parameters!");
        }
        var error = libxmp.xmp_set_player(Context, (int) param, value);
        _ = IsInInvalidState(error);
        return error;
    }

    public int SetInterpolation(XmpInterpolations interpolations)
    {
        return SetParameter(XmpPlayerParameters.Interp, (int)interpolations);
    }
    
    public record TestInfo(string Name, string Format);
    public static unsafe bool TestModule(string path, [NotNullWhen(true)] out TestInfo? testInfo)
    {
        var nativetestInfo = new xmp_test_info();
        bool isModule = libxmp.xmp_test_module(path, &nativetestInfo) == 0;
        if (isModule is false)
        {
            testInfo = null;
            return false;
        }
        testInfo = new TestInfo(nativetestInfo.Name, nativetestInfo.Type);
        return isModule;
    }
    /**
     * Warning! This is costly to call, use GetEstimatedTotalPlayTime instead if applicable
     */
    public unsafe TimeSpan GetTotalPlayTime(string path)
    {
        LoadModule(path);
        StartPlayer();
        var fi = new xmp_frame_info();
        ulong totalTime = 0;
        while (fi.loop_count == 0)
        {
            int error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return TimeSpan.Zero;
        
            libxmp.xmp_get_frame_info(Context, &fi);
            totalTime += (ulong)fi.frame_time;
        }
        return TimeSpan.FromMicroseconds(totalTime);
    }
    public unsafe TimeSpan GetEstimatedTotalPlayTime(string path, uint samples = 1)
    {
        LoadModule(path);
        StartPlayer();
        var fi = new xmp_frame_info();
        ulong totalTime = 0;
        int error = libxmp.xmp_play_frame(Context);
        
        if (IsInInvalidState(error))
            return TimeSpan.Zero;
        
        libxmp.xmp_get_frame_info(Context, &fi);
        totalTime += (ulong)fi.total_time;
        int positions = (int) (fi.total_time / samples);
        for (int i = 1; i < samples; i++)
        {
            if (!SkipToPosition(positions * i))
            {
                return TimeSpan.Zero;
            }
            error = libxmp.xmp_play_frame(Context);
            if (IsInInvalidState(error))
                return TimeSpan.Zero;
            
            libxmp.xmp_get_frame_info(Context, &fi);
            totalTime += (ulong)fi.total_time;
        }
        EndPlayer();
        ReleaseModule();
        // ReSharper disable once PossibleLossOfFraction
        return TimeSpan.FromMilliseconds(totalTime / samples);
    }
    public static unsafe IEnumerable<string?> GetFormatList()
    {
        sbyte** nativeList = libxmp.xmp_get_format_list();
        if (nativeList == null)
            return Array.Empty<string>();

        var managedList = new List<string?>();
        int index = 0;

        while (true)
        {
            IntPtr currentPtr = Marshal.ReadIntPtr((IntPtr)nativeList, index * nint.Size);
            if (currentPtr == nint.Zero)
                break;

            string? format = Marshal.PtrToStringAnsi(currentPtr);
            managedList.Add(format);
            index++;
        }

        return managedList;
    }
    private bool IsInInvalidState(int error)
    {
        if (error >= (int)XmpErrorCodes.End)
            return false;
        EndPlayer();
        ReleaseModule();
        Dispose();
        throw new XmpIllegalStateException((XmpErrorCodes) error);
    }
    public unsafe bool SkipToPosition(int milliseconds)
    {
        int error = libxmp.xmp_seek_time(Context, milliseconds);
        return !IsInInvalidState(error);
    }
#region IDisposeable
    private unsafe void ReleaseUnmanagedResources()
    {
        libxmp.xmp_free_context(Context);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose()
    {
        if (_hasBeenDisposed)
            return;
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
        _hasBeenDisposed = true;
    }
    ~Xmp()
    {
        ReleaseUnmanagedResources();
    }
#endregion
}