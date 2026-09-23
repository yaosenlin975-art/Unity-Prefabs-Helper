/*
┌────────────────────────────┐
│　Description: PrefsHelper 编辑器档案扫描测试
│　Remark: 验证空目录、短哈希备份与损坏附属文件过滤
└────────────────────────────┘
┌──────────────┐
│　ClassName: PrefsHelperEditorScanTests
└──────────────┘
*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Lin.Runtime.Helper.Editor;
using NUnit.Framework;

namespace Lin.Runtime.Helper.Tests
{
    public class PrefsHelperEditorScanTests
    {
        [Test]
        public void ScanFindsShortHashBackupAndIgnoresBadOnlyFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "PrefsHelperScan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                MethodInfo scan = typeof(PrefsHelperEditorWindow).GetMethod("ScanArchives", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(scan);
                Dictionary<string, List<Type>> candidates = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

                IList empty = (IList)scan.Invoke(null, new object[] { directory, candidates });
                Assert.AreEqual(0, empty.Count);

                File.WriteAllText(Path.Combine(directory, "a.bak"), "backup");
                File.WriteAllText(Path.Combine(directory, "b.bad"), "quarantined");
                IList archives = (IList)scan.Invoke(null, new object[] { directory, candidates });

                Assert.AreEqual(1, archives.Count);
                object archive = archives[0];
                Assert.AreEqual("a", archive.GetType().GetField("FileName").GetValue(archive));
                Assert.IsFalse((bool)archive.GetType().GetField("HasMain").GetValue(archive));
                Assert.IsTrue((bool)archive.GetType().GetField("HasBackup").GetValue(archive));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
