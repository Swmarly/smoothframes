namespace SmoothFrames.Models;

public sealed record GameEntry(string Executable, string FullPath, string? WindowTitle, int? ProcessId)
{
    public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle)
        ? Path.GetFileNameWithoutExtension(Executable)
        : WindowTitle;

    public override string ToString() => DisplayName;
}
