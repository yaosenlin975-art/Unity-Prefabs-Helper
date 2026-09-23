[简体中文](README.md) | [English](README_EN.md)

# Lin Runtime Prefs Helper

这是一个独立的 Unity 持久化包，用于保存设置和结构化存档。它按 C# 类型分档，通过 `PrefsHelper` 读写数据，并提供树状编辑器窗口管理当前工程的存档；无需引入 Lin 框架的其他模块。

可将 `com.lin.runtime-prefs-helper` 作为内嵌或本地 UPM 包安装。本工程已将其放在 `Packages/` 下。使用时在脚本中引入 `Lin.Runtime.Helper` 命名空间。包依赖 `com.unity.nuget.newtonsoft-json`。

## 快速上手

### 主要接口

| 接口 | 作用 |
| --- | --- |
| `Set<T>(key, value)` | 新增或覆盖值，并立即保存。 |
| `Get<T>(key)` | 读取值；key 不存在时返回 `default(T)`。 |
| `Get<T>(key, defaultValue)` / `Get<T>(key, createFunc)` | key 不存在时返回备用值；备用值**不会自动保存**，需要时应再调用 `Set`。 |
| `ContainsKey<T>(key)` | 判断 key 是否存在。 |
| `GetAllKeys<T>()` | 返回类型 `T` 下的 key 快照。 |
| `DeleteKey<T>(key)` | 删除单个 key 并保存。 |
| `Clear<T>()` | 删除**整个工程中该类型的所有 key**并保存。 |

值类型 `T` 决定存档，key 指向其中一条数据。读取引用类型后，修改对象或其嵌套集合都需要再次调用 `Set` 才能持久化。`GetAllArchiveTypes()` 只列出当前进程中已成功加载的类型，不扫描磁盘；`GetArchiveFileName(typeof(T))` 返回该类型的存档文件名。

### 简单案例：保存一个设置

例如，把音量保存在 `float` 类型的存档中。以下代码可放进项目的 `VolumePrefsExample.cs`：

```csharp
using Lin.Runtime.Helper;

public static class VolumePrefsExample
{
    private const string KEY = "music-volume";

    public static void Save(float volume)
    {
        PrefsHelper.Set(KEY, volume);
    }

    public static float Load()
    {
        return PrefsHelper.Get(KEY, 1f);
    }

    public static void Delete()
    {
        PrefsHelper.DeleteKey<float>(KEY);
    }
}
```

游戏启动时调用 `Load()`，音量变化时调用 `Save(volume)`。第一次读取时返回默认值 `1f`；该默认值不会自动保存。

`PrefsHelper` 也支持可由 Newtonsoft.Json 序列化的复杂结构，例如嵌套对象、列表和字典。下面用多槽位玩家存档展示这些类型如何一起读写。

### 复杂案例：嵌套存档数据

以下示例管理多个存档槽。每个 `PlayerSave` 包含嵌套的进度对象、背包列表和货币字典。可将代码保存为项目脚本目录下的 `PlayerSaveExample.cs`。

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
            PrefsHelper.Set(slotKey, save); // 创建回调返回的值不会自动保存。
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
        PrefsHelper.Set(slotKey, save); // 嵌套对象和集合修改后也要重新保存。
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

打开存档槽时调用 `PlayerSaveExample.LoadOrCreate("slot-1")`，完成任务后调用 `CompleteQuest("slot-1", "quest-001")`。传入 `"slot-2"` 可创建第二个槽位。嵌套数据修改后再次传给 `Set`，才能持久化。`GetSlotKeys()` 可列出所有槽位的 key。

## 存储位置与编辑器窗口

存档按值类型名称分组。编辑器中的文件位于工程根目录的 `EditorPrefs/<hash>`，普通 Player 位于 `Application.persistentDataPath/Temps/<hash>`。WebGL 使用该哈希作为 PlayerPrefs 的键，也能读取旧版以类型完整名称为键的数据。现有存档文件仍可读取。

在 Unity 编辑器中打开 `Lin > Prefs Helper > 持久化数据管理器`，可按树形结构浏览当前工程的 `EditorPrefs` 文件。对于唯一匹配类型的档案，点击“读取键值”，再展开 key 查看 JSON 字段和数组元素；选中 key 可删除它，选中档案可清空该类型的全部 key，操作前均需确认。未知或匹配多个类型的文件只能查看信息，或在确认后移入 `EditorPrefs/Removed`。

## 兼容性说明

简单名称相同的类型共用一个存档；如果这些类型的键或值结构不同，应使用不同的类型名称。不要在同一工程中同时编译本包与原 `Lin.Runtime` 中的 `PrefsHelper.cs`，两者定义了相同的完整类型名。

写入时存档格式会升级。回退到不支持信封格式的旧包或旧版 Player，可能将这些存档判为损坏；回退前请备份或迁移数据。

写盘失败时，当前内存中的存档可能暂时比磁盘上的新，直到重新加载。
