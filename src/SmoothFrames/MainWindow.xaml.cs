using Microsoft.Win32;
using SmoothFrames.Models;
using SmoothFrames.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmoothFrames;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromMilliseconds(50), Priority = DispatcherPriority.Background };
    private string? _rtssDirectory;
    private bool _isRtssRunning;
    private RtssTelemetryReader? _telemetryReader;
    private DateTime _nextInstallProbeUtc = DateTime.MinValue;
    private DateTime _nextRtssCheckUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private bool _hasTelemetrySample;

    public MainWindow()
    {
        InitializeComponent();
        GamePicker.ItemsSource = _games;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshGames();
        RefreshRtssStatus();
        UpdateFrameInterval();
        _telemetryTimer.Tick += TelemetryTimer_Tick;
        _telemetryTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _telemetryTimer.Stop();
        _telemetryReader?.Dispose();
        _telemetryReader = null;
    }

    private void RefreshGames_Click(object sender, RoutedEventArgs e) => RefreshGames();

    private void RefreshGames()
    {
        var previous = (GamePicker.SelectedItem as GameEntry)?.Executable;
        var discovered = new List<GameEntry>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    continue;

                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                discovered.Add(new GameEntry(Path.GetFileName(path), path, process.MainWindowTitle, process.Id));
            }
            catch
            {
                // Processes owned by another user or protected by Windows may not expose their executable path.
            }
            finally
            {
                process.Dispose();
            }
        }

        var distinct = discovered
            .GroupBy(game => game.Executable, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(game => !string.IsNullOrWhiteSpace(game.WindowTitle)).First())
            .OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _games.Clear();
        foreach (var game in distinct)
            _games.Add(game);

        if (previous is not null)
            GamePicker.SelectedItem = _games.FirstOrDefault(game => game.Executable.Equals(previous, StringComparison.OrdinalIgnoreCase));

        RefreshRtssStatus();
    }

    private void RefreshRtssStatus(bool forceDiscovery = true)
    {
        var now = DateTime.UtcNow;
        if (forceDiscovery || now >= _nextInstallProbeUtc)
        {
            _rtssDirectory = RtssLocator.FindInstallDirectory();
            _nextInstallProbeUtc = now.AddSeconds(30);
        }

        var installed = _rtssDirectory is not null;
        _isRtssRunning = installed && RtssLocator.IsRunning();

        RtssStatusText.Text = _isRtssRunning ? "RTSS ready" : installed ? "Start RTSS" : "RTSS not found";
        RtssIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            _isRtssRunning ? "#8AE7CF" : installed ? "#F5BD69" : "#F08C91"));
    }

    private void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var game = GamePicker.SelectedItem as GameEntry;
        if (SelectedGameStatus is not null)
        {
            SelectedGameStatus.Text = game is null
                ? "Choose a running game or browse to its executable."
                : $"{game.DisplayName}  ·  {(game.ProcessId is null ? "waiting for process" : "running")}";
            SelectedGameStatus.ToolTip = game?.FullPath;
        }

        if (FrameGraph is not null)
            FrameGraph.ClearSamples();
        _hasTelemetrySample = false;
        _lastSampleUtc = DateTime.MinValue;
        if (LiveMetricsText is not null)
            LiveMetricsText.Text = "FPS —   |   Time —   |   API —";
        if (EmptyGraphLabel is not null)
        {
            EmptyGraphLabel.Text = game is null
                ? "Select a game to monitor frametimes"
                : "Start the selected game with RTSS enabled";
            EmptyGraphLabel.Visibility = Visibility.Visible;
        }
        if (StatusText is not null)
            StatusText.Text = game is null ? "Ready" : $"Ready · cap applies to {game.Executable}";
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a game executable",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
            return;

        var selected = new GameEntry(Path.GetFileName(picker.FileName), picker.FileName, null, null);
        var existing = _games.FirstOrDefault(game => game.Executable.Equals(selected.Executable, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _games.Insert(0, selected);
            GamePicker.SelectedItem = selected;
        }
        else
        {
            GamePicker.SelectedItem = existing;
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value })
            FpsInput.Text = value;
    }

    private void FpsInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateFrameInterval();

    private void UpdateFrameInterval()
    {
        if (TargetText is null)
            return;

        if (int.TryParse(FpsInput?.Text, out var fps) && fps > 0)
        {
            var intervalMs = 1000d / fps;
            TargetText.Text = $"Target {intervalMs:0.00} ms";
            if (FrameGraph is not null)
                FrameGraph.TargetIntervalMs = intervalMs;
        }
        else
        {
            TargetText.Text = "Target —";
            if (FrameGraph is not null)
                FrameGraph.TargetIntervalMs = 8.33;
        }
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextRtssCheckUtc)
        {
            _nextRtssCheckUtc = now.AddSeconds(2);
            RefreshRtssStatus(forceDiscovery: false);
            if (!_isRtssRunning)
            {
                _telemetryReader?.Dispose();
                _telemetryReader = null;
            }
            else
            {
                if (_telemetryReader is not null && !_telemetryReader.IsMappingValid)
                {
                    _telemetryReader.Dispose();
                    _telemetryReader = null;
                }
                _telemetryReader ??= RtssTelemetryReader.TryConnect();
            }
        }

        var game = GamePicker.SelectedItem as GameEntry;
        if (game is not null && _telemetryReader?.TryRead(game.ProcessId, game.Executable, out var sample) == true)
        {
            _lastSampleUtc = now;
            _hasTelemetrySample = true;
            LiveMetricsText.Text = $"FPS {sample.FramesPerSecond:0}   |   Time {sample.FrametimeMs:0.00} ms   |   API {sample.Api}";
            FrameGraph.AddSample(sample.FrametimeMs);
            EmptyGraphLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_hasTelemetrySample && now - _lastSampleUtc > TimeSpan.FromSeconds(1))
        {
            _hasTelemetrySample = false;
            FrameGraph.ClearSamples();
        }

        if (!_hasTelemetrySample)
        {
            LiveMetricsText.Text = _telemetryReader is null
                ? "FPS —   |   Time —   |   RTSS offline"
                : "FPS —   |   Time —   |   Waiting for frames";
            EmptyGraphLabel.Text = game is null
                ? "Select a game to monitor frametimes"
                : _telemetryReader is null
                    ? "Start RTSS to view frametimes"
                    : "Start the selected game with RTSS enabled";
            EmptyGraphLabel.Visibility = Visibility.Visible;
        }
    }

    private async void ApplyLimit_Click(object sender, RoutedEventArgs e)
    {
        var game = GamePicker.SelectedItem as GameEntry;
        if (game is null)
        {
            StatusText.Text = "Choose a game first, either from the running list or with Browse for game.";
            return;
        }
        if (!int.TryParse(FpsInput.Text, out var fps) || fps is < 15 or > 1000)
        {
            StatusText.Text = "Enter an FPS cap from 15 to 1000.";
            return;
        }

        try
        {
            var directory = RequireRtss();
            await Task.Run(() =>
            {
                using var client = new RtssProfileClient(directory);
                client.ApplyLimit(game.Executable, fps);
            });
            StatusText.Text = $"Applied {fps} FPS to {game.Executable}. RTSS has been asked to refresh active profiles.";
            RefreshRtssStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = ExplainFailure(ex);
        }
    }

    private async void RemoveLimit_Click(object sender, RoutedEventArgs e)
    {
        var game = GamePicker.SelectedItem as GameEntry;
        if (game is null)
        {
            StatusText.Text = "Choose a game first.";
            return;
        }

        try
        {
            var directory = RequireRtss();
            await Task.Run(() =>
            {
                using var client = new RtssProfileClient(directory);
                client.ClearLimit(game.Executable);
            });
            StatusText.Text = $"Set {game.Executable}'s per-game cap to 0. Check RTSS global settings if you use a global cap.";
            RefreshRtssStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = ExplainFailure(ex);
        }
    }

    private string RequireRtss()
    {
        RefreshRtssStatus();
        if (_rtssDirectory is null)
            throw new InvalidOperationException("RTSS was not found. Install RivaTuner Statistics Server, then click Refresh games.");
        if (!_isRtssRunning)
            throw new InvalidOperationException("RTSS is installed but not running. Start RivaTuner Statistics Server and try again.");
        return _rtssDirectory;
    }

    private static string ExplainFailure(Exception exception)
    {
        var message = exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException.Message
            : exception.Message;
        return $"Could not update RTSS: {message}";
    }

}
