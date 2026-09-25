using System.Threading;
using System.Windows;

namespace SmoothFrames;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\SmoothFrames.Desktop.v2", out var first);
        if (!first)
        {
            MessageBox.Show("SmoothFrames is already running. Open it from the notification area.", "SmoothFrames");
            Shutdown(); return;
        }
        if (e.Args.Contains("--verify-engine"))
        {
            try
            {
                Services.EngineFiles.Ensure("x64");
                Services.EngineFiles.Ensure("x86");
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
