/*
┌────────────────────────────┐
│　Description: PrefsHelper 持久化数据管理器
│　Remark: 仅管理当前工程 EditorPrefs；不编辑存档值
└────────────────────────────┘
┌──────────────┐
│　ClassName: PrefsHelperEditorWindow
└──────────────┘
*/
using Lin.Runtime.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Lin.Runtime.Helper.Editor
{
    public sealed class PrefsHelperEditorWindow : EditorWindow
    {
        private const string MENU_PATH = "Lin/Prefs Helper/持久化数据管理器";

        private readonly Dictionary<string, int> nodeIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<ArchiveRecord> archives = new List<ArchiveRecord>();
        private TreeViewState treeViewState;
        private SearchField searchField;
        private PrefsTreeView treeView;
        private string archiveDirectory;
        private string searchText = string.Empty;
        private string scanError;
        private string operationMessage;
        private bool treeNeedsReload;
        private int nextNodeId = 1;

        [MenuItem(MENU_PATH)]
        private static void OpenWindow()
        {
            PrefsHelperEditorWindow window = GetWindow<PrefsHelperEditorWindow>();
            window.titleContent = new GUIContent("Prefs 数据管理器");
            window.minSize = new Vector2(760f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Prefs 数据管理器");
            treeViewState = new TreeViewState();
            searchField = new SearchField();
            treeView = new PrefsTreeView(this, treeViewState);
            RefreshArchives();
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.LabelField("当前工程 EditorPrefs", archiveDirectory, EditorStyles.miniLabel);

            if (treeView.SyncExpandedValues())
                treeView.Reload();

            EditorGUILayout.BeginHorizontal();
            Rect treeRect = GUILayoutUtility.GetRect(240f, 10000f, 180f, 10000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            treeView.OnGUI(treeRect);
            GUILayout.Space(5f);
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Min(380f, position.width * 0.42f)));
            DrawDetails();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            if (treeNeedsReload)
            {
                treeNeedsReload = false;
                treeView.Reload();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(44f)))
                RefreshArchives();

            string nextSearch = searchField.OnToolbarGUI(searchText, GUILayout.MinWidth(140f));
            if (!string.Equals(nextSearch, searchText, StringComparison.Ordinal))
            {
                searchText = nextSearch;
                treeView.searchString = searchText;
                treeView.Reload();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDetails()
        {
            NodeReference selected = treeView.GetSelectedNode();
            if (!string.IsNullOrEmpty(operationMessage))
                EditorGUILayout.HelpBox(operationMessage, MessageType.Info);
            if (selected == null)
            {
                EditorGUILayout.HelpBox("选择一个档案或键查看详情。", MessageType.Info);
                if (!string.IsNullOrEmpty(scanError))
                    EditorGUILayout.HelpBox("扫描失败：" + scanError, MessageType.Error);
                else if (archives.Count == 0)
                    EditorGUILayout.HelpBox("没有发现活跃档案。目录不存在时会显示为空。", MessageType.Info);
                return;
            }

            if (selected.Archive != null && selected.Value == null)
                DrawArchiveDetails(selected.Archive);
            else if (selected.Value != null)
                DrawValueDetails(selected.Value);
        }

        private void DrawArchiveDetails(ArchiveRecord archive)
        {
            EditorGUILayout.LabelField("档案", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("文件名", archive.FileName, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("状态", archive.StateLabel);
            EditorGUILayout.LabelField("类型候选", GetCandidateSummary(archive), EditorStyles.wordWrappedLabel);
            DrawFileDetails(archive);

            EditorGUILayout.BeginHorizontal();
            if (archive.HasMain || archive.HasBackup)
            {
                if (GUILayout.Button("在文件管理器中定位"))
                    EditorUtility.RevealInFinder(archive.HasMain ? archive.MainPath : archive.BackupPath);
            }
            if (archive.MatchedType != null && GUILayout.Button(archive.KeysLoaded ? "重新读取键值" : "读取键值"))
                ReadKeys(archive);
            EditorGUILayout.EndHorizontal();

            if (archive.MatchedType != null)
            {
                EditorGUILayout.HelpBox("读取或清空会调用 PrefsHelper；坏档隔离或备份恢复可能发生。", MessageType.Warning);
                if (GUILayout.Button("清空此类型的全部键值"))
                    ClearArchive(archive);
            }
            else if (archive.CandidateTypes.Count == 0 || archive.CandidateTypes.Count > 1)
            {
                EditorGUILayout.HelpBox("类型无法唯一匹配。不会读取或按类型删除此档。", MessageType.Warning);
                if (GUILayout.Button("将档案移入 Removed…"))
                    RemoveUnknownArchive(archive);
            }
        }

        private void DrawValueDetails(ValueNode value)
        {
            EditorGUILayout.LabelField(value.IsKeyRoot ? "键值" : "JSON 节点", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("档案类型", value.Archive.MatchedType.FullName, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("档案状态", value.Archive.StateLabel);
            EditorGUILayout.LabelField("节点路径", GetDisplayPath(value), EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("键", value.KeyName, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("JSON 类型", value.Token == null ? "尚未读取" : value.Token.Type.ToString());
            DrawFileDetails(value.Archive);
            if (value.Error != null)
            {
                EditorGUILayout.HelpBox("读取或序列化失败：" + value.Error, MessageType.Error);
                return;
            }

            if (value.Token != null)
            {
                string json;
                try
                {
                    json = value.Token.ToString(Formatting.Indented);
                }
                catch (Exception exception)
                {
                    EditorGUILayout.HelpBox("显示值失败：" + exception.Message, MessageType.Error);
                    return;
                }
                EditorGUILayout.TextArea(json, GUILayout.ExpandHeight(true));
            }

            if (value.IsKeyRoot)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("删除会影响此类型在整个工程中的同名键。", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("删除此键"))
                    DeleteKey(value);
            }
        }

        private void DrawFileDetails(ArchiveRecord archive)
        {
            if (archive.HasMain)
                DrawFileLine("主档", archive.MainPath);
            if (archive.HasBackup)
                DrawFileLine("备份", archive.BackupPath);
            for (int i = 0; i < archive.AuxiliaryPaths.Count; i++)
                DrawFileLine("附属文件", archive.AuxiliaryPaths[i]);
        }

        private static void DrawFileLine(string label, string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                string detail = info.Exists
                    ? info.Length + " 字节，修改于 " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    : "文件不存在";
                EditorGUILayout.LabelField(label, detail, EditorStyles.wordWrappedMiniLabel);
            }
            catch (Exception exception)
            {
                EditorGUILayout.LabelField(label, "无法读取文件信息：" + exception.Message, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void RefreshArchives()
        {
            archiveDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "EditorPrefs"));
            scanError = null;
            operationMessage = null;
            Dictionary<string, ArchiveRecord> previous = new Dictionary<string, ArchiveRecord>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < archives.Count; i++)
                previous[archives[i].FileName] = archives[i];

            Dictionary<string, List<Type>> candidates = BuildCandidateIndex();
            archives.Clear();
            try
            {
                archives.AddRange(ScanArchives(archiveDirectory, candidates));
            }
            catch (Exception exception)
            {
                scanError = exception.Message;
            }

            for (int i = 0; i < archives.Count; i++)
            {
                ArchiveRecord current = archives[i];
                if (!previous.TryGetValue(current.FileName, out ArchiveRecord oldRecord))
                    continue;
                if (current.MatchedType != null && oldRecord.MatchedType == current.MatchedType && oldRecord.KeysLoaded)
                {
                    current.KeysLoaded = true;
                    current.KeyNodes = oldRecord.KeyNodes;
                    current.KeyOrder = oldRecord.KeyOrder;
                    UpdateArchiveReferences(current.KeyNodes, current);
                }
            }

            if (treeView != null)
            {
                treeView.Reload();
                treeView.ClearSelectionIfMissing();
            }
            Repaint();
        }

        private void ReadKeys(ArchiveRecord archive)
        {
            try
            {
                LoadKeys(archive);
                RefreshRecordFiles(archive);
                operationMessage = "已读取键值；文件状态已重新扫描。";
            }
            catch (Exception exception)
            {
                operationMessage = "读取失败：" + Unwrap(exception).Message;
            }
            treeView.Reload();
            treeView.RefreshSelectedValue();
            Repaint();
        }

        private void DeleteKey(ValueNode value)
        {
            ArchiveRecord archive = value.Archive;
            string message = "将删除类型 " + archive.MatchedType.FullName + " 的键“" + value.KeyName + "”。";
            if (!EditorUtility.DisplayDialog("确认删除键", message, "删除", "取消"))
                return;

            try
            {
                InvokeTyped(DeleteKeyMethod, archive.MatchedType, new object[] { value.KeyName });
                LoadKeys(archive);
                RefreshRecordFiles(archive);
                operationMessage = "键已删除。";
            }
            catch (Exception exception)
            {
                operationMessage = "删除失败：" + Unwrap(exception).Message;
            }
            treeView.Reload();
            treeView.ClearSelectionIfMissing();
            Repaint();
        }

        private void ClearArchive(ArchiveRecord archive)
        {
            string message = "将清除类型 " + archive.MatchedType.FullName + " 在整个工程存档中的全部键值。";
            if (!EditorUtility.DisplayDialog("确认清空类型存档", message, "清空全部", "取消"))
                return;

            try
            {
                InvokeTyped(ClearMethod, archive.MatchedType, null);
                LoadKeys(archive);
                RefreshRecordFiles(archive);
                operationMessage = "类型存档已清空。";
            }
            catch (Exception exception)
            {
                operationMessage = "清空失败：" + Unwrap(exception).Message;
            }
            treeView.Reload();
            treeView.ClearSelectionIfMissing();
            Repaint();
        }

        private void RemoveUnknownArchive(ArchiveRecord archive)
        {
            string message = "将把此档案的主档和备份移入 EditorPrefs/Removed。该操作不解析内容。";
            if (!EditorUtility.DisplayDialog("确认移除无法匹配的档案", message, "移入 Removed", "取消"))
                return;

            List<string> movedFiles = new List<string>();
            string destinationDirectory = null;
            try
            {
                string removedDirectory = Path.Combine(archiveDirectory, "Removed");
                Directory.CreateDirectory(removedDirectory);
                destinationDirectory = CreateUniqueRemovedDirectory(removedDirectory, archive.FileName);
                if (archive.HasMain)
                {
                    File.Move(archive.MainPath, Path.Combine(destinationDirectory, Path.GetFileName(archive.MainPath)));
                    movedFiles.Add(archive.MainPath);
                }
                if (archive.HasBackup)
                {
                    File.Move(archive.BackupPath, Path.Combine(destinationDirectory, Path.GetFileName(archive.BackupPath)));
                    movedFiles.Add(archive.BackupPath);
                }

                RefreshArchives();
                operationMessage = "档案已移入：" + destinationDirectory;
            }
            catch (Exception exception)
            {
                string moved = movedFiles.Count == 0
                    ? "未移动文件。"
                    : "已移动部分文件到 " + destinationDirectory + "：" + FormatFileList(movedFiles) + "。";
                RefreshArchives();
                operationMessage = moved + "移除失败：" + exception.Message;
            }
            Repaint();
        }

        private void LoadKeys(ArchiveRecord archive)
        {
            IEnumerable<string> keys = (IEnumerable<string>)InvokeTyped(GetAllKeysMethod, archive.MatchedType, null);
            List<string> sortedKeys = new List<string>();
            foreach (string key in keys)
                sortedKeys.Add(key);
            sortedKeys.Sort(StringComparer.Ordinal);

            Dictionary<string, ValueNode> oldNodes = archive.KeyNodes;
            Dictionary<string, ValueNode> nextNodes = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                string key = sortedKeys[i];
                if (oldNodes.TryGetValue(key, out ValueNode existing))
                {
                    existing.Token = null;
                    existing.Error = null;
                    existing.TokenLoaded = false;
                    existing.ChildrenLoaded = false;
                    existing.Children = null;
                    nextNodes.Add(key, existing);
                    continue;
                }

                string path = GetArchiveNodePath(archive) + "/k" + key.Length + ":" + key;
                nextNodes.Add(key, new ValueNode(archive, key, key, path, true));
            }
            archive.KeyNodes = nextNodes;
            archive.KeyOrder = sortedKeys;
            archive.KeysLoaded = true;
        }

        private static void UpdateArchiveReferences(Dictionary<string, ValueNode> nodes, ArchiveRecord archive)
        {
            foreach (ValueNode node in nodes.Values)
                UpdateArchiveReference(node, archive);
        }

        private static void UpdateArchiveReference(ValueNode node, ArchiveRecord archive)
        {
            node.Archive = archive;
            if (node.Children == null)
                return;
            for (int i = 0; i < node.Children.Count; i++)
                UpdateArchiveReference(node.Children[i], archive);
        }

        private void RefreshRecordFiles(ArchiveRecord archive)
        {
            archive.HasMain = File.Exists(archive.MainPath);
            archive.HasBackup = File.Exists(archive.BackupPath);
            archive.AuxiliaryPaths.Clear();
            if (!Directory.Exists(archiveDirectory))
                return;

            string[] files = Directory.GetFiles(archiveDirectory, archive.FileName + ".*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (IsAuxiliaryFileName(name, archive.FileName))
                    archive.AuxiliaryPaths.Add(files[i]);
            }
            archive.AuxiliaryPaths.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void OnNodeSelected(NodeReference selected)
        {
            if (selected != null && selected.Value != null && selected.Value.IsKeyRoot)
            {
                LoadValue(selected.Value);
                treeNeedsReload = true;
            }
        }

        private void LoadValue(ValueNode value)
        {
            if (value.TokenLoaded)
                return;

            try
            {
                object rawValue = InvokeTyped(GetMethod, value.Archive.MatchedType, new object[] { value.KeyName });
                value.Token = rawValue == null ? JValue.CreateNull() : JToken.FromObject(rawValue);
            }
            catch (Exception exception)
            {
                value.Error = Unwrap(exception).Message;
            }
            value.TokenLoaded = true;
        }

        private void BuildValueChildren(ValueNode value)
        {
            if (value.ChildrenLoaded || !value.TokenLoaded)
                return;
            value.ChildrenLoaded = true;
            value.Children = new List<ValueNode>();
            if (value.Token is JObject jsonObject)
            {
                foreach (JProperty property in jsonObject.Properties())
                {
                    string path = value.Path + "/p" + property.Name.Length + ":" + property.Name;
                    value.Children.Add(new ValueNode(value.Archive, value.KeyName, property.Name, path, false)
                    {
                        Parent = value,
                        Token = property.Value,
                        TokenLoaded = true
                    });
                }
            }
            else if (value.Token is JArray jsonArray)
            {
                for (int i = 0; i < jsonArray.Count; i++)
                {
                    string label = "[" + i + "]";
                    value.Children.Add(new ValueNode(value.Archive, value.KeyName, label, value.Path + "/i" + i, false)
                    {
                        Parent = value,
                        Token = jsonArray[i],
                        TokenLoaded = true
                    });
                }
            }
        }

        private int GetNodeId(string path)
        {
            if (nodeIds.TryGetValue(path, out int id))
                return id;
            id = nextNodeId++;
            nodeIds.Add(path, id);
            return id;
        }

        private static string GetArchiveNodePath(ArchiveRecord archive)
        {
            return "a" + archive.FileName.Length + ":" + archive.FileName;
        }

        private static string GetDisplayPath(ValueNode value)
        {
            if (value.Parent == null)
                return value.Archive.FileName + "/" + value.KeyName;
            return GetDisplayPath(value.Parent) + "/" + value.Label;
        }

        private static string GetCandidateSummary(ArchiveRecord archive)
        {
            if (archive.CandidateTypes.Count == 0)
                return "未匹配";
            if (archive.CandidateTypes.Count == 1)
                return archive.MatchedType.FullName;
            string summary = "多重匹配：";
            for (int i = 0; i < archive.CandidateTypes.Count; i++)
            {
                if (i > 0)
                    summary += "；";
                summary += archive.CandidateTypes[i].FullName;
            }
            return summary;
        }

        private static Dictionary<string, List<Type>> BuildCandidateIndex()
        {
            HashSet<Type> types = new HashSet<Type>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] assemblyTypes;
                try
                {
                    assemblyTypes = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    assemblyTypes = exception.Types;
                }
                catch
                {
                    continue;
                }

                for (int j = 0; j < assemblyTypes.Length; j++)
                    AddCandidateType(types, assemblyTypes[j]);
            }

            try
            {
                foreach (Type type in PrefsHelper.GetAllArchiveTypes())
                    AddCandidateType(types, type);
            }
            catch
            {
            }

            Dictionary<string, List<Type>> candidates = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.IsPointer || type.IsByRef)
                    continue;

                string fileName;
                try
                {
                    fileName = PrefsHelper.GetArchiveFileName(type);
                }
                catch
                {
                    continue;
                }

                if (!candidates.TryGetValue(fileName, out List<Type> matches))
                {
                    matches = new List<Type>();
                    candidates.Add(fileName, matches);
                }
                matches.Add(type);
            }

            foreach (List<Type> matches in candidates.Values)
                matches.Sort(CompareTypes);
            return candidates;
        }

        private static void AddCandidateType(HashSet<Type> types, Type type)
        {
            if (type != null)
                types.Add(type);
        }

        private static int CompareTypes(Type left, Type right)
        {
            return string.Compare(left.FullName, right.FullName, StringComparison.Ordinal);
        }

        private static List<ArchiveRecord> ScanArchives(string directory, Dictionary<string, List<Type>> candidates)
        {
            List<ArchiveRecord> result = new List<ArchiveRecord>();
            if (!Directory.Exists(directory))
                return result;

            Dictionary<string, ArchiveRecord> byName = new Dictionary<string, ArchiveRecord>(StringComparer.OrdinalIgnoreCase);
            string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                string archiveName;
                bool isBackup;
                if (IsArchiveFileName(name))
                {
                    archiveName = name;
                    isBackup = false;
                }
                else if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && IsArchiveFileName(name.Substring(0, name.Length - 4)))
                {
                    archiveName = name.Substring(0, name.Length - 4);
                    isBackup = true;
                }
                else
                {
                    continue;
                }

                if (!byName.TryGetValue(archiveName, out ArchiveRecord archive))
                {
                    candidates.TryGetValue(archiveName, out List<Type> matches);
                    archive = new ArchiveRecord(archiveName, directory, matches);
                    byName.Add(archiveName, archive);
                    result.Add(archive);
                }

                if (isBackup)
                    archive.HasBackup = true;
                else
                    archive.HasMain = true;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (!TryGetAuxiliaryArchiveName(name, out string archiveName))
                    continue;
                if (byName.TryGetValue(archiveName, out ArchiveRecord archive))
                    archive.AuxiliaryPaths.Add(files[i]);
            }

            result.Sort(CompareArchives);
            return result;
        }

        private static int CompareArchives(ArchiveRecord left, ArchiveRecord right)
        {
            return string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsArchiveFileName(string name)
        {
            if (name.Length < 1 || name.Length > 16)
                return false;
            for (int i = 0; i < name.Length; i++)
            {
                char character = name[i];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F')))
                    return false;
            }
            return true;
        }

        private static bool TryGetAuxiliaryArchiveName(string name, out string archiveName)
        {
            archiveName = null;
            int separator = name.IndexOf('.');
            if (separator <= 0)
                return false;
            string prefix = name.Substring(0, separator);
            if (!IsArchiveFileName(prefix))
                return false;
            string suffix = name.Substring(separator);
            if (string.Equals(suffix, ".tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, ".bad", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, ".bak.bad", StringComparison.OrdinalIgnoreCase)
                || IsNumberedBadSuffix(suffix))
            {
                archiveName = prefix;
                return true;
            }
            return false;
        }

        private static bool IsAuxiliaryFileName(string name, string archiveName)
        {
            return name.StartsWith(archiveName + ".", StringComparison.OrdinalIgnoreCase)
                && TryGetAuxiliaryArchiveName(name, out string parsedName)
                && string.Equals(parsedName, archiveName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumberedBadSuffix(string suffix)
        {
            int digitsStart;
            if (suffix.StartsWith(".bad.", StringComparison.OrdinalIgnoreCase))
                digitsStart = 5;
            else if (suffix.StartsWith(".bak.bad.", StringComparison.OrdinalIgnoreCase))
                digitsStart = 9;
            else
                return false;
            if (suffix.Length == digitsStart)
                return false;
            for (int i = digitsStart; i < suffix.Length; i++)
            {
                if (suffix[i] < '0' || suffix[i] > '9')
                    return false;
            }
            return true;
        }

        private static string CreateUniqueRemovedDirectory(string parent, string fileName)
        {
            string baseName = fileName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string path = Path.Combine(parent, baseName);
            int suffix = 1;
            while (Directory.Exists(path) || File.Exists(path))
            {
                path = Path.Combine(parent, baseName + "_" + suffix);
                suffix++;
            }
            Directory.CreateDirectory(path);
            return path;
        }

        private static string FormatFileList(List<string> paths)
        {
            string result = string.Empty;
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0)
                    result += "，";
                result += paths[i];
            }
            return result;
        }

        private static object InvokeTyped(MethodInfo method, Type valueType, object[] arguments)
        {
            if (method == null)
                throw new MissingMethodException("PrefsHelper API 未找到。");
            try
            {
                return method.MakeGenericMethod(valueType).Invoke(null, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw Unwrap(exception);
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
                exception = invocation.InnerException;
            return exception;
        }

        private static readonly MethodInfo GetAllKeysMethod = FindGenericMethod("GetAllKeys", Type.EmptyTypes);
        private static readonly MethodInfo GetMethod = FindGenericMethod("Get", new[] { typeof(string) });
        private static readonly MethodInfo DeleteKeyMethod = FindGenericMethod("DeleteKey", new[] { typeof(string) });
        private static readonly MethodInfo ClearMethod = FindGenericMethod("Clear", Type.EmptyTypes);

        private static MethodInfo FindGenericMethod(string name, Type[] parameterTypes)
        {
            MethodInfo[] methods = typeof(PrefsHelper).GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != name || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                    continue;
                bool matches = true;
                for (int j = 0; j < parameters.Length; j++)
                {
                    if (parameters[j].ParameterType != parameterTypes[j])
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    return method;
            }
            return null;
        }

        private sealed class ArchiveRecord
        {
            public readonly string FileName;
            public readonly string MainPath;
            public readonly string BackupPath;
            public readonly List<Type> CandidateTypes;
            public readonly List<string> AuxiliaryPaths = new List<string>();
            public Dictionary<string, ValueNode> KeyNodes = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
            public List<string> KeyOrder = new List<string>();
            public bool HasMain;
            public bool HasBackup;
            public bool KeysLoaded;

            public ArchiveRecord(string fileName, string directory, List<Type> candidateTypes)
            {
                FileName = fileName;
                MainPath = Path.Combine(directory, fileName);
                BackupPath = MainPath + ".bak";
                CandidateTypes = candidateTypes ?? new List<Type>();
            }

            public Type MatchedType
            {
                get { return CandidateTypes.Count == 1 ? CandidateTypes[0] : null; }
            }

            public string StateLabel
            {
                get
                {
                    if (!HasMain)
                        return "仅备份";
                    if (CandidateTypes.Count == 0)
                        return "类型未匹配";
                    return CandidateTypes.Count == 1 ? "已匹配" : "多重匹配";
                }
            }
        }

        private sealed class ValueNode
        {
            public ArchiveRecord Archive;
            public readonly string KeyName;
            public readonly string Label;
            public readonly string Path;
            public readonly bool IsKeyRoot;
            public ValueNode Parent;
            public JToken Token;
            public string Error;
            public bool TokenLoaded;
            public bool ChildrenLoaded;
            public List<ValueNode> Children;

            public ValueNode(ArchiveRecord archive, string keyName, string label, string path, bool isKeyRoot)
            {
                Archive = archive;
                KeyName = keyName;
                Label = label;
                Path = path;
                IsKeyRoot = isKeyRoot;
            }
        }

        private sealed class NodeReference
        {
            public ArchiveRecord Archive;
            public ValueNode Value;
        }

        private sealed class PrefsTreeView : TreeView
        {
            private readonly PrefsHelperEditorWindow owner;
            private readonly Dictionary<int, NodeReference> nodeReferences = new Dictionary<int, NodeReference>();

            public PrefsTreeView(PrefsHelperEditorWindow owner, TreeViewState state) : base(state)
            {
                this.owner = owner;
                showBorder = true;
                showAlternatingRowBackgrounds = true;
                Reload();
            }

            protected override TreeViewItem BuildRoot()
            {
                nodeReferences.Clear();
                TreeViewItem root = new TreeViewItem(0, -1, "当前工程 EditorPrefs");
                root.children = new List<TreeViewItem>();
                for (int i = 0; i < owner.archives.Count; i++)
                {
                    ArchiveRecord archive = owner.archives[i];
                    string path = GetArchiveNodePath(archive);
                    string label = GetArchiveLabel(archive);
                    TreeViewItem archiveItem = new TreeViewItem(owner.GetNodeId(path), 0, label);
                    root.AddChild(archiveItem);
                    nodeReferences[archiveItem.id] = new NodeReference { Archive = archive };

                    if (!archive.KeysLoaded)
                        continue;
                    for (int keyIndex = 0; keyIndex < archive.KeyOrder.Count; keyIndex++)
                    {
                        if (archive.KeyNodes.TryGetValue(archive.KeyOrder[keyIndex], out ValueNode value))
                            archiveItem.AddChild(BuildValueItem(value, 1));
                    }
                }
                return root;
            }

            protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
            {
                if (string.IsNullOrEmpty(search))
                    return true;
                if (!nodeReferences.TryGetValue(item.id, out NodeReference reference))
                    return false;
                if (reference.Archive != null)
                    return ContainsIgnoreCase(reference.Archive.FileName, search) || ContainsCandidate(reference.Archive, search);
                if (reference.Value != null && reference.Value.IsKeyRoot)
                    return ContainsIgnoreCase(reference.Value.KeyName, search);
                return false;
            }

            protected override void SelectionChanged(IList<int> selectedIds)
            {
                if (selectedIds.Count == 0)
                    return;
                if (nodeReferences.TryGetValue(selectedIds[0], out NodeReference selected))
                    owner.OnNodeSelected(selected);
            }

            public bool SyncExpandedValues()
            {
                bool changed = false;
                IList<int> expanded = GetExpanded();
                for (int i = 0; i < expanded.Count; i++)
                {
                    if (!nodeReferences.TryGetValue(expanded[i], out NodeReference reference) || reference.Value == null)
                        continue;
                    owner.LoadValue(reference.Value);
                    if (!reference.Value.ChildrenLoaded)
                    {
                        owner.BuildValueChildren(reference.Value);
                        changed = true;
                    }
                }
                return changed;
            }

            public NodeReference GetSelectedNode()
            {
                IList<int> selected = GetSelection();
                if (selected.Count == 0)
                    return null;
                nodeReferences.TryGetValue(selected[0], out NodeReference reference);
                return reference;
            }

            public void RefreshSelectedValue()
            {
                NodeReference selected = GetSelectedNode();
                if (selected != null && selected.Value != null && selected.Value.IsKeyRoot)
                    owner.LoadValue(selected.Value);
            }

            public void ClearSelectionIfMissing()
            {
                IList<int> selected = GetSelection();
                if (selected.Count > 0 && !nodeReferences.ContainsKey(selected[0]))
                    SetSelection(new List<int>(), TreeViewSelectionOptions.None);
            }

            private TreeViewItem BuildValueItem(ValueNode value, int depth)
            {
                string label = GetValueLabel(value);
                TreeViewItem item = new TreeViewItem(owner.GetNodeId(value.Path), depth, label);
                nodeReferences[item.id] = new NodeReference { Archive = value.Archive, Value = value };

                if (value.ChildrenLoaded && value.Children != null)
                {
                    for (int i = 0; i < value.Children.Count; i++)
                        item.AddChild(BuildValueItem(value.Children[i], depth + 1));
                }
                else if (MayHaveChildren(value))
                {
                    string placeholderPath = value.Path + "/pending";
                    item.AddChild(new TreeViewItem(owner.GetNodeId(placeholderPath), depth + 1, "展开后解析 JSON"));
                }
                return item;
            }

            private static bool MayHaveChildren(ValueNode value)
            {
                if (!value.TokenLoaded)
                    return value.IsKeyRoot;
                return value.Token is JObject || value.Token is JArray;
            }

            private static string GetValueLabel(ValueNode value)
            {
                if (value.Error != null)
                    return value.Label + "（读取失败）";
                if (!value.TokenLoaded || value.Token == null)
                    return value.Label;
                if (value.Token is JObject)
                    return value.Label + "  [对象]";
                if (value.Token is JArray array)
                    return value.Label + "  [数组，" + array.Count + " 项]";
                return value.Label + ": " + value.Token.ToString(Formatting.None);
            }

            private static string GetArchiveLabel(ArchiveRecord archive)
            {
                string name = archive.MatchedType == null ? archive.FileName : archive.MatchedType.FullName;
                return name + "（" + archive.StateLabel + "）";
            }

            private static bool ContainsCandidate(ArchiveRecord archive, string search)
            {
                for (int i = 0; i < archive.CandidateTypes.Count; i++)
                {
                    if (ContainsIgnoreCase(archive.CandidateTypes[i].FullName, search))
                        return true;
                }
                return false;
            }

            private static bool ContainsIgnoreCase(string value, string search)
            {
                return value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
