using System.Runtime.InteropServices;

namespace SmoothFrames.Services;

/// <summary>
/// Small binding to RTSS's documented profile interface in RTSSHooks64.dll.
/// SmoothFrames does not ship or modify RTSS binaries.
/// </summary>
public sealed class RtssProfileClient : IDisposable
{
    private const string LimitProperty = "FramerateLimit";
    private IntPtr _module;
    private readonly LoadProfileDelegate _loadProfile;
    private readonly SaveProfileDelegate _saveProfile;
    private readonly SetProfilePropertyDelegate _setProfileProperty;
    private readonly UpdateProfilesDelegate _updateProfiles;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void LoadProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void SaveProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetProfilePropertyDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string propertyName,
        IntPtr propertyData,
        uint propertySize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateProfilesDelegate();

    public RtssProfileClient(string installDirectory)
    {
        var dllPath = Path.Combine(installDirectory, "RTSSHooks64.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("RTSS profile interface was not found.", dllPath);
        }

        _module = NativeLibrary.Load(dllPath);
        try
        {
            _loadProfile = Load<LoadProfileDelegate>("LoadProfile");
            _saveProfile = Load<SaveProfileDelegate>("SaveProfile");
            _setProfileProperty = Load<SetProfilePropertyDelegate>("SetProfileProperty");
            _updateProfiles = Load<UpdateProfilesDelegate>("UpdateProfiles");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ApplyLimit(string executable, int framesPerSecond)
    {
        ValidateExecutable(executable);
        if (framesPerSecond is < 15 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Choose a limit from 15 to 1000 FPS.");
        }

        SetLimit(executable, (uint)framesPerSecond);
    }

    public void ClearLimit(string executable)
    {
        ValidateExecutable(executable);
        // Reset only this profile property's value; preserve any other RTSS profile settings.
        SetLimit(executable, 0);
    }

    private void SetLimit(string executable, uint value)
    {
        _loadProfile(executable);
        var data = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(data, unchecked((int)value));
            if (!_setProfileProperty(LimitProperty, data, sizeof(uint)))
            {
                throw new InvalidOperationException("RTSS rejected the frame-limit setting. Try running SmoothFrames with the same permissions as RTSS.");
            }

            _saveProfile(executable);
            _updateProfiles();
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private T Load<T>(string exportName) where T : Delegate
    {
        var address = NativeLibrary.GetExport(_module, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            !string.Equals(Path.GetFileName(executable), executable, StringComparison.OrdinalIgnoreCase) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a Windows game executable (.exe).", nameof(executable));
        }
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
            _module = IntPtr.Zero;
        }
    }
}
