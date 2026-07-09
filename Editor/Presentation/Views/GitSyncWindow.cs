using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Headwind.GitSync.Data;
using Headwind.GitSync.Data.Models;
using Headwind.GitSync.Presentation.ViewModels;

namespace Headwind.GitSync.Presentation.Views
{
    /// <summary>
    /// Main editor window of GitSync.
    /// Open via  Window → GitSync → GitSync Window.
    /// </summary>
    public class GitSyncWindow : EditorWindow
    {
        // ── Singleton accessor ────────────────────────────────────────────────

        [MenuItem("Window/GitSync/GitSync Window")]
        public static void Open()
        {
            var window = GetWindow<GitSyncWindow>("GitSync");
            window.minSize = new Vector2(420, 540);
            window.Show();
        }

        // ── State ─────────────────────────────────────────────────────────────

        private GitSyncViewModel _vm;
        private Vector2 _fileListScroll;
        private bool _remoteExpanded = false; // 접기/펼치기 상태

        // ── File tree state ───────────────────────────────────────────────────

        /// <summary>Directory tree built from <see cref="GitSyncViewModel.Files"/>.</summary>
        private TreeNode _rootNode;

        /// <summary>Files 리스트 참조가 바뀌었는지 감지하기 위한 캐시 키.</summary>
        private object _lastFilesRef;

        /// <summary>폴더 경로별 확장 상태 (없으면 확장으로 간주).</summary>
        private readonly Dictionary<string, bool> _folderExpanded =
            new Dictionary<string, bool>();

        private const int IndentPixels = 14;

        // ── GUI styles (lazy-init) ────────────────────────────────────────────

        private GUIStyle _headerStyle;
        private GUIStyle _statusOkStyle;
        private GUIStyle _statusErrStyle;
        private GUIStyle _sectionLabelStyle;
        private GUIStyle _remoteWarningStyle;
        private bool _stylesInitialized;

        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color ColorAdded       = new Color(0.3f, 0.8f, 0.3f);
        private static readonly Color ColorModified    = new Color(0.9f, 0.8f, 0.2f);
        private static readonly Color ColorDeleted     = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color ColorRenamed     = new Color(0.5f, 0.7f, 1.0f);
        private static readonly Color ColorUntracked   = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color ColorLockedMe    = new Color(0.3f, 0.8f, 0.3f);
        private static readonly Color ColorLockedOther = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color ColorWarningBg   = new Color(0.8f, 0.5f, 0.1f, 0.15f);

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            var repoRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            _vm = new GitSyncViewModel(new GitRepository(repoRoot));
            _vm.OnStateChanged += Repaint;
            _vm.RefreshAsync();
        }

        private void OnDisable()
        {
            if (_vm != null)
                _vm.OnStateChanged -= Repaint;
        }

        // ── Drawing ───────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            // 비동기 작업 중 모든 컨트롤 입력 차단
            var prevEnabled = GUI.enabled;
            if (_vm.IsBusy) GUI.enabled = false;

            DrawHeader();
            DrawRemoteSection();
            DrawFileList();
            DrawActionArea();
            DrawStatusBar();

            GUI.enabled = prevEnabled;

            // 로딩 오버레이는 맨 마지막에 그려 모든 UI 위에 표시
            if (_vm.IsBusy)
                DrawLoadingOverlay();
        }

        private void DrawLoadingOverlay()
        {
            var overlayRect = new Rect(0, 0, position.width, position.height);

            // 반투명 배경
            EditorGUI.DrawRect(overlayRect, new Color(0f, 0f, 0f, 0.4f));

            // 애니메이션 점 (0~3개 반복)
            int dotCount = (int)(EditorApplication.timeSinceStartup * 2.5) % 4;
            var baseMsg  = _vm.StatusMessage.TrimEnd('.', '…', ' ');
            var animated = baseMsg + new string('.', dotCount);

            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = true,
                fontSize  = 13,
                normal    = { textColor = Color.white },
            };
            GUI.Label(overlayRect, animated, labelStyle);

            // 애니메이션 유지를 위해 계속 Repaint
            Repaint();
        }

        private void EnsureStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 13,
                alignment = TextAnchor.MiddleLeft,
            };

            _sectionLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
            };

            _statusOkStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal   = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.6f, 1f, 0.6f)
                    : new Color(0.0f, 0.4f, 0.0f) },
                wordWrap = true,
            };

            _statusErrStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal   = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 0.5f, 0.5f)
                    : new Color(0.6f, 0.0f, 0.0f) },
                wordWrap = true,
            };

            _remoteWarningStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal   = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 0.75f, 0.3f)
                    : new Color(0.55f, 0.3f, 0.0f) },
                wordWrap = true,
                fontStyle = FontStyle.Bold,
            };
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("GitSync", _headerStyle, GUILayout.ExpandWidth(false));
            GUILayout.Space(6);

            var branchContent = new GUIContent($"  {_vm.CurrentBranch}  ",
                EditorGUIUtility.IconContent("d_UnityEditor.VersionControl").image);
            GUILayout.Label(branchContent, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_vm.IsBusy))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    _vm.RefreshAsync();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Remote section ────────────────────────────────────────────────────

        private void DrawRemoteSection()
        {
            // Remote 미설정 시 자동으로 펼쳐 경고 표시
            if (!_vm.HasRemote)
                _remoteExpanded = true;

            // 섹션 헤더 (클릭으로 접기/펼치기)
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var foldLabel = _remoteExpanded ? "▾  Remote" : "▸  Remote";
            var labelColor = !_vm.HasRemote ? new Color(1f, 0.75f, 0.3f) : GUI.contentColor;
            var prevColor = GUI.contentColor;
            GUI.contentColor = labelColor;
            if (GUILayout.Button(foldLabel, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                _remoteExpanded = !_remoteExpanded;
            GUI.contentColor = prevColor;

            GUILayout.FlexibleSpace();

            // 현재 URL을 접힌 상태에서도 한 줄로 표시
            if (!_remoteExpanded)
            {
                var displayUrl = _vm.HasRemote ? _vm.RemoteUrl : "설정되지 않음";
                GUILayout.Label(displayUrl, EditorStyles.miniLabel);
                GUILayout.Space(4);
            }

            EditorGUILayout.EndHorizontal();

            if (!_remoteExpanded) return;

            // ── 펼쳐진 패널 ──────────────────────────────────────────────────
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // 미설정 경고
                if (!_vm.HasRemote)
                {
                    EditorGUILayout.LabelField(
                        "⚠  Remote 저장소가 설정되지 않았습니다.\n" +
                        "Sync 및 LFS Lock/Unlock 기능을 사용하려면 URL을 입력하세요.",
                        _remoteWarningStyle);
                    EditorGUILayout.Space(2);
                }

                // URL 입력 행
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("URL", GUILayout.Width(30));
                _vm.PendingRemoteUrl = EditorGUILayout.TextField(_vm.PendingRemoteUrl,
                    GUILayout.ExpandWidth(true));

                using (new EditorGUI.DisabledScope(
                    _vm.IsBusy || string.IsNullOrWhiteSpace(_vm.PendingRemoteUrl)))
                {
                    if (GUILayout.Button("저장", GUILayout.Width(44)))
                        _vm.SetRemoteUrlAsync();
                }
                EditorGUILayout.EndHorizontal();

                // 현재 적용된 URL 표시
                if (_vm.HasRemote)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("현재", EditorStyles.miniLabel, GUILayout.Width(30));
                    EditorGUILayout.SelectableLabel(_vm.RemoteUrl, EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        // ── File list ─────────────────────────────────────────────────────────

        private void DrawFileList()
        {
            EditorGUILayout.Space(4);

            // 섹션 헤더 + Expand/Collapse all 버튼
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Changes & Locks", _sectionLabelStyle);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_vm.Files == null || _vm.Files.Count == 0))
            {
                if (GUILayout.Button("Expand all", EditorStyles.miniButton, GUILayout.Width(78)))
                    SetAllFoldersExpanded(true);
                if (GUILayout.Button("Collapse all", EditorStyles.miniButton, GUILayout.Width(88)))
                    SetAllFoldersExpanded(false);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);

            // Files 리스트 참조가 바뀌면 트리 재구성
            if (!ReferenceEquals(_lastFilesRef, _vm.Files))
            {
                _lastFilesRef = _vm.Files;
                _rootNode = BuildTree(_vm.Files);
            }

            _fileListScroll = EditorGUILayout.BeginScrollView(
                _fileListScroll, GUILayout.ExpandHeight(true));

            if (_vm.Files == null || _vm.Files.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _vm.IsBusy ? "Loading…" : "No changes detected.",
                    MessageType.None);
            }
            else if (_rootNode != null)
            {
                foreach (var child in EnumerateOrderedChildren(_rootNode))
                    DrawTreeNode(child, 0);
            }

            EditorGUILayout.EndScrollView();
        }

        // ── Tree rendering ────────────────────────────────────────────────────

        /// <summary>동일 depth 안에서 폴더가 먼저, 그 다음 파일 순으로 정렬.</summary>
        private static IEnumerable<TreeNode> EnumerateOrderedChildren(TreeNode node)
        {
            foreach (var c in node.Children.Values.Where(c => c.IsFolder))
                yield return c;
            foreach (var c in node.Children.Values.Where(c => !c.IsFolder))
                yield return c;
        }

        private void DrawTreeNode(TreeNode node, int depth)
        {
            if (node.IsFolder)
                DrawFolderNode(node, depth);
            else
                DrawFileNode(node, depth);
        }

        private void DrawFolderNode(TreeNode node, int depth)
        {
            bool expanded = IsFolderExpanded(node.FullPath);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(depth * IndentPixels + 2);

            var label = (expanded ? "▾ " : "▸ ") + node.Name;
            // 폴더 자체는 Lock 대상이 아님 — 클릭 시 접기/펼치기만 수행
            if (GUILayout.Button(label, EditorStyles.label, GUILayout.ExpandWidth(true)))
                _folderExpanded[node.FullPath] = !expanded;

            EditorGUILayout.EndHorizontal();

            if (expanded)
            {
                foreach (var child in EnumerateOrderedChildren(node))
                    DrawTreeNode(child, depth + 1);
            }
        }

        private void DrawFileNode(TreeNode node, int depth)
        {
            var file = node.File;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(depth * IndentPixels + 2);

            var (statusLabel, statusColor) = GetStatusDisplay(file.ChangeStatus);
            var prevColor = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusLabel, EditorStyles.boldLabel, GUILayout.Width(22));
            GUI.color = prevColor;

            GUILayout.Label(node.Name, GUILayout.ExpandWidth(true));
            DrawLockIndicator(file);

            // .meta 파일은 LFS 대상이 아니라 Lock 불가 — 버튼 자리는 비워둠
            if (IsMetaFile(file.RelativePath))
                GUILayout.Label(string.Empty, GUILayout.Width(52));
            else
                DrawLockToggleButton(file);

            EditorGUILayout.EndHorizontal();
        }

        // ── Tree building / state helpers ─────────────────────────────────────

        private static bool IsMetaFile(string path)
            => !string.IsNullOrEmpty(path)
               && path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

        private bool IsFolderExpanded(string path)
            => !_folderExpanded.TryGetValue(path, out var v) || v; // default: expanded

        private void SetAllFoldersExpanded(bool expanded)
        {
            if (_rootNode == null) return;
            var stack = new Stack<TreeNode>();
            foreach (var c in _rootNode.Children.Values) stack.Push(c);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!n.IsFolder) continue;
                _folderExpanded[n.FullPath] = expanded;
                foreach (var c in n.Children.Values) stack.Push(c);
            }
        }

        private static TreeNode BuildTree(List<FileState> files)
        {
            var root = new TreeNode { Name = string.Empty, FullPath = string.Empty, IsFolder = true };
            if (files == null) return root;

            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file?.RelativePath)) continue;

                var parts = file.RelativePath.Split('/');
                var node = root;
                var accum = string.Empty;

                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    accum = accum.Length == 0 ? part : accum + "/" + part;
                    bool isLeaf = (i == parts.Length - 1);

                    if (!node.Children.TryGetValue(part, out var child))
                    {
                        child = new TreeNode
                        {
                            Name     = part,
                            FullPath = accum,
                            IsFolder = !isLeaf,
                        };
                        node.Children[part] = child;
                    }

                    if (isLeaf)
                    {
                        child.IsFolder = false;
                        child.File     = file;
                    }

                    node = child;
                }
            }
            return root;
        }

        /// <summary>디렉터리 트리의 노드. 폴더 또는 파일(리프)을 표현.</summary>
        private class TreeNode
        {
            public string Name;
            public string FullPath;
            public bool IsFolder;
            public FileState File; // null when IsFolder

            public readonly SortedDictionary<string, TreeNode> Children =
                new SortedDictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        }

        private void DrawLockIndicator(FileState file)
        {
            if (!file.IsLocked)
            {
                GUILayout.Label("", GUILayout.Width(90));
                return;
            }

            var prevColor = GUI.color;
            GUI.color = file.IsLockedByMe ? ColorLockedMe : ColorLockedOther;
            var lockText = file.IsLockedByMe ? "🔒 Me" : $"🔒 {file.LfsLock.OwnerName}";
            GUILayout.Label(lockText, EditorStyles.miniLabel, GUILayout.Width(90));
            GUI.color = prevColor;
        }

        private void DrawLockToggleButton(FileState file)
        {
            using (new EditorGUI.DisabledScope(_vm.IsBusy))
            {
                if (!file.IsLocked)
                {
                    if (GUILayout.Button("Lock", EditorStyles.miniButton, GUILayout.Width(52)))
                        _vm.LockFileAsync(file);
                }
                else if (file.IsLockedByMe)
                {
                    if (GUILayout.Button("Unlock", EditorStyles.miniButton, GUILayout.Width(52)))
                        _vm.UnlockFileAsync(file);
                }
                else
                {
                    using (new EditorGUI.DisabledScope(true))
                        GUILayout.Button("Locked", EditorStyles.miniButton, GUILayout.Width(52));
                }
            }
        }

        // ── Action area ───────────────────────────────────────────────────────

        private void DrawActionArea()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Commit & Upload", _sectionLabelStyle);

            _vm.CommitMessage = EditorGUILayout.TextArea(
                _vm.CommitMessage,
                GUILayout.Height(52),
                GUILayout.ExpandWidth(true));

            using (new EditorGUI.DisabledScope(
                _vm.IsBusy || string.IsNullOrWhiteSpace(_vm.CommitMessage)))
            {
                if (GUILayout.Button("Upload  (Lock 파일 → add → commit → push)", GUILayout.Height(30)))
                    _vm.UploadAsync();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_vm.IsBusy))
            {
                if (GUILayout.Button("Fetch  (pull --rebase)", GUILayout.Height(24)))
                    _vm.FetchAsync();

                if (GUILayout.Button("All Unlock", GUILayout.Height(24), GUILayout.Width(90)))
                    _vm.UnlockAllAsync();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // ── Status bar ────────────────────────────────────────────────────────

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(_vm.StatusMessage)) return;

            var style = _vm.LastOperationFailed ? _statusErrStyle : _statusOkStyle;
            EditorGUILayout.SelectableLabel(_vm.StatusMessage, style,
                GUILayout.Height(EditorStyles.helpBox.CalcHeight(
                    new GUIContent(_vm.StatusMessage), EditorGUIUtility.currentViewWidth) + 4));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static (string label, Color color) GetStatusDisplay(FileChangeStatus status)
        {
            return status switch
            {
                FileChangeStatus.Added     => ("A", ColorAdded),
                FileChangeStatus.Deleted   => ("D", ColorDeleted),
                FileChangeStatus.Renamed   => ("R", ColorRenamed),
                FileChangeStatus.Untracked => ("?", ColorUntracked),
                FileChangeStatus.Locked    => ("=", ColorLockedMe),
                _                          => ("M", ColorModified),
            };
        }
    }
}
