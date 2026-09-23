/*
┌────────────────────────────┐
│　Description: PrefsHelper 包兼容性测试
│　Remark: 只使用独立测试类型，验证档名与实际落盘读取
└────────────────────────────┘
┌──────────────┐
│　ClassName: PrefsHelperTests
└──────────────┘
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Lin.Runtime.Helper;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lin.Runtime.Helper.Tests
{
    public class PrefsHelperTests
    {
        [Serializable]
        private class PrefsHelperRoundTripValue
        {
            public int Value;
        }

        [Serializable]
        private class PrefsHelperCorruptMainValue
        {
            public int Value;
        }

        [Serializable]
        private class PrefsHelperEmptyMainValue
        {
            public int Value;
        }

        [Serializable]
        private class PrefsHelperTransientReadValue
        {
            public int Value;
        }

        [Serializable]
        private class PrefsHelperLegacyFixtureValue
        {
            public int Value;
        }

        [Test]
        public void HashAndDiskRoundTripPreserveArchiveContract()
        {
            Assert.AreEqual("d677a67e495cb5d9", PrefsHelper.GetArchiveFileName(typeof(PrefsHelperRoundTripValue)));

            string path = GetArchivePath<PrefsHelperRoundTripValue>();
            ClearArchiveCache<PrefsHelperRoundTripValue>();
            DeleteArchiveFiles<PrefsHelperRoundTripValue>();

            try
            {
                PrefsHelper.Set("round-trip", new PrefsHelperRoundTripValue { Value = 1 });
                PrefsHelper.Set("round-trip", new PrefsHelperRoundTripValue { Value = 2 });
                Assert.IsTrue(File.Exists(path + ".bak"));
                Assert.IsFalse(File.Exists(path + ".tmp"));

                ClearArchiveCache<PrefsHelperRoundTripValue>();
                Assert.AreEqual(2, PrefsHelper.Get<PrefsHelperRoundTripValue>("round-trip").Value);
            }
            finally
            {
                ClearArchiveCache<PrefsHelperRoundTripValue>();
                DeleteArchiveFiles<PrefsHelperRoundTripValue>();
            }
        }

        [Test]
        public void HistoricalBareDictionaryArchiveRemainsReadable()
        {
            string path = GetArchivePath<PrefsHelperLegacyFixtureValue>();
            ClearArchiveCache<PrefsHelperLegacyFixtureValue>();
            DeleteArchiveFiles<PrefsHelperLegacyFixtureValue>();

            try
            {
                Assert.AreEqual("2a2e2a7cf6855fbc", PrefsHelper.GetArchiveFileName(typeof(PrefsHelperLegacyFixtureValue)));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, new byte[]
                {
                    0x18, 0x41, 0x0f, 0x06, 0x04, 0x02, 0x00, 0x1a, 0x41, 0x59, 0x18, 0x41,
                    0x35, 0x02, 0x0f, 0x16, 0x06, 0x41, 0x59, 0x54, 0x50, 0x1e, 0x1e
                });

                Assert.AreEqual(73, PrefsHelper.Get<PrefsHelperLegacyFixtureValue>("legacy").Value);
            }
            finally
            {
                ClearArchiveCache<PrefsHelperLegacyFixtureValue>();
                DeleteArchiveFiles<PrefsHelperLegacyFixtureValue>();
            }
        }

        [Test]
        public void CorruptMainIsQuarantinedAndValidBackupIsLoaded()
        {
            const string key = "corrupt-main-recovery";
            string path = GetArchivePath<PrefsHelperCorruptMainValue>();
            ClearArchiveCache<PrefsHelperCorruptMainValue>();
            DeleteArchiveFiles<PrefsHelperCorruptMainValue>();

            try
            {
                PrefsHelper.Set(key, new PrefsHelperCorruptMainValue { Value = 1 });
                PrefsHelper.Set(key, new PrefsHelperCorruptMainValue { Value = 2 });
                ClearArchiveCache<PrefsHelperCorruptMainValue>();
                File.WriteAllText(path + ".bad", "keep-existing-quarantine");
                WriteEncodedArchive<PrefsHelperCorruptMainValue>(path, "{bad");

                LogAssert.Expect(LogType.Error, new Regex("存档损坏"));
                LogAssert.Expect(LogType.Warning, new Regex("恢复上一版"));
                Assert.AreEqual(1, PrefsHelper.Get<PrefsHelperCorruptMainValue>(key).Value);
                Assert.AreEqual("keep-existing-quarantine", File.ReadAllText(path + ".bad"));
                Assert.IsTrue(File.Exists(path + ".bad.1"));
            }
            finally
            {
                ClearArchiveCache<PrefsHelperCorruptMainValue>();
                DeleteArchiveFiles<PrefsHelperCorruptMainValue>();
            }
        }

        [Test]
        public void EmptyMainIsQuarantinedAndValidBackupIsLoaded()
        {
            const string key = "empty-main-recovery";
            string path = GetArchivePath<PrefsHelperEmptyMainValue>();
            ClearArchiveCache<PrefsHelperEmptyMainValue>();
            DeleteArchiveFiles<PrefsHelperEmptyMainValue>();

            try
            {
                PrefsHelper.Set(key, new PrefsHelperEmptyMainValue { Value = 1 });
                PrefsHelper.Set(key, new PrefsHelperEmptyMainValue { Value = 2 });
                ClearArchiveCache<PrefsHelperEmptyMainValue>();
                File.WriteAllBytes(path, new byte[0]);

                LogAssert.Expect(LogType.Error, new Regex("存档损坏"));
                LogAssert.Expect(LogType.Warning, new Regex("恢复上一版"));
                Assert.AreEqual(1, PrefsHelper.Get<PrefsHelperEmptyMainValue>(key).Value);
                Assert.IsTrue(File.Exists(path + ".bad"));
            }
            finally
            {
                ClearArchiveCache<PrefsHelperEmptyMainValue>();
                DeleteArchiveFiles<PrefsHelperEmptyMainValue>();
            }
        }

        [Test]
        public void TransientDeserializationFailurePreservesArchiveAndSetDoesNotOverwrite()
        {
            string path = GetArchivePath<PrefsHelperTransientReadValue>();
            ClearArchiveCache<PrefsHelperTransientReadValue>();
            DeleteArchiveFiles<PrefsHelperTransientReadValue>();

            try
            {
                WriteEncodedArchive<PrefsHelperTransientReadValue>(path, "{\"broken\":{\"Value\":\"not-an-int\"}}");
                byte[] originalBytes = File.ReadAllBytes(path);

                LogAssert.Expect(LogType.Error, new Regex("读取失败"));
                Assert.IsNull(PrefsHelper.Get<PrefsHelperTransientReadValue>("broken"));
                LogAssert.Expect(LogType.Error, new Regex("读取失败"));
                Assert.Throws<InvalidOperationException>(SetTransientReplacement);
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));

                WriteEncodedArchive<PrefsHelperTransientReadValue>(path, "{\"original\":{\"Value\":17}}");
                Assert.AreEqual(17, PrefsHelper.Get<PrefsHelperTransientReadValue>("original").Value);
            }
            finally
            {
                ClearArchiveCache<PrefsHelperTransientReadValue>();
                DeleteArchiveFiles<PrefsHelperTransientReadValue>();
            }
        }

        private static string GetArchivePath<T>()
        {
            return Path.Combine("EditorPrefs", PrefsHelper.GetArchiveFileName(typeof(T)));
        }

        private static void DeleteArchiveFiles<T>()
        {
            string path = GetArchivePath<T>();
            DeleteIfExists(path);
            DeleteIfExists(path + ".bak");
            DeleteIfExists(path + ".tmp");
            DeleteIfExists(path + ".bad");
            DeleteIfExists(path + ".bad.1");
            DeleteIfExists(path + ".bak.bad");
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        private static void WriteEncodedArchive<T>(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            Type archiveType = typeof(PrefsHelper).GetNestedType("PrefsArchive`1", BindingFlags.NonPublic).MakeGenericType(typeof(T));
            MethodInfo translate = archiveType.GetMethod("Translate", BindingFlags.NonPublic | BindingFlags.Static);
            translate.Invoke(null, new object[] { bytes, path });
            File.WriteAllBytes(path, bytes);
        }

        private static void SetTransientReplacement()
        {
            PrefsHelper.Set("replacement", new PrefsHelperTransientReadValue { Value = 99 });
        }

        private static void ClearArchiveCache<T>()
        {
            FieldInfo field = typeof(PrefsHelper).GetField("archivesMap", BindingFlags.NonPublic | BindingFlags.Static);
            var archivesMap = (Dictionary<Type, object>)field.GetValue(null);
            lock (archivesMap)
                archivesMap.Remove(typeof(T));
        }
    }
}
