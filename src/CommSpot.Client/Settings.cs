using System.Text.Json;

namespace CommSpot.Client;

/// <summary>Remembers server, name and devices between runs. The access key is never saved.</summary>
internal sealed class Settings
{
    public string Server { get; set; } = "commspot.net";
    public string Name { get; set; } = string.Empty;
    public string? Mic { get; set; }
    public string? Speaker { get; set; }
    public bool PushToTalk { get; set; }
    public string? PushToTalkKey { get; set; }
    public double Sensitivity { get; set; } = 180;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommSpot", "settings.json");

    public static Settings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
