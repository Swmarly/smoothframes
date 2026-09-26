using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SmoothFrames.Services;

internal sealed class NativeSession : IDisposable
{
    private const int Magic = 0x53465032, Capacity = 512, Size = 2080;
    private readonly Process _game;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private uint _sequence;
    public int ProcessId => _game.Id;
    public string Path { get; }
    public int Cap { get; private set; }
    public bool HasExited { get { try { return _game.HasExited; } catch { return true; } } }
    public int Status => _view.ReadInt32(16);
    public string Api => _view.ReadInt32(20) switch { 1 => "DXGI", 2 => "Direct3D 9", 3 => "OpenGL", _ => "—" };

    private NativeSession(Process game, string path)
    {
        _game = game;
        Path = path;
        _mapping = MemoryMappedFile.CreateOrOpen($@"Local\SmoothFrames.v2.{game.Id}", Size);
        _view = _mapping.CreateViewAccessor(0, Size);
        var magic = _view.ReadInt32(0);
        if (magic != 0 && (magic != Magic || _view.ReadInt32(4) != 2))
        {
            _view.Dispose(); _mapping.Dispose();
            throw new InvalidOperationException("An incompatible engine is attached. Restart this game first.");
        }
        _view.Write(0, Magic);
        _view.Write(4, 2);
        SetCap(0);
        Heartbeat();
        _sequence = _view.ReadUInt32(24);
    }

    public static async Task<NativeSession> AttachAsync(int pid, string expectedPath)
    {
        var game = Process.GetProcessById(pid);
        NativeSession? session = null;
        try
        {
            if (!string.Equals(game.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected process changed. Refresh games and select it again.");
            if (!IsWow64Process2(game.Handle, out var machine, out var nativeMachine) ||
                (machine != 0 && machine != 0x014c) || (machine == 0 && nativeMachine != 0x8664))
                throw new InvalidOperationException("This build supports x86 and x64 games on Windows x64.");
            var folder = EngineFiles.Ensure(machine == 0x014c ? "x86" : "x64");
            session = new NativeSession(game, expectedPath);
            using var injector = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(folder, "SmoothFramesInjector.exe"))
            {
                ArgumentList = { pid.ToString(), game.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("Could not start the bundled engine.");
            var error = injector.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await injector.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!injector.HasExited) injector.Kill();
                throw new InvalidOperationException("Attachment timed out. Restart the game before retrying.");
            }
            if (injector.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
            var until = Stopwatch.StartNew();
            while (session.Status == 0 && !session.HasExited && until.Elapsed < TimeSpan.FromSeconds(10))
            {
                session.Heartbeat();
                await Task.Delay(100);
            }
            if (session.HasExited) throw new InvalidOperationException("The game closed during attachment.");
            if (session.Status != 1) throw new InvalidOperationException("The engine could not initialize. Restart the game before retrying.");
            return session;
        }
        catch
        {
            session?.Dispose();
            if (session is null) game.Dispose();
            throw;
        }
    }

    public void Heartbeat() => _view.Write(12, Environment.TickCount);
    public void SetCap(int fps) { Cap = fps; _view.Write(8, fps * 1000); }
    public double[] ReadSamples()
    {
        var end = _view.ReadUInt32(24);
        var count = Math.Min(unchecked(end - _sequence), (uint)Capacity);
        if (count == 0) return Array.Empty<double>();
        var values = new double[(int)count];
        var start = unchecked(end - count);
        for (uint i = 0; i < count; i++) values[i] = _view.ReadInt32(32 + (unchecked(start + i) % Capacity) * 4) / 1000d;
        // Discard an overwritten snapshot rather than graphing mixed generations of the ring.
        if (unchecked(_view.ReadUInt32(24) - start) > Capacity) { _sequence = end; return Array.Empty<double>(); }
        _sequence = end;
        return values.Where(value => value > 0 && double.IsFinite(value)).ToArray();
    }
    public void Dispose() { SetCap(0); _view.Dispose(); _mapping.Dispose(); _game.Dispose(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}

internal static class EngineFiles
{
    public static string Ensure(string architecture)
    {
        var assembly = typeof(EngineFiles).Assembly;
        var files = new[] { "SmoothFramesHook.dll", "SmoothFramesInjector.exe" };
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmoothFrames", "engine", assembly.GetName().Version!.ToString(), architecture);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            using var resource = assembly.GetManifestResourceStream($"Engine.{architecture}.{file}")
                ?? throw new InvalidOperationException("This build is missing its native engine. Download the complete release build.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var destination = System.IO.Path.Combine(folder, file);
            if (File.Exists(destination) && SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(bytes))) continue;
            var temporary = destination + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, destination, true); }
            catch (IOException) { throw new IOException("Close games using an older SmoothFrames engine, then reopen SmoothFrames."); }
        }
        return folder;
    }
}
