using System.IO;
using System.Text.Json;

namespace HoshinoTransfer.Windows.Services;

public enum TransferPreference { Auto, DirectWifi, P2P, ServerApi }

/// <summary>Persists the user's transport preference in the signed-in user's LocalAppData.</summary>
public sealed class TransferPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoshinoTransfer", "preferences.json");

    public TransferPreference Load()
    {
        try
        {
            if (!File.Exists(_path)) return TransferPreference.Auto;
            var raw = File.ReadAllText(_path).Trim();
            return Enum.TryParse<TransferPreference>(raw, ignoreCase: true, out var parsed) ? parsed : TransferPreference.Auto;
        }
        catch { return TransferPreference.Auto; }
    }

    public void Save(TransferPreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, preference.ToString());
        }
        catch { }
    }
}
