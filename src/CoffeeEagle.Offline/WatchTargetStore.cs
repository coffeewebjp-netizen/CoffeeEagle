using System.Text.Json;

namespace CoffeeEagle.Offline;

/// <summary>Phone-owned choices. They never imply deletion on either device.</summary>
public sealed class WatchTargetStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record State(int Version, HashSet<string> Keys);

    private HashSet<string> Load()
    {
        if (!File.Exists(path)) return [];
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
        if (state is null || state.Version != 1 || state.Keys is null || state.Keys.Count > 10000 || state.Keys.Any(x => !AudioRequest.IsHash(x)))
            throw new InvalidDataException("Watch同期対象の設定を読み込めません。");
        return state.Keys;
    }

    public async Task<HashSet<string>> ReadAsync()
    {
        await _gate.WaitAsync();
        try { return Load(); } finally { _gate.Release(); }
    }

    public async Task SetAsync(IEnumerable<string> keys, bool included)
    {
        var changed = keys.Distinct().ToArray();
        if (changed.Any(x => !AudioRequest.IsHash(x))) throw new InvalidDataException("同期対象IDが無効です。");
        await _gate.WaitAsync();
        var temp = path + ".partial";
        try
        {
            var selected = Load();
            foreach (var key in changed) { if (included) selected.Add(key); else selected.Remove(key); }
            if (selected.Count > 10000) throw new InvalidOperationException("Watch同期対象は10,000件までです。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new State(1, selected)));
            File.Move(temp, path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { _gate.Release(); } }
    }
}
