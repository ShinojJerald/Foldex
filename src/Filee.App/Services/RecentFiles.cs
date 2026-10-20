// Files opened in the PDF workspace and the tools, newest first (recent-files.json in the data folder).

using System.Text.Json;

namespace Filee.App.Services;

public sealed class RecentFiles(Filee.Core.Settings.UserDataStore store)
{
    private const int Max = 15;
    private readonly object _gate = new();
    private string FilePath => Path.Combine(store.Directory, "recent-files.json");

    public event EventHandler? Changed;

    /// <summary>Recent files that still exist.</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    var items = File.Exists(FilePath) ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.StringArray) : null;
                    return (items ?? []).Where(File.Exists).ToList();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    return [];
                }
            }
        }
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            var items = Items.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Prepend(Path.GetFullPath(path)).Take(Max).ToArray();
            try
            {
                Directory.CreateDirectory(store.Directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(items, AppJsonContext.Default.StringArray));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // a recent list is a convenience
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
