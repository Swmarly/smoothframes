using System.Text.Json;

namespace SmoothFrames.Services;

internal sealed class ProfileStore
{
    public Dictionary<string, int> Caps { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastGame { get; set; }
    public string? LoadWarning { get; private set; }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmoothFrames", "profiles.json");
    private sealed record Data(string? LastGame, Dictionary<string, int> Caps);
    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            if (data?.Caps is null) throw new JsonException("Missing profiles");
            foreach (var (path, fps) in data.Caps)
                if (Path.IsPathFullyQualified(path) && fps is >= 15 and <= 1000) Caps[path] = fps;
            LastGame = data.LastGame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadWarning = "Saved settings could not be read. Your games are still available through Refresh or Browse.";
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Data(LastGame, Caps), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
