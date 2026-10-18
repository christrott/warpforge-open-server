using System.Text.Json.Nodes;

namespace WarpforgeOpenServer;

// Handlers for the PlayFab methods the game calls. Each returns the "data" part of PlayFab's
// {code, status, data} envelope. Shapes follow the public PlayFab REST docs:
// https://learn.microsoft.com/rest/api/playfab/client/
public class Api
{
    readonly PlayerStore _store;
    readonly string _titleId;

    public Api(PlayerStore store, string titleId)
    {
        _store = store;
        _titleId = titleId;
    }

    // PlayFab's JSON parser accepts this ISO-8601 UTC format
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    static string In(TimeSpan t) => (DateTime.UtcNow + t).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public Dictionary<string, Func<JsonObject, JsonNode?>> Handlers() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Auth: every login flavour gets the same fake account
        ["LoginWithSteam"] = Login,
        ["LoginWithCustomID"] = Login,
        ["LoginWithEmailAddress"] = Login,
        ["LoginWithPlayFab"] = Login,
        ["LoginWithGoogleAccount"] = Login,
        ["GetEntityToken"] = _ => EntityToken(),

        // Account / profile
        ["GetAccountInfo"] = _ => new JsonObject { ["AccountInfo"] = AccountInfo() },
        ["GetPlayerProfile"] = _ => new JsonObject { ["PlayerProfile"] = PlayerProfile() },
        ["UpdateUserTitleDisplayName"] = UpdateDisplayName,

        // Title data
        ["GetTitleData"] = req => new JsonObject { ["Data"] = FilterTitleData(req["Keys"] as JsonArray) },
        ["GetTitleNews"] = _ => new JsonObject { ["News"] = new JsonArray() },

        // Player data
        ["GetUserData"] = req => UserData("UserData", req),
        ["GetUserReadOnlyData"] = req => UserData("UserReadOnlyData", req),
        ["UpdateUserData"] = UpdateUserData,

        // Economy
        ["GetUserInventory"] = _ => new JsonObject
        {
            ["Inventory"] = _store.Inventory().DeepClone(),
            ["VirtualCurrency"] = _store.Section("VirtualCurrency").DeepClone(),
            ["VirtualCurrencyRechargeTimes"] = new JsonObject(),
        },
        ["GetCatalogItems"] = _ => new JsonObject { ["Catalog"] = new JsonArray() },
        ["GetStoreItems"] = req => new JsonObject { ["Store"] = new JsonArray(), ["StoreId"] = req["StoreId"]?.DeepClone() },

        // Statistics / leaderboards
        ["GetPlayerStatistics"] = _ => new JsonObject { ["Statistics"] = Statistics() },
        ["UpdatePlayerStatistics"] = UpdateStatistics,
        ["GetLeaderboard"] = _ => new JsonObject { ["Leaderboard"] = new JsonArray(), ["Version"] = 0 },
        ["GetLeaderboardAroundPlayer"] = _ => new JsonObject { ["Leaderboard"] = new JsonArray(), ["Version"] = 0 },

        // Entity objects (PlayFab "Data" API): small per-player JSON blobs
        ["GetObjects"] = GetObjects,
        ["SetObjects"] = SetObjects,

        // Telemetry: accept and drop
        ["WritePlayerEvent"] = _ => new JsonObject { ["EventId"] = Guid.NewGuid().ToString("N") },
        ["WriteEvents"] = _ => new JsonObject { ["AssignedEventIds"] = new JsonArray() },
        ["WriteTelemetryEvents"] = _ => new JsonObject { ["AssignedEventIds"] = new JsonArray() },
        ["ReportDeviceInfo"] = _ => new JsonObject(),
    };

    JsonNode Login(JsonObject req)
    {
        var steamTicket = (string?)req["SteamTicket"];
        if (steamTicket != null) _store.Player["LastSteamTicketLength"] = steamTicket.Length;
        _store.Player["LastLogin"] = Now();
        _store.Save();

        var token = EntityToken();
        return new JsonObject
        {
            ["PlayFabId"] = _store.PlayFabId,
            ["SessionTicket"] = $"{_store.PlayFabId}-FAKE-{Guid.NewGuid():N}",
            ["NewlyCreated"] = false,
            ["LastLoginTime"] = Now(),
            ["SettingsForUser"] = new JsonObject
            {
                ["NeedsAttribution"] = false,
                ["GatherDeviceInfo"] = false,
                ["GatherFocusInfo"] = false,
            },
            ["EntityToken"] = token,
            ["InfoResultPayload"] = InfoPayload(req["InfoRequestParameters"] as JsonObject),
            ["TreatmentAssignment"] = new JsonObject { ["Variants"] = new JsonArray(), ["Variables"] = new JsonArray() },
        };
    }

    // GetPlayerCombinedInfo payload; login requests ask for it via InfoRequestParameters
    JsonObject InfoPayload(JsonObject? p)
    {
        bool Want(string flag) => (bool?)p?[flag] ?? false;
        var payload = new JsonObject();
        if (Want("GetUserAccountInfo")) payload["AccountInfo"] = AccountInfo();
        if (Want("GetUserInventory")) payload["UserInventory"] = _store.Inventory().DeepClone();
        if (Want("GetUserVirtualCurrency"))
        {
            payload["UserVirtualCurrency"] = _store.Section("VirtualCurrency").DeepClone();
            payload["UserVirtualCurrencyRechargeTimes"] = new JsonObject();
        }
        if (Want("GetUserData"))
        {
            payload["UserData"] = FilterData("UserData", p?["UserDataKeys"] as JsonArray);
            payload["UserDataVersion"] = _store.DataVersion;
        }
        if (Want("GetUserReadOnlyData"))
        {
            payload["UserReadOnlyData"] = FilterData("UserReadOnlyData", p?["UserReadOnlyDataKeys"] as JsonArray);
            payload["UserReadOnlyDataVersion"] = _store.DataVersion;
        }
        if (Want("GetCharacterList")) payload["CharacterList"] = new JsonArray();
        if (Want("GetCharacterInventories")) payload["CharacterInventories"] = new JsonArray();
        if (Want("GetTitleData")) payload["TitleData"] = FilterTitleData(p?["TitleDataKeys"] as JsonArray);
        if (Want("GetPlayerStatistics")) payload["PlayerStatistics"] = Statistics();
        if (Want("GetPlayerProfile")) payload["PlayerProfile"] = PlayerProfile();
        return payload;
    }

    JsonObject EntityToken() => new()
    {
        ["EntityToken"] = $"FAKE-ENTITY-{Guid.NewGuid():N}",
        ["TokenExpiration"] = In(TimeSpan.FromDays(1)),
        ["Entity"] = new JsonObject
        {
            ["Id"] = _store.PlayFabId,
            ["Type"] = "title_player_account",
            ["TypeString"] = "title_player_account",
        },
    };

    JsonObject AccountInfo() => new()
    {
        ["PlayFabId"] = _store.PlayFabId,
        ["Created"] = _store.Created,
        ["TitleInfo"] = new JsonObject
        {
            ["DisplayName"] = _store.DisplayName,
            ["Origination"] = "Steam",
            ["Created"] = _store.Created,
            ["LastLogin"] = Now(),
            ["FirstLogin"] = _store.Created,
            ["isBanned"] = false,
            ["TitlePlayerAccount"] = new JsonObject { ["Id"] = _store.PlayFabId, ["Type"] = "title_player_account" },
        },
        // UnpackUserProfile dereferences PrivateInfo unconditionally
        ["PrivateInfo"] = new JsonObject { ["Email"] = (string?)_store.Player["Email"] ?? "" },
        ["SteamInfo"] = new JsonObject
        {
            ["SteamId"] = (string?)_store.Player["SteamId"] ?? "0",
            ["SteamName"] = _store.DisplayName,
            ["SteamCountry"] = "AU",
            ["SteamCurrency"] = "AUD",
        },
    };

    JsonObject PlayerProfile() => new()
    {
        ["PublisherId"] = _titleId,
        ["TitleId"] = _titleId,
        ["PlayFabId"] = _store.PlayFabId,
        ["DisplayName"] = _store.DisplayName,
        ["Created"] = _store.Created,
        ["LastLogin"] = Now(),
        ["AvatarUrl"] = null,
        ["Locations"] = new JsonArray(),
        ["Tags"] = TagList(),
        ["TotalValueToDateInUSD"] = 0,
    };

    JsonNode UpdateDisplayName(JsonObject req)
    {
        _store.Player["DisplayName"] = (string?)req["DisplayName"] ?? _store.DisplayName;
        _store.Save();
        return new JsonObject { ["DisplayName"] = _store.DisplayName };
    }

    JsonObject FilterTitleData(JsonArray? keys)
    {
        var result = new JsonObject();
        foreach (var (k, v) in _store.TitleData)
            if (keys == null || keys.Count == 0 || keys.Any(x => (string?)x == k))
                result[k] = TitleDataValue(v);
        return result;
    }

    // PlayFab title data values are always strings. titledata.json may hold objects for readability;
    // they're sent as JSON text, which is what the game deserializes
    static JsonNode? TitleDataValue(JsonNode? v) =>
        v is JsonValue jv && jv.TryGetValue<string>(out var s) ? JsonValue.Create(s) : JsonValue.Create(v?.ToJsonString());

    JsonObject FilterData(string section, JsonArray? keys)
    {
        var result = new JsonObject();
        foreach (var (k, v) in _store.Section(section))
            if (keys == null || keys.Count == 0 || keys.Any(x => (string?)x == k))
                result[k] = v?.DeepClone();
        return result;
    }

    JsonNode UserData(string section, JsonObject req) => new JsonObject
    {
        ["Data"] = FilterData(section, req["Keys"] as JsonArray),
        ["DataVersion"] = _store.DataVersion,
    };

    JsonNode UpdateUserData(JsonObject req)
    {
        var data = _store.Section("UserData");
        if (req["Data"] is JsonObject updates)
            foreach (var (k, v) in updates)
                data[k] = new JsonObject
                {
                    ["Value"] = v?.DeepClone(),
                    ["LastUpdated"] = Now(),
                    ["Permission"] = (string?)req["Permission"] ?? "Private",
                };
        if (req["KeysToRemove"] is JsonArray remove)
            foreach (var k in remove)
                data.Remove((string)k!);
        var version = _store.NextDataVersion();
        _store.Save();
        return new JsonObject { ["DataVersion"] = version };
    }

    JsonArray Statistics()
    {
        var arr = new JsonArray();
        foreach (var (name, value) in _store.Section("Statistics"))
            arr.Add(new JsonObject { ["StatisticName"] = name, ["Value"] = value?.DeepClone(), ["Version"] = 0 });
        return arr;
    }

    JsonNode UpdateStatistics(JsonObject req)
    {
        var stats = _store.Section("Statistics");
        if (req["Statistics"] is JsonArray updates)
            foreach (var s in updates)
                if ((string?)s?["StatisticName"] is { } name)
                    stats[name] = s!["Value"]?.DeepClone();
        _store.Save();
        return new JsonObject();
    }

    JsonObject EntityKey() => new() { ["Id"] = _store.PlayFabId, ["Type"] = "title_player_account" };

    JsonNode GetObjects(JsonObject req)
    {
        var escape = (bool?)req["EscapeObject"] ?? false;
        var objects = new JsonObject();
        foreach (var (name, value) in _store.Section("EntityObjects"))
            objects[name] = new JsonObject
            {
                ["ObjectName"] = name,
                ["DataObject"] = escape ? null : value?.DeepClone(),
                ["EscapedDataObject"] = escape ? value?.ToJsonString() : null,
            };
        return new JsonObject
        {
            ["Entity"] = EntityKey(),
            ["Objects"] = objects,
            ["ProfileVersion"] = _store.DataVersion,
        };
    }

    JsonNode SetObjects(JsonObject req)
    {
        var stored = _store.Section("EntityObjects");
        var results = new JsonArray();
        if (req["Objects"] is JsonArray objects)
            foreach (var o in objects)
            {
                var name = (string?)o?["ObjectName"];
                if (name == null) continue;
                if ((bool?)o!["DeleteObject"] == true) stored.Remove(name);
                else stored[name] = o["DataObject"]?.DeepClone()
                                    ?? ((string?)o["EscapedDataObject"] is { } esc ? JsonNode.Parse(esc) : null);
                results.Add(new JsonObject { ["ObjectName"] = name, ["SetResult"] = "Updated" });
            }
        var version = _store.NextDataVersion();
        _store.Save();
        return new JsonObject { ["ProfileVersion"] = version, ["SetResults"] = results };
    }

    // Built-in results for CloudScript functions, shaped from the game's result classes
    // (Tools/TypeDump). A data/cloudscript/<FunctionName>.json file overrides these.
    public JsonNode? BuiltInCloudScript(string? functionName, JsonObject? parameter) => functionName switch
    {
        // OnUserConnectResult
        "onUserConnected" => new JsonObject
        {
            ["ServerTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["Achievements"] = new JsonObject(),
            ["Segmentation"] = new JsonObject(),
            ["grantedItems"] = new JsonArray(),
            ["OverrideXp"] = null,
        },
        // InitializeLiveopsResult. HandlerData needs an entry per live-ops handler (each handler's
        // HandlerKey); TitleData is where ConfigManager reads the game's configs from
        "InitializeLiveOps" => new JsonObject
        {
            ["HandlerData"] = LiveOpsHandlerData(),
            ["TitleData"] = FilterTitleData(null),
            ["SharedEventData"] = new JsonObject
            {
                ["energyData"] = new JsonObject
                {
                    ["energy"] = 0,
                    ["lastUpdated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
            ["grantedItems"] = new JsonArray(),
        },
        // TwitchDropsProvider enumerates the result directly
        "FetchTwitchDrops" => new JsonArray(),
        // AchievementPersistence[]
        "UpdatePooledAchievements" => new JsonArray(),
        "GrantStarterDeck" => GrantStarterDeck(parameter?["Data"] as JsonObject),
        "UpdateAchievement" => UpdateAchievement(parameter?["Data"] as JsonObject),
        "updatePlayerDecks" => UpdatePlayerDecks(parameter?["Data"] as JsonObject),
        "SetGamemodeDefaultDeck" => SetEventSave((string?)parameter?["Data"]?["eventId"], "defaultDeckId", parameter?["Data"]?["deckId"]),
        "SelectActiveCampaign" => SetPlayerValue("SelectedArmy", parameter?["Data"]?["Army"]),
        "AddPlayerTag" => AddPlayerTag((string?)parameter?["Data"]?["tag"]),
        "GetLeaderboard" => Leaderboard((string?)parameter?["Data"]?["leaderboardKey"]),
        "onPracticeMatchFinished" => MatchFinished(parameter?["Data"] as JsonObject, matchType: 6),
        _ => null,
    };

    // CardArmy enum values, matching the campaign_<Army> event IDs in data/liveops/Campaign.json
    static readonly Dictionary<string, int> Armies = new()
    {
        ["Ultramarines"] = 10, ["Goff"] = 20, ["SaimHann"] = 30, ["Sautekh"] = 40, ["BlackLegion"] = 50,
        ["Leviathan"] = 60, ["TauEmpire"] = 70, ["Sororitas"] = 80, ["Genestealers"] = 90,
        ["AstraMilitarum"] = 100, ["DarkAngels"] = 110, ["EmperorsChildren"] = 120, ["SpaceWolves"] = 130,
    };

    // GrantStarterDeckResult { rewards, army, deck, extraRewards }. The deck reward references the
    // prebuilt deck by id; the client resolves it (and its card list) from its own asset library
    JsonNode GrantStarterDeck(JsonObject? data)
    {
        var deckId = (string?)data?["starterDeckId"] ?? "";
        var eventId = (string?)data?["eventID"] ?? "";
        var army = Armies.GetValueOrDefault(eventId.Replace("campaign_", ""), 0);

        _store.Player["StarterDeck"] = deckId;
        _store.Player["SelectedArmy"] = army;
        _store.Save();

        return new JsonObject
        {
            ["rewards"] = new JsonArray(),
            ["army"] = army,
            ["deck"] = new JsonObject
            {
                ["item"] = new JsonObject { ["targetId"] = deckId },
                ["quantity"] = 1,
            },
            ["extraRewards"] = new JsonArray(),
        };
    }

    // AchievementPersistence for { Id, TotalPoints }; progress kept in player.json
    JsonNode UpdateAchievement(JsonObject? data)
    {
        var id = (string?)data?["Id"] ?? "";
        var value = (int?)data?["TotalPoints"] ?? 0;
        var all = _store.Section("Achievements");
        var highest = Math.Max(value, (int?)all[id]?["highestValue"] ?? 0);
        var persistence = new JsonObject
        {
            ["id"] = id, ["value"] = value, ["highestValue"] = highest, ["enabled"] = true,
            ["collectedRewards"] = (int?)all[id]?["collectedRewards"] ?? 0,
            ["premiumCollectedRewards"] = 0, ["resetProgress"] = false,
        };
        all[id] = persistence.DeepClone();
        _store.Save();
        return persistence;
    }

    // Decks live in user data "Decks" as a JSON list of AzureDeckInfo, which login reads back
    JsonNode? UpdatePlayerDecks(JsonObject? data)
    {
        if ((string?)data?["Deck"] is not { } deckJson) return null;
        var deck = JsonNode.Parse(deckJson)!.AsObject();
        var id = (string?)deck["id"];

        var userData = _store.Section("UserData");
        var decks = (string?)userData["Decks"]?["Value"] is { } existing
            ? JsonNode.Parse(existing)!.AsArray()
            : new JsonArray();
        foreach (var d in decks.ToList())
            if ((string?)d?["id"] == id) decks.Remove(d);
        decks.Add(deck);

        userData["Decks"] = new JsonObject
        {
            ["Value"] = decks.ToJsonString(),
            ["LastUpdated"] = Now(),
            ["Permission"] = "Private",
        };
        _store.NextDataVersion();
        _store.Save();
        return null;
    }

    // LeaderboardResult with the player as the only entry; the score is the statistic of the same name.
    // The post-battle screen waits on this
    JsonNode Leaderboard(string? key)
    {
        var score = key != null ? (long?)_store.Section("Statistics")[key] ?? 0 : 0;
        var entry = new JsonObject
        {
            ["entryId"] = _store.PlayFabId, ["value"] = score, ["position"] = 1, ["name"] = _store.DisplayName,
            ["avatarId"] = "", ["groupName"] = "", ["groupId"] = "",
            ["armyScores"] = new JsonObject(), ["highestScores"] = new JsonObject(),
        };
        return new JsonObject
        {
            ["total"] = 1,
            ["totalArmyScore"] = new JsonObject(),
            ["currentEntry"] = entry,
            ["entries"] = new JsonArray(entry.DeepClone()),
            ["needUpdate"] = false,
            ["fetchTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // OnlineMatchResult { EndMatchData, BattleLogData, VictoryCounters }. Practice earns nothing; the
    // match goes into a battle log and win/loss counters kept in player.json
    JsonNode MatchFinished(JsonObject? data, int matchType)
    {
        var won = ((double?)data?["hasWon"] ?? 0) >= 1;
        var eventId = (string?)data?["eventId"] ?? "";

        var counters = _store.Section("VictoryCounters");
        var key = won ? $"{eventId}_won" : $"{eventId}_lost";
        counters[key] = ((int?)counters[key] ?? 0) + 1;

        if (_store.Player["BattleLog"] is not JsonArray log)
            _store.Player["BattleLog"] = log = new JsonArray();
        log.Add(new JsonObject
        {
            ["season"] = 1, ["enemyId"] = (string?)data?["opponentID"] ?? "AI",
            ["matchWin"] = won, ["matchLose"] = !won,
            ["ownExp"] = 0, ["enemyExp"] = 0, ["ownTotalExp"] = 0, ["enemyTotalExp"] = 0,
            ["ownGlobalRating"] = 1000, ["enemyGlobalRating"] = 1000,
            ["ownHeroName"] = (string?)data?["playerHeroId"] ?? "", ["enemyHeroName"] = (string?)data?["opponentHeroId"] ?? "",
            ["ownName"] = (string?)data?["ownName"] ?? _store.DisplayName, ["enemyName"] = (string?)data?["opponentName"] ?? "",
            ["ownSkulls"] = (int?)data?["milestone"] ?? 0, ["enemySkulls"] = (int?)data?["enemymilestone"] ?? 0,
            ["recordingIndex"] = "", ["matchType"] = matchType, ["replaysVersion"] = (string?)data?["ReplaysVersion"] ?? "",
            ["pinned"] = false, ["playerClan"] = "", ["enemyClan"] = "", ["botAltid"] = "",
        });
        while (log.Count > 50) log.RemoveAt(0);
        _store.Save();

        return new JsonObject
        {
            ["EndMatchData"] = new JsonObject
            {
                ["XP"] = 0, ["NewRating"] = 1000, ["EarnedRating"] = 0, ["EarnedSeals"] = 0, ["NewOpponentRating"] = 1000,
                ["ArmyScores"] = new JsonObject(), ["HighestScores"] = new JsonObject(), ["TotalScores"] = new JsonObject(),
                ["ArmyStats"] = new JsonObject(), ["Rewards"] = new JsonArray(), ["RatingBoostState"] = new JsonArray(),
            },
            ["BattleLogData"] = log.DeepClone(),
            ["VictoryCounters"] = counters.DeepClone(),
        };
    }

    // Per-player fields for live-ops event saves, merged over the generated event files
    JsonNode? SetEventSave(string? eventId, string field, JsonNode? value)
    {
        if (string.IsNullOrEmpty(eventId)) return null;
        var saves = _store.Section("EventSaves");
        if (saves[eventId] is not JsonObject save)
            saves[eventId] = save = new JsonObject();
        save[field] = value?.DeepClone();
        _store.Save();
        return null;
    }

    JsonNode? SetPlayerValue(string key, JsonNode? value)
    {
        _store.Player[key] = value?.DeepClone();
        _store.Save();
        return null;
    }

    JsonArray TagList()
    {
        var tags = new JsonArray();
        if (_store.Player["Tags"] is JsonArray stored)
            foreach (var t in stored)
                tags.Add(new JsonObject { ["TagValue"] = (string?)t });
        return tags;
    }

    JsonNode? AddPlayerTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (_store.Player["Tags"] is not JsonArray tags)
            _store.Player["Tags"] = tags = new JsonArray();
        if (!tags.Any(t => (string?)t == tag)) tags.Add(tag);
        _store.Save();
        return null;
    }

    static readonly string[] LiveOpsHandlerKeys =
    {
        "ParentEvents", "Campaign", "Forge", "Inbox", "RankedScoreBoost", "Rewards",
        "ScoringEvent", "Shops", "GameModes", "Missions",
    };

    // LiveopsHandlerData { SharedData: JObject, RawEvents: List<RawEvent> } per handler, no events yet.
    // data/liveops/<HandlerKey>.json, if present, replaces a handler's entry
    JsonObject LiveOpsHandlerData()
    {
        var result = new JsonObject();
        foreach (var key in LiveOpsHandlerKeys)
        {
            var path = Path.Combine(_store.DataDir, "liveops", key + ".json");
            var handler = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
                : new JsonObject { ["SharedData"] = new JsonObject(), ["RawEvents"] = new JsonArray() };

            // Overlay what this player saved for each event (e.g. defaultDeckId)
            var saves = _store.Section("EventSaves");
            if (handler["RawEvents"] is JsonArray events)
                foreach (var e in events)
                    if (e?["Persistence"] is JsonObject persistence &&
                        (string?)persistence["eventId"] is { } eventId && saves[eventId] is JsonObject save)
                        foreach (var (k, v) in save)
                            persistence[k] = v?.DeepClone();
            result[key] = handler;
        }
        return result;
    }

    // CloudScript functions are the game's custom server logic. Until we know what each one should
    // return, a canned result can be dropped in data/cloudscript/<FunctionName>.json
    public static JsonNode CloudScriptResult(string method, string? functionName, JsonNode? result) =>
        method.Equals("ExecuteFunction", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject
            {
                ["FunctionName"] = functionName,
                ["FunctionResult"] = result,
                ["FunctionResultTooLarge"] = false,
                ["ExecutionTimeMilliseconds"] = 1,
            }
            : new JsonObject
            {
                ["FunctionName"] = functionName,
                ["Revision"] = 1,
                ["FunctionResult"] = result,
                ["FunctionResultTooLarge"] = false,
                ["Logs"] = new JsonArray(),
                ["LogsTooLarge"] = false,
                ["ExecutionTimeSeconds"] = 0.001,
                ["ProcessorTimeSeconds"] = 0.001,
                ["MemoryConsumedBytes"] = 0,
                ["APIRequestsIssued"] = 0,
                ["HttpRequestsIssued"] = 0,
            };
}
