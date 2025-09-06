namespace libxmpBindings;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeBindings;
using NAudio.Wave;

public sealed class Xmp : IDisposable
{
    public readonly unsafe sbyte* Context;
    private readonly int _rate;
    private readonly XmpFormat _format;
    private readonly float _refillRatio;
    private bool _hasBeenDisposed = false;
    public unsafe Xmp(IWavePlayer player, int rate = 44100, XmpFormat format = XmpFormat.None, float refillRatio = 0.75f)
    {
        Player = player;
        _rate = rate;
        _format = format;
        _refillRatio = refillRatio;
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
    public IWavePlayer Player { get; }
    
#region Play
    public async Task PlayListWithTimeoutAsync(string[] pathsArray, TimeSpan timeout, bool loop = true, CancellationToken cancellationToken = default)
    {
        do
        {
            foreach (string path in pathsArray)
            {
                var songPlayTime = GetEstimatedTotalPlayTime(path);
                var playingStart = DateTime.UtcNow;
                await PlayAsync(path, false, cancellationToken: cancellationToken);
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
                    Player.Stop();
                }
            }
        } while (loop && !cancellationToken.IsCancellationRequested);
    }
    public async Task<bool> PlayAsync(string path, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        await PlayInternal(loop, cancellationToken);
        return true;
    }
    public Task PlayAsync(bool loop = true, CancellationToken cancellationToken = default)
    {
        return PlayInternal(loop, cancellationToken);
    }
    public bool PlayBlocking(string path, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        PlayBlocking(loop, cancellationToken);
        return true;
    }
    public void PlayBlocking(bool loop = true, CancellationToken cancellationToken = default)
    {
        PlayInternal(loop, cancellationToken).Wait(CancellationToken.None);
    }
    public async Task<bool> PlayAsync(string path, int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        await PlayAsync(buffersize, loop, cancellationToken);
        return true;
    }
    public bool PlayBlocking(string path, int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        if (!LoadModule(path))
            return false;
        
        PlayBlocking(buffersize, loop, cancellationToken);
        return true;
    }
    public void PlayBlocking(int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        PlayInternal(buffersize, loop, cancellationToken).Wait(CancellationToken.None);
    }
    private async Task PlayInternal(bool loop, CancellationToken cancellationToken)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }
        var waveProvider = new BufferedWaveProvider(new WaveFormat(_rate, _format.HasFlag(XmpFormat.Eightbit) ? 8 : 16, _format.HasFlag(XmpFormat.Mono) ? 1 : 2));
        Player.Init(waveProvider);
        byte[] buffer = new byte[(int)XmpLimits.MaxFramesize];
        Player.Play();
        var refillDuration = waveProvider.BufferDuration * _refillRatio;
        while (PlayFrame(buffer, out int length, out int loopcounter) && (loop || loopcounter == 0) && !cancellationToken.IsCancellationRequested)
        {
            while (waveProvider.BufferedBytes >= waveProvider.BufferLength - length)
            {
                await Task.Delay(refillDuration, CancellationToken.None);
            }
            waveProvider.AddSamples(buffer, 0, length);
        }
        EndPlayer();
        ReleaseModule();
    }

    private async Task PlayInternal(int buffersize, bool loop, CancellationToken cancellationToken)
    {
        if (GetPlayerState() == XmpPlayerStates.Loaded)
        {
            StartPlayer();
        }
        var waveProvider = new BufferedWaveProvider(new WaveFormat(_rate, _format.HasFlag(XmpFormat.Eightbit) ? 8 : 16, _format.HasFlag(XmpFormat.Mono) ? 1 : 2));
        byte[] buffer = new byte[buffersize];
        Player.Init(waveProvider);
        Player.Play();
        var refillDuration = waveProvider.BufferDuration * _refillRatio;
        while (PlayBuffer(buffer, loop) && !cancellationToken.IsCancellationRequested)
        {
            while (waveProvider.BufferedBytes >= waveProvider.BufferLength - buffer.Length)
            {
                await Task.Delay(refillDuration, CancellationToken.None);
            }
            waveProvider.AddSamples(buffer, 0, buffer.Length);
        }
        EndPlayer();
        ReleaseModule();
        await Task.Delay(refillDuration * 2, CancellationToken.None);
        Player.Stop();
    }
    public Task PlayAsync(int buffersize, bool loop = true, CancellationToken cancellationToken = default)
    {
        return PlayInternal(buffersize, loop, cancellationToken);
    }
    public unsafe bool PlayFrame(out Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = PlayFrame(out var fi);
        buffer = new Span<byte>(fi.buffer, fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }
    public unsafe bool PlayFrame(Span<byte> buffer, out int length, out int loopcounter)
    {
        var ret = PlayFrame(out var fi);
        fixed (byte* ptr = buffer)
            NativeMemory.Copy(fi.buffer, ptr, (nuint) fi.buffer_size);
        length = fi.buffer_size;
        loopcounter = fi.loop_count;
        return ret;
    }
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
    private unsafe bool PlayFrame(out xmp_frame_info frameInfo)
    {
        var fi = new xmp_frame_info();
        int error = libxmp.xmp_play_frame(Context);
        bool ret = !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
        libxmp.xmp_get_frame_info(Context, &fi);
        frameInfo = fi;
        return ret;
    }
    public unsafe bool PlayBuffer(byte[] buffer, bool loop)
    {
        int error;
        fixed (byte* ptr = &buffer[0])
            error = libxmp.xmp_play_buffer(Context, ptr, buffer.Length, loop ? 0 : 1);
        return !IsInInvalidState(error) && error != (int)XmpErrorCodes.End;
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
    private unsafe void EndPlayer()
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
    private void ReleaseManagedResources()
    {
        try
        {
            Player.Dispose();
        }
        catch (Exception)
        {
            //ignored
        }
    }
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose()
    {
        if (_hasBeenDisposed)
            return;
        ReleaseManagedResources();
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