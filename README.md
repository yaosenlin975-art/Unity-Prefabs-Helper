[English](README.md) | [简体中文](README.zh-CN.md)

# Lin Runtime Prefs Helper

Install `com.lin.runtime-prefs-helper` as an embedded or local UPM package. In this project it is already under `Packages/`. Import `Lin.Runtime.Helper` in scripts that use it. The package depends on `com.unity.nuget.newtonsoft-json`.

## Quick start

### Main APIs

| API | Behavior |
| --- | --- |
| `Set<T>(key, value)` | Adds or replaces a value and saves it immediately. |
| `Get<T>(key)` | Returns the value, or `default(T)` when the key is absent. |
| `Get<T>(key, defaultValue)` / `Get<T>(key, createFunc)` | Returns a fallback for a missing key. The fallback is **not saved** until you call `Set`. |
| `ContainsKey<T>(key)` | Checks whether the key exists. |
| `GetAllKeys<T>()` | Returns a snapshot of keys stored for `T`. |
| `DeleteKey<T>(key)` | Removes one key and saves the change. |
| `Clear<T>()` | Removes **every key of type `T` across the project** and saves the change. |

The value type `T` determines the archive; the key selects an entry within it. Changes to a loaded reference object, including its nested collections, require another `Set` to persist. `GetAllArchiveTypes()` lists only types successfully loaded in this process; it does not scan disk. `GetArchiveFileName(typeof(T))` returns that type's archive file name.

### Example: nested save data

This example stores multiple save slots. Each `PlayerSave` contains a nested progress object, an inventory list, and a currency dictionary. Save it as `PlayerSaveExample.cs` in your project's scripts.

```csharp
using System;
using System.Collections.Generic;
using Lin.Runtime.Helper;

[Serializable]
public sealed class PlayerSave
{
    public string PlayerId;
    public PlayerProgress Progress;
    public List<InventoryItem> Inventory;
    public Dictionary<string, int> Currencies;
}

[Serializable]
public sealed class PlayerProgress
{
    public int Chapter;
    public List<string> CompletedQuestIds;
}

[Serializable]
public sealed class InventoryItem
{
    public string ItemId;
    public int Quantity;
}

public static class PlayerSaveExample
{
    public static PlayerSave LoadOrCreate(string slotKey)
    {
        PlayerSave save = PrefsHelper.Get<PlayerSave>(slotKey, CreateInitialSave);
        if (!PrefsHelper.ContainsKey<PlayerSave>(slotKey))
            PrefsHelper.Set(slotKey, save); // The factory result is not saved automatically.
        return save;
    }

    public static void CompleteQuest(string slotKey, string questId)
    {
        PlayerSave save = LoadOrCreate(slotKey);
        if (save.Progress.CompletedQuestIds.Contains(questId))
            return;

        save.Progress.CompletedQuestIds.Add(questId);
        save.Inventory.Add(new InventoryItem { ItemId = "potion", Quantity = 2 });
        save.Currencies.TryGetValue("gold", out int gold);
        save.Currencies["gold"] = gold + 100;
        PrefsHelper.Set(slotKey, save); // Persist changes inside nested objects and collections.
    }

    public static IEnumerable<string> GetSlotKeys()
    {
        return PrefsHelper.GetAllKeys<PlayerSave>();
    }

    public static void DeleteSlot(string slotKey)
    {
        PrefsHelper.DeleteKey<PlayerSave>(slotKey);
    }

    private static PlayerSave CreateInitialSave()
    {
        return new PlayerSave
        {
            PlayerId = "player-1",
            Progress = new PlayerProgress
            {
                Chapter = 1,
                CompletedQuestIds = new List<string>()
            },
            Inventory = new List<InventoryItem>
            {
                new InventoryItem { ItemId = "sword", Quantity = 1 }
            },
            Currencies = new Dictionary<string, int> { ["gold"] = 50 }
        };
    }
}
```

Call `PlayerSaveExample.LoadOrCreate("slot-1")` when opening a slot, then `CompleteQuest("slot-1", "quest-001")` after finishing a quest. Pass `"slot-2"` to create a second slot. The nested changes persist because the updated `PlayerSave` is passed to `Set` again. `GetSlotKeys()` lists all slot keys.

## Storage and Editor window

Archives are grouped by value type name and stored under `EditorPrefs/<hash>` in the Editor, or `Application.persistentDataPath/Temps/<hash>` in players. WebGL uses the hash as its PlayerPrefs key and can read the previous full type name key. Existing archive files remain readable.

Open `Lin > Prefs Helper > 持久化数据管理器` in the Unity Editor to browse the current project's `EditorPrefs` files as a tree. Click **读取键值** on a uniquely matched archive, then expand keys to inspect JSON fields and array elements. Select a key to delete it, or select an archive to clear all keys of its type. These actions require confirmation. Ambiguous or unknown files can only be inspected or moved to `EditorPrefs/Removed` after confirmation.

## Compatibility notes

Types with the same simple name share an archive; choose distinct type names when their keys or value shapes differ. Do not compile this package together with the original `Lin.Runtime` `PrefsHelper.cs`, since both define the same fully qualified type.

The archive format upgrades when written. Rolling back to an older package or player that does not support the envelope format can mark these archives as corrupt; back up or migrate saved data before rolling back.

A failed disk write can leave the current in-memory archive ahead of disk until it is reloaded.
