using Microsoft.Win32;
using System.Diagnostics;

namespace SmoothFrames.Services;

public static class RtssLocator
{
    public static string? FindInstallDirectory()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Unwinder\RTSS");
                var installDir = key?.GetValue("InstallDir") as string;
                if (!string.IsNullOrWhiteSpace(installDir) &&
                    File.Exists(Path.Combine(installDir, "RTSSHooks64.dll")))
                {
                    return installDir;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Try the other registry view and the standard install paths below.
            }
            catch (System.Security.SecurityException)
            {
                // Try the other registry view and the standard install paths below.
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server")
        };

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "RTSSHooks64.dll")));
    }

    public static bool IsRunning()
    {
        Process[] processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcessesByName("RTSS");
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
