using Microsoft.Win32;
using SmoothFrames.Models;
using SmoothFrames.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmoothFrames;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GameEntry> _games = new();
    private string? _rtssDirectory;

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

    private void RefreshRtssStatus()
    {
        _rtssDirectory = RtssLocator.FindInstallDirectory();
        var installed = _rtssDirectory is not null;
        var running = installed && RtssLocator.IsRunning();

        RtssStatusText.Text = running ? "RTSS ready" : installed ? "Start RTSS" : "RTSS not found";
        RtssIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            running ? "#8AE7CF" : installed ? "#F5BD69" : "#F08C91"));
    }

    private void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var game = GamePicker.SelectedItem as GameEntry;
        SelectedGamePath.Text = game?.FullPath ?? "No game selected yet";
        if (game is not null)
            StatusText.Text = $"Selected {game.DisplayName}. The per-game cap will be stored for {game.Executable}.";
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
        if (FrameIntervalText is null)
            return;

        if (int.TryParse(FpsInput?.Text, out var fps) && fps > 0)
            FrameIntervalText.Text = (1000d / fps).ToString("0.00");
        else
            FrameIntervalText.Text = "—";
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
        if (!RtssLocator.IsRunning())
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
