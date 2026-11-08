using System.Text;
using System.Text.Json;

namespace CommSpot.Client.Core;

public sealed record ChatEntry(string From, string Text, long At, bool Mine);

/// <summary>
/// Saves chat history on this PC only (the server never stores messages). One file per conversation:
/// the room, or one per person for private messages. Keeps the newest <see cref="MaxEntries"/>.
/// </summary>
public sealed class ChatStore
{
    public const int MaxEntries = 1000;
    public const string Room = "room";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly object _gate = new();

    public ChatStore(string directory)
    {
        _dir = directory;
    }

    /// <summary>The conversation key for private messages with someone (names are case-insensitive).</summary>
    public static string Direct(string name) => "dm-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));

    private string PathFor(string key)
    {
        foreach (char c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                throw new ArgumentException("Bad conversation key.", nameof(key));
            }
        }
        return System.IO.Path.Combine(_dir, key + ".json");
    }

    public IReadOnlyList<ChatEntry> Load(string key)
    {
        lock (_gate)
        {
            try
            {
                string path = PathFor(key);
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024)
                {
                    return [];
                }
                return JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(path), Json) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return []; // a damaged file never stops the app
            }
        }
    }

    public void Append(string key, ChatEntry entry)
    {
        lock (_gate)
        {
            var list = Load(key).ToList();
            list.Add(entry);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(0, list.Count - MaxEntries);
            }
            try
            {
                Directory.CreateDirectory(_dir);
                string path = PathFor(key);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Couldn't save (disk full, permissions): the message still shows; history just misses it.
            }
        }
    }
}
