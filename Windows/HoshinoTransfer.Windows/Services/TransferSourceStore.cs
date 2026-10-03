using System.IO;
using System.Text.Json;

namespace HoshinoTransfer.Windows.Services;

/// <summary>Stores only local source paths needed for resumable uploads in the signed-in user's LocalAppData.</summary>
public sealed class TransferSourceStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoshinoTransfer", "transfer-sources.json");
    private readonly object _sync = new();
    private Dictionary<string, string[]> _entries;

    public TransferSourceStore()
    {
        _entries = Load();
    }

    public void Save(string transferId, IReadOnlyList<string> paths)
    {
        lock (_sync)
        {
            _entries[transferId] = paths.Select(Path.GetFullPath).ToArray();
            Flush();
        }
    }

    public bool TryGet(string transferId, out IReadOnlyList<string> paths)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(transferId, out var stored))
            {
                paths = stored;
                return stored.All(File.Exists);
            }
            paths = [];
            return false;
        }
    }

    public void Remove(string transferId)
    {
        lock (_sync) { if (_entries.Remove(transferId)) Flush(); }
    }

    private Dictionary<string, string[]> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path), Options) ?? new(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private void Flush()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, Options));
        File.Move(temporary, _path, overwrite: true);
    }
}
