using System.Runtime.InteropServices;

namespace SmoothFrames.Services;

public sealed class RtssTelemetryReader : IDisposable
{
    private const uint FileMapRead = 0x0004;
    private const uint RtssSignature = 0x52545353;
    private const uint RtssSignatureReversed = 0x53535452;
    private const uint MinimumVersion = 0x00020000;
    private const uint ModernApiFlagsVersion = 0x0002000A;
    private const int HeaderSize = 36;
    private const int MinimumAppEntrySize = 284;
    private readonly IntPtr _mapping;
    private readonly IntPtr _view;
    private bool _disposed;

    private RtssTelemetryReader(IntPtr mapping, IntPtr view)
    {
        _mapping = mapping;
        _view = view;
    }

    public bool IsMappingValid => !_disposed && HasValidHeader();

    public static RtssTelemetryReader? TryConnect()
    {
        var mapping = OpenFileMapping(FileMapRead, false, "RTSSSharedMemoryV2");
        if (mapping == IntPtr.Zero)
            return null;

        var view = MapViewOfFile(mapping, FileMapRead, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero)
        {
            CloseHandle(mapping);
            return null;
        }

        var reader = new RtssTelemetryReader(mapping, view);
        if (!reader.HasValidHeader())
        {
            reader.Dispose();
            return null;
        }

        return reader;
    }

    public bool TryRead(int? processId, string executable, out RtssTelemetrySample sample)
    {
        sample = default;
        if (_disposed || _view == IntPtr.Zero || !HasValidHeader())
            return false;

        var entrySize = ReadUInt32(8);
        var arrayOffset = ReadUInt32(12);
        var entryCount = ReadUInt32(16);
        var version = ReadUInt32(4);
        var totalSize = (ulong)entrySize * entryCount;
        if (entrySize < MinimumAppEntrySize || entrySize > 16384 ||
            arrayOffset < HeaderSize || arrayOffset > 1024 * 1024 ||
            entryCount > 4096 || totalSize > 8 * 1024 * 1024)
            return false;

        var targetName = Path.GetFileName(executable);
        for (uint index = 0; index < entryCount; index++)
        {
            var entry = IntPtr.Add(_view, checked((int)(arrayOffset + index * entrySize)));
            var pid = ReadUInt32(entry, 0);
            var name = ReadAnsiName(entry, 4, 260);
            if (processId is int expectedPid && pid != expectedPid)
                continue;
            if (processId is null && !name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (processId is int && !string.IsNullOrWhiteSpace(name) &&
                !name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var time0 = ReadUInt32(entry, 268);
            var time1 = ReadUInt32(entry, 272);
            var frameTimeUs = ReadUInt32(entry, 280);
            var time1After = ReadUInt32(entry, 272);
            var time0After = ReadUInt32(entry, 268);
            if (time0 != time0After || time1 != time1After || frameTimeUs == 0)
                return false;

            var elapsed = unchecked(GetTickCount() - time1);
            if (time1 == 0 || elapsed > 2000)
                return false;

            var frametimeMs = frameTimeUs / 1000d;
            sample = new RtssTelemetrySample(1_000_000d / frameTimeUs, frametimeMs,
                GetApiName(version, ReadUInt32(entry, 264)));
            return true;
        }

        return false;
    }

    private bool HasValidHeader()
    {
        if (_view == IntPtr.Zero)
            return false;

        var signature = ReadUInt32(0);
        var version = ReadUInt32(4);
        return (signature == RtssSignature || signature == RtssSignatureReversed) &&
               version >= MinimumVersion && ReadUInt32(12) >= HeaderSize;
    }

    private uint ReadUInt32(int headerOffset) => unchecked((uint)Marshal.ReadInt32(_view, headerOffset));

    private static uint ReadUInt32(IntPtr entry, int offset) =>
        unchecked((uint)Marshal.ReadInt32(entry, offset));

    private static string ReadAnsiName(IntPtr entry, int offset, int maximumLength)
    {
        var bytes = new byte[maximumLength];
        Marshal.Copy(IntPtr.Add(entry, offset), bytes, 0, bytes.Length);
        var end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static string GetApiName(uint version, uint flags)
    {
        var api = version >= ModernApiFlagsVersion ? flags & 0xFFFF : DecodeLegacyApi(flags);
        return api switch
        {
            1 => "OpenGL",
            2 => "DirectDraw",
            3 => "D3D8",
            4 => "D3D9",
            5 => "D3D9Ex",
            6 => "D3D10",
            7 => "D3D11",
            8 or 9 => "D3D12",
            10 => "Vulkan",
            _ => "RTSS"
        };
    }

    private static uint DecodeLegacyApi(uint flags)
    {
        if ((flags & 0x01000000) != 0) return 7;
        if ((flags & 0x00100000) != 0) return 6;
        if ((flags & 0x00010000) != 0) return 1;
        if ((flags & 0x00002000) != 0) return 5;
        if ((flags & 0x00001000) != 0) return 4;
        if ((flags & 0x00000100) != 0) return 3;
        if ((flags & 0x00000010) != 0) return 2;
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_view != IntPtr.Zero)
            UnmapViewOfFile(_view);
        if (_mapping != IntPtr.Zero)
            CloseHandle(_mapping);
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMapping(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr fileMapping, uint desiredAccess,
        uint fileOffsetHigh, uint fileOffsetLow, UIntPtr numberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
}

public readonly record struct RtssTelemetrySample(double FramesPerSecond, double FrametimeMs, string Api);
