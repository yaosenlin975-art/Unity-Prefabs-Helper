/*
┌────────────────────────────┐
│　Description: 信息存储辅助
│　Remark: 会将所有信息都序列化成字符串
└────────────────────────────┘
┌──────────────┐                                   
│　ClassName: PrefsHelper
└──────────────┘
*/
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Text;
using UnityEngine.Scripting;

namespace Lin.Runtime.Helper
{
    [Preserve]
    public static class PrefsHelper
    {
        // 存档格式版本：写盘一律带信封，读盘只认 v >= 本版本且带 d 字段的档，其余按旧格式（裸字典）读、下次写自动升级
        // 注意这是单向升级：带信封的档被旧版本代码读到，它会判成损坏并挪成 .bad，所以装了这个包就不要回滚 Player 版本
        private const int ARCHIVE_VERSION = 1;

        private static Dictionary<Type, object> archivesMap = new Dictionary<Type, object>();

#if !UNITY_WEBGL
        // throwOnInvalidBytes：用于识别旧版本以 Encoding.Default 写下的非 UTF-8 存档
        private static readonly UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
#endif

        // 档身份：两端必须用同一套规则，否则同一个类型在 WebGL 和真机的分档粒度不一样、彼此读不到对方的档。
        // 统一选了 Name 的哈希（沿用已有档名规则，避免给全工程存量档付一次迁移税）；
        // 代价是 Name 不含命名空间，跨命名空间同名的类型会撞进同一档——WebGL 端尤其明显，因为那边原先用的是 FullName
        public static string GetArchiveFileName(Type type)
        {
            unchecked
            {
                const long prime = 1099511628211;
                long hash = (long)14695981039346656037;
                string name = type.Name;
                for (int i = 0; i < name.Length; i++)
                    hash = (hash ^ name[i]) * prime;
                return hash.ToString("x");
            }
        }

        private static string GetArchiveDir()
        {
#if UNITY_EDITOR
            var dir = "EditorPrefs";
            Directory.CreateDirectory(dir);
            return dir;
#else
            return Application.persistentDataPath + "/Temps";
#endif
        }

        private static PrefsArchive<T> GetArchive<T>()
        {
            var type = typeof(T);
            // 全局锁：存档 I/O 只在首次访问该类型时发生，量小；真出现跨线程热点再换并发字典
            lock (archivesMap)
            {
                if (!archivesMap.TryGetValue(type, out var archive))
                {
                    var loaded = PrefsArchive<T>.Load();
                    if (!loaded.readFailed)
                        archivesMap.Add(type, loaded);
                    return loaded;
                }
                return (PrefsArchive<T>)archive;
            }
        }

        public static void DeleteKey<T>(string key)
        {
            var archive = GetArchive<T>();
            archive.RemoveAndSave(key);
        }

        public static T Get<T>(string key)
        {
            var archive = GetArchive<T>();
            return archive.Get(key);
        }

        public static T Get<T>(string key, T defaultValue)
        {
            var archive = GetArchive<T>();
            return archive.Get(key, defaultValue);
        }

        public static T Get<T>(string key, Func<T> createFunc)
        {
            var archive = GetArchive<T>();
            return archive.Get(key, createFunc);
        }

        // 引用类型拿到的是档里那个活对象：就地改了不 Set 就等于没改，域重载后会回到盘上的样子
        public static void Set<T>(string key, T value)
        {
            var archive = GetArchive<T>();
            archive.Set(key, value);
        }

        /// <summary>
        /// 获取已成功加载并缓存的存档类型（不触发任何 I/O）
        /// </summary>
        public static IEnumerable<Type> GetAllArchiveTypes()
        {
            lock (archivesMap)
                return new List<Type>(archivesMap.Keys);
        }

        /// <summary>
        /// 获取指定类型下的所有 key。注意作用域是"整个值类型"而不是本模块：
        /// 档按值的类型分堆，全工程同一个 T 的所有 key 都在同一档里
        /// </summary>
        public static IEnumerable<string> GetAllKeys<T>()
        {
            return GetArchive<T>().SnapshotKeys();
        }

        /// <summary>
        /// 判断指定 key 是否存在
        /// </summary>
        public static bool ContainsKey<T>(string key)
        {
            return GetArchive<T>().ContainsKeyLocked(key);
        }

        /// <summary>
        /// 清除指定类型下的所有 key。作用域是"整个值类型"而不是本模块，
        /// 清标量类型（如 float）会把全工程写到这一档里的 key 一起清掉
        /// </summary>
        public static void Clear<T>()
        {
            GetArchive<T>().ClearAndSave();
        }

        [Serializable]
        class PrefsArchive<T> : Dictionary<string, T>
        {
            private string filePath;
            private object locker;
            internal bool readFailed;
            private const byte OFFSET = 7;

            class PrefsEnvelope
            {
                public int v;
                public Dictionary<string, T> d;
            }

            public static PrefsArchive<T> Load()
            {
                string filePath = GetPath();
                PrefsArchive<T> result;
#if UNITY_WEBGL
                try
                {
                    string json = ReadWebGlJson();
                    result = string.IsNullOrEmpty(json) ? new PrefsArchive<T>() : FromJson(json);
                    if (result is null)
                        throw new JsonSerializationException("存档根节点不是有效对象。");
                }
                catch (Exception ex)
                {
                    // WebGL 没有文件隔离能力；失败档不进缓存，也不允许 Set 覆盖
                    Debug.LogError(string.Format("[PrefsHelper] {0} WebGL 存档读取失败，本次按空档处理：{1}", typeof(T).FullName, ex.Message));
                    result = new PrefsArchive<T> { readFailed = true };
                }
#else
                if (File.Exists(filePath))
                {
                    result = ReadFile(filePath, filePath, out bool corrupt);
                    if (corrupt)
                        result = ReadBackup(filePath);
                }
                else
                {
                    result = ReadBackup(filePath);
                }
#endif
                result.filePath = filePath;
                result.locker = new object();
                 
                return result;
            }

#if UNITY_WEBGL
            // 档身份从 FullName 换成 Name 哈希：老 key 再读一次，别让 WebGL 上已有的档变成孤儿
            private static string ReadWebGlJson()
            {
                var json = PlayerPrefs.GetString(GetArchiveFileName(typeof(T)));
                return string.IsNullOrEmpty(json) ? PlayerPrefs.GetString(typeof(T).FullName) : json;
            }
#endif

#if !UNITY_WEBGL
            // 主档缺失或损坏时尝试读上一版；.bak 内容仍按主档路径混淆
            private static PrefsArchive<T> ReadBackup(string filePath)
            {
                string bakPath = string.Concat(filePath, ".bak");
                if (!File.Exists(bakPath))
                    return new PrefsArchive<T>();

                var result = ReadFile(bakPath, filePath, out bool corrupt);
                if (!corrupt && !result.readFailed)
                    Debug.LogWarning(string.Format("[PrefsHelper] {0} 主档缺失或损坏，已从 {1} 恢复上一版。", typeof(T).FullName, bakPath));
                return result;
            }
#endif

            // 新格式是 {"v":1,"d":{...}}；旧档是裸字典，解信封只会得到 v=0、d=null，
            // 于是这里退回按裸字典读，下次写盘自然升级成新格式。
            // 裸字典里若恰好有名为 v / d 的 key 且形状不匹配，解信封会直接抛；必须在这里继续尝试旧格式
            private static PrefsArchive<T> FromJson(string json)
            {
                PrefsEnvelope envelope = null;
                try
                {
                    envelope = JsonConvert.DeserializeObject<PrefsEnvelope>(json);
                }
                catch (JsonException)
                {
                    envelope = null;
                }

                if (envelope is not null && envelope.v >= ARCHIVE_VERSION && envelope.d is not null)
                {
                    var archive = new PrefsArchive<T>();
                    foreach (var pair in envelope.d)
                        archive[pair.Key] = pair.Value;
                    return archive;
                }

                return JsonConvert.DeserializeObject<PrefsArchive<T>>(json);
            }

#if !UNITY_WEBGL
            private static PrefsArchive<T> ReadFile(string readPath, string archivePath, out bool corrupt)
            {
                corrupt = false;
                string json = null;
                try
                {
                    var bytes = File.ReadAllBytes(readPath);
                    if (bytes.Length == 0)
                    {
                        if (!QuarantineFile(readPath, "文件为空"))
                            return new PrefsArchive<T> { readFailed = true };
                        corrupt = true;
                        return new PrefsArchive<T>();
                    }

                    Translate(bytes, archivePath);
                    json = Decode(bytes);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        if (!QuarantineFile(readPath, "文件内容为空"))
                            return new PrefsArchive<T> { readFailed = true };
                        corrupt = true;
                        return new PrefsArchive<T>();
                    }

                    var archive = FromJson(json);
                    if (archive is null)
                    {
                        if (!QuarantineFile(readPath, "存档根节点不是对象"))
                            return new PrefsArchive<T> { readFailed = true };
                        corrupt = true;
                        return new PrefsArchive<T>();
                    }
                    return archive;
                }
                catch (JsonReaderException ex)
                {
                    // Json.NET 也会用 JsonReaderException 表示类型转换错误；只在失败时单独校验 JSON 语法
                    bool validJson = false;
                    try
                    {
                        JToken.Parse(json);
                        validJson = true;
                    }
                    catch (JsonException)
                    {
                    }

                    if (validJson)
                    {
                        Debug.LogError(string.Format("[PrefsHelper] {0} 读取失败，本次按空档处理（原文件保留）：{1}",
                            typeof(T).FullName, ex.Message));
                        return new PrefsArchive<T> { readFailed = true };
                    }

                    if (!QuarantineFile(readPath, ex.Message))
                        return new PrefsArchive<T> { readFailed = true };
                    corrupt = true;
                    return new PrefsArchive<T>();
                }
                catch (Exception ex)
                {
                    // 类型转换或 I/O 暂时失败时保留原档，失败结果不缓存且不允许写入
                    Debug.LogError(string.Format("[PrefsHelper] {0} 读取失败，本次按空档处理（原文件保留）：{1}",
                        typeof(T).FullName, ex.Message));
                    return new PrefsArchive<T> { readFailed = true };
                }
            }

            private static bool QuarantineFile(string filePath, string reason)
            {
                string badPath = string.Concat(filePath, ".bad");
                bool quarantined = false;
                int suffix = 1;
                while (File.Exists(badPath))
                {
                    badPath = string.Concat(filePath, ".bad.", suffix.ToString());
                    suffix++;
                }

                try
                {
                    File.Move(filePath, badPath);
                    quarantined = true;
                }
                catch (Exception moveEx)
                {
                    badPath = string.Concat(badPath, "（挪动失败: ", moveEx.Message, "）");
                }
                Debug.LogError(string.Format("[PrefsHelper] {0} 存档损坏，已按空档处理，原文件在 {1}。原因：{2}",
                    typeof(T).FullName, badPath, reason));
                return quarantined;
            }

            // 单字节 XOR 混淆：只让档文件不可直读，不是加密（key 由类型全名长度与路径长度推出，JSON 恒以 {" 开头，
            // 两个已知明文字节就能把它恢复出来）。客户端本地存档本来也不构成安全边界，别把它当防护
            private static void Translate(byte[] bytes, string path)
            {
                Type type = typeof(T);
                byte flags = (byte)(type.FullName.Length % byte.MaxValue);
                byte offset = (byte)((flags + path.Length) % byte.MaxValue);
                offset = offset == 0 ? OFFSET : offset;
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] ^= offset;
            }

            // 就地覆盖会在进程被杀/断电时留下撕裂档，下次读直接解不出来、整档进 .bad；
            // 官方范式是"临时文件 → 确认落盘 → 原子替换"，File.Replace 一次给齐替换和上一版备份
            private static void WriteAtomically(string filePath, byte[] bytes)
            {
                string tmpPath = string.Concat(filePath, ".tmp");
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);           // 无参 Flush 只送到 OS 缓存，等于没落盘
                }

                if (File.Exists(filePath))
                    File.Replace(tmpPath, filePath, string.Concat(filePath, ".bak"));
                else
                    File.Move(tmpPath, filePath);   // 首次写没有可替换的目标
            }

            // 旧版本按 Encoding.Default（中文 Windows 下是 GBK）写盘，严格 UTF-8 解码失败时回退一次
            private static string Decode(byte[] bytes)
            {
                try
                {
                    return strictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    Debug.LogWarning(string.Format("[PrefsHelper] {0} 命中旧编码存档，按 Encoding.Default 读取。", typeof(T).FullName));
                    return Encoding.Default.GetString(bytes);
                }
            }
#endif
            private static void Save(PrefsArchive<T> archive)
            {
                var json = JsonConvert.SerializeObject(new PrefsEnvelope { v = ARCHIVE_VERSION, d = archive });
#if UNITY_WEBGL
                PlayerPrefs.SetString(GetArchiveFileName(typeof(T)), json);
                // 官方只承诺 OnApplicationQuit 时自动写盘，而 Web 平台明确不支持那个回调：
                // 不显式 Sync 就等于把落盘时机交给浏览器
                PlayerPrefs.Save();
#else
                var bytes = strictUtf8.GetBytes(json);
                string dir = Path.GetDirectoryName(archive.filePath);
                Directory.CreateDirectory(dir);
                Translate(bytes, archive.filePath);
                WriteAtomically(archive.filePath, bytes);
#endif
            }

            private static string GetPath()
            {
                return Path.Combine(GetArchiveDir(), GetArchiveFileName(typeof(T)));
            }

            public void Set(string key, T value)
            {
                lock (locker)
                {
                    if (readFailed)
                        throw new InvalidOperationException("存档读取失败，已跳过写入。");
                    this[key] = value;
                    Save(this);
                }
            }

            // 基类 Dictionary.Clear 只改内存不落盘, 清空后下次域重载会被 Load 回来
            public void ClearAndSave()
            {
                lock (locker)
                {
                    if (readFailed)
                        throw new InvalidOperationException("存档读取失败，已跳过写入。");
                    Clear();
                    Save(this);
                }
            }

            public IEnumerable<string> SnapshotKeys()
            {
                lock (locker)
                    return new List<string>(Keys);
            }

            public bool ContainsKeyLocked(string key)
            {
                lock (locker)
                    return ContainsKey(key);
            }

            // 基类 Dictionary.Remove 只改内存不落盘, 删除后下次域重载会被 Load 回来
            // (历史上导致 Generator Step7 清完 Info, 下一次编译又重新跑整个生成流程)
            public void RemoveAndSave(string key)
            {
                lock (locker)
                {
                    if (readFailed)
                        throw new InvalidOperationException("存档读取失败，已跳过写入。");
                    if (Remove(key))
                        Save(this);
                }
            }

            public T Get(string key, T defaultValue = default)
            {
                lock (locker)
                {
                    if (TryGetValue(key, out T result))
                        return result;

                    return defaultValue;
                }
            }

            public T Get(string key, Func<T> createFunc)
            {
                lock (locker)
                {
                    if (TryGetValue(key, out T result))
                        return result;
                }
                return createFunc();
            }
        }
    }
}

