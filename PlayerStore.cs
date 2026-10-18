using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarpforgeOpenServer;

// Single-player state persisted to data/player.json. Kept as raw JSON so it can be edited by hand
// while we discover what the game expects.
public class PlayerStore
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    readonly string _playerPath;
    readonly string _titleDataPath;
    readonly object _lock = new();

    public JsonObject Player { get; }
    public string DataDir { get; }

    JsonObject _titleData;
    DateTime _titleDataStamp;

    // Re-read whenever the file changes, so title data can be edited while the server runs
    public JsonObject TitleData
    {
        get
        {
            lock (_lock)
            {
                var stamp = File.GetLastWriteTimeUtc(_titleDataPath);
                if (stamp != _titleDataStamp)
                {
                    _titleData = LoadOrCreate(_titleDataPath, () => new JsonObject());
                    _titleDataStamp = stamp;
                }
                return _titleData;
            }
        }
    }

    public PlayerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        DataDir = dataDir;
        _playerPath = Path.Combine(dataDir, "player.json");
        _titleDataPath = Path.Combine(dataDir, "titledata.json");

        Player = LoadOrCreate(_playerPath, DefaultPlayer);
        _titleData = LoadOrCreate(_titleDataPath, () => new JsonObject());
        _titleDataStamp = File.GetLastWriteTimeUtc(_titleDataPath);
    }

    public string PlayFabId => (string)Player["PlayFabId"]!;
    public string DisplayName => (string?)Player["DisplayName"] ?? "Commander";
    public string Created => (string)Player["Created"]!;

    public JsonObject Section(string name)
    {
        if (Player[name] is not JsonObject obj)
            Player[name] = obj = new JsonObject();
        return obj;
    }

    public JsonArray Inventory()
    {
        if (Player["Inventory"] is not JsonArray arr)
            Player["Inventory"] = arr = new JsonArray();
        return arr;
    }

    public int NextDataVersion()
    {
        var v = ((int?)Player["DataVersion"] ?? 0) + 1;
        Player["DataVersion"] = v;
        return v;
    }

    public int DataVersion => (int?)Player["DataVersion"] ?? 0;

    public void Save()
    {
        lock (_lock)
            File.WriteAllText(_playerPath, Player.ToJsonString(Pretty));
    }

    static JsonObject LoadOrCreate(string path, Func<JsonObject> create)
    {
        if (File.Exists(path))
            return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var obj = create();
        File.WriteAllText(path, obj.ToJsonString(Pretty));
        return obj;
    }

    static JsonObject DefaultPlayer() => new()
    {
        // PlayFab IDs are 16 hex chars
        ["PlayFabId"] = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
        ["DisplayName"] = "Commander",
        ["SteamId"] = "0",
        ["Created"] = Api.Now(),
        ["DataVersion"] = 0,
        // Key -> { Value, LastUpdated, Permission }, same shape PlayFab returns
        ["UserData"] = new JsonObject(),
        ["UserReadOnlyData"] = new JsonObject(),
        ["VirtualCurrency"] = new JsonObject(),
        ["Inventory"] = new JsonArray(),
        // Name -> value
        ["Statistics"] = new JsonObject(),
    };
}
