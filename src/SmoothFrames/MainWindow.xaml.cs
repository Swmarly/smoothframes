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
    private readonly ProfileStore _profiles = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private NativeSession? _session;
    private bool _busy, _closing;
    private DateTime _lastFrames;
    private readonly Queue<double> _recent = new();
    private readonly System.Windows.Forms.NotifyIcon _tray = new();

    public MainWindow()
    {
        InitializeComponent();
        GamePicker.ItemsSource = _games;
        _profiles.Load();
        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.Text = "SmoothFrames";
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open SmoothFrames", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Pause cap", null, (_, _) => Dispatcher.Invoke(() => PauseCap()));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        _tray.ContextMenuStrip = menu;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { _tray.Visible = true; Hide(); } };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = "Finishing attachment — please wait before closing."; } };
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); _tray.Visible = false; }
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshGames();
        UpdateFrameInterval();
        if (_profiles.LoadWarning is not null) StatusText.Text = _profiles.LoadWarning;
        _timer.Tick += Tick;
        _timer.Start();
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _session?.Dispose();
        _tray.Dispose();
    }
    private void RefreshGames_Click(object sender, RoutedEventArgs e) => RefreshGames();
    private void RefreshGames()
    {
        var previous = (GamePicker.SelectedItem as GameEntry)?.FullPath ?? _profiles.LastGame;
        var discovered = new List<GameEntry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    var path = process.MainModule?.FileName;
                    if (path is not null) discovered.Add(new GameEntry(Path.GetFileName(path), path, process.MainWindowTitle, process.Id));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        foreach (var path in _profiles.Caps.Keys.Concat(_games.Select(g => g.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!discovered.Any(g => g.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
                discovered.Add(new GameEntry(Path.GetFileName(path), path, null, null));
        _games.Clear();
        foreach (var game in discovered.OrderBy(g => g.ProcessId is null).ThenBy(g => g.DisplayName)) _games.Add(game);
        GamePicker.SelectedItem = _games.FirstOrDefault(g => g.FullPath.Equals(previous, StringComparison.OrdinalIgnoreCase));
        UpdateButtons();
    }
    private void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedGameStatus is null) return;
        var game = GamePicker.SelectedItem as GameEntry;
        SelectedGameStatus.Text = game is null ? "Choose a running game, or browse to save a cap for later."
            : $"{game.Executable} · {(game.ProcessId is null ? "not running" : $"PID {game.ProcessId}")}";
        SelectedGameStatus.ToolTip = game?.FullPath;
        if (game is not null && _profiles.Caps.TryGetValue(game.FullPath, out var cap)) FpsInput.Text = cap.ToString();
        ResetGraph();
        UpdateButtons();
    }
    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a game", Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var existing = _games.FirstOrDefault(g => g.FullPath.Equals(picker.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { existing = new GameEntry(Path.GetFileName(picker.FileName), picker.FileName, null, null); _games.Insert(0, existing); }
        GamePicker.SelectedItem = existing;
        RefreshGames();
    }
    private void Preset_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string value }) FpsInput.Text = value; }
    private void FpsInput_TextChanged(object sender, TextChangedEventArgs e) { UpdateFrameInterval(); UpdateButtons(); }
    private void UpdateFrameInterval()
    {
        if (TargetText is null) return;
        var valid = int.TryParse(FpsInput.Text, out var fps) && fps is >= 15 and <= 1000;
        TargetText.Text = valid ? $"{1000d / fps:0.00} ms target" : "Enter 15–1000 FPS";
        FrameGraph.TargetIntervalMs = valid ? 1000d / fps : 8.33;
    }
    private void UpdateButtons()
    {
        if (ApplyButton is null) return;
        var game = GamePicker.SelectedItem as GameEntry;
        ApplyButton.IsEnabled = !_busy && game is not null && int.TryParse(FpsInput.Text, out var fps) && fps is >= 15 and <= 1000;
        ApplyButton.Content = _busy ? "Attaching…" : game?.ProcessId is null ? "Save cap" : "Apply cap";
        PauseButton.IsEnabled = !_busy && _session is { Cap: > 0 };
        GamePicker.IsEnabled = !_busy;
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = !_busy;
    }
    private void ResetGraph()
    {
        _recent.Clear(); FrameGraph.ClearSamples(); _lastFrames = DateTime.MinValue;
        LiveMetricsText.Text = "FPS —    Avg —    P99 —";
        EmptyGraphLabel.Visibility = Visibility.Visible;
        EmptyGraphLabel.Text = "Apply a cap to start monitoring";
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (_session is null) return;
        if (_session.HasExited)
        {
            _session.Dispose(); _session = null;
            EngineStatusText.Text = "Game closed";
            StatusText.Text = "Game closed. Start it again, refresh the list, and apply your saved cap.";
            ResetGraph(); UpdateButtons(); return;
        }
        _session.Heartbeat();
        var samples = _session.ReadSamples();
        if (samples.Length > 0)
        {
            _lastFrames = DateTime.UtcNow;
            EngineStatusText.Text = _session.Cap > 0 ? $"{_session.Cap} FPS active · {_session.Api}" : $"Paused · {_session.Api}";
            EngineIndicator.Fill = (Brush)FindResource("Blue");
            if (GamePicker.SelectedItem is not GameEntry game || game.ProcessId != _session.ProcessId) return;
            foreach (var sample in samples) _recent.Enqueue(sample);
            while (_recent.Count > 2048) _recent.Dequeue();
            // Each graph point is the mean of newly captured present intervals (4 updates/sec).
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                FrameGraph.AddSample(samples.Average());
                var sorted = _recent.OrderBy(v => v).ToArray();
                var avg = sorted.Average();
                LiveMetricsText.Text = $"{1000 / avg:0.0} FPS    Avg {avg:0.00} ms    P99 {sorted[(int)Math.Ceiling(sorted.Length * .99) - 1]:0.00} ms";
                EmptyGraphLabel.Visibility = Visibility.Collapsed;
            }
        }
        else if (DateTime.UtcNow - _lastFrames > TimeSpan.FromSeconds(2))
        {
            EngineStatusText.Text = "Attached · waiting for frames";
            EngineIndicator.Fill = (Brush)FindResource("Muted");
            LiveMetricsText.Text = "FPS —    Avg —    P99 —";
            _recent.Clear(); FrameGraph.ClearSamples();
            EmptyGraphLabel.Text = "No supported frames yet — focus the game. Vulkan is not supported.";
            EmptyGraphLabel.Visibility = Visibility.Visible;
        }
    }
    private async void ApplyLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || GamePicker.SelectedItem is not GameEntry game || !int.TryParse(FpsInput.Text, out var fps) || fps is < 15 or > 1000) return;
        _busy = true; UpdateButtons();
        try
        {
            _profiles.Caps[game.FullPath] = fps;
            _profiles.LastGame = game.FullPath;
            _profiles.Save();
            if (game.ProcessId is not int pid) { StatusText.Text = "Cap saved. Start the game, refresh the list, then apply."; return; }
            if (_session?.ProcessId != pid || _session.HasExited)
            {
                _session?.Dispose(); _session = null;
                EngineStatusText.Text = "Attaching…";
                StatusText.Text = "Attaching the bundled engine…";
                _session = await NativeSession.AttachAsync(pid, game.FullPath);
                if (_closing) { _session.Dispose(); _session = null; return; }
            }
            _session.SetCap(fps);
            ResetGraph();
            EngineStatusText.Text = "Attached · waiting for frames";
            StatusText.Text = $"{game.Executable} · {fps} FPS requested. Minimize to tray to keep it running; exiting clears the cap.";
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = _session is null ? "Ready" : "Attached";
            StatusText.Text = ex is System.ComponentModel.Win32Exception ? "Cannot access the game. Match its Windows permissions, then retry." : ex.Message;
        }
        finally { _busy = false; UpdateButtons(); }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseCap();
    private void PauseCap()
    {
        if (_session is null) return;
        _session.SetCap(0);
        EngineStatusText.Text = "Paused";
        StatusText.Text = "Cap paused. Your saved value is kept; click Apply cap to resume.";
        UpdateButtons();
    }
}
