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
            EditorGUILayout.LabelField("Changes & Locks", _sectionLabelStyle);
            EditorGUILayout.Space(2);

            _fileListScroll = EditorGUILayout.BeginScrollView(
                _fileListScroll, GUILayout.ExpandHeight(true));

            if (_vm.Files == null || _vm.Files.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _vm.IsBusy ? "Loading…" : "No changes detected.",
                    MessageType.None);
            }
            else
            {
                foreach (var file in _vm.Files)
                    DrawFileRow(file);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawFileRow(FileState file)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var (statusLabel, statusColor) = GetStatusDisplay(file.ChangeStatus);
            var prevColor = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusLabel, EditorStyles.boldLabel, GUILayout.Width(22));
            GUI.color = prevColor;

            GUILayout.Label(file.RelativePath, GUILayout.ExpandWidth(true));
            DrawLockIndicator(file);
            DrawLockToggleButton(file);

            EditorGUILayout.EndHorizontal();
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
            EditorGUILayout.LabelField("Commit & Sync", _sectionLabelStyle);

            _vm.CommitMessage = EditorGUILayout.TextArea(
                _vm.CommitMessage,
                GUILayout.Height(52),
                GUILayout.ExpandWidth(true));

            using (new EditorGUI.DisabledScope(
                _vm.IsBusy || string.IsNullOrWhiteSpace(_vm.CommitMessage)))
            {
                if (GUILayout.Button("Sync  (add → commit → pull → push)", GUILayout.Height(30)))
                    _vm.SyncAsync();
            }

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
