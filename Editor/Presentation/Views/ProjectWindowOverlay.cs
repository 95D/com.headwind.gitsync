using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Headwind.GitSync.Data;
using Headwind.GitSync.Data.Models;
using Headwind.GitSync.Domain.UseCases;

namespace Headwind.GitSync.Presentation.Views
{
    /// <summary>
    /// Draws small status overlays on top of file icons in the Project window,
    /// so designers can see git/LFS state without opening the GitSync window.
    ///
    /// Overlays:
    ///   • Modified  — yellow pencil (✎)
    ///   • Added     — green plus   (+)
    ///   • Deleted   — red minus    (−)
    ///   • LFS lock (me)    — green padlock
    ///   • LFS lock (other) — red padlock
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectWindowOverlay
    {
        // ── Cached state ──────────────────────────────────────────────────────

        // Map: asset path (relative, forward-slash, no leading slash) → FileState
        private static Dictionary<string, FileState> _stateCache
            = new Dictionary<string, FileState>();

        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color ColorAdded      = new Color(0.25f, 0.85f, 0.25f, 1f);
        private static readonly Color ColorModified   = new Color(1.00f, 0.85f, 0.10f, 1f);
        private static readonly Color ColorDeleted    = new Color(0.95f, 0.20f, 0.20f, 1f);
        private static readonly Color ColorLockedMe   = new Color(0.25f, 0.85f, 0.25f, 1f);
        private static readonly Color ColorLockedOther= new Color(0.95f, 0.20f, 0.20f, 1f);

        // ── Badge sizes ───────────────────────────────────────────────────────

        private const float BadgeSize  = 10f;
        private const float BadgeInset =  2f;  // from icon corner

        // ── Constructor ───────────────────────────────────────────────────────

        static ProjectWindowOverlay()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;

            // Refresh overlay cache every time the GitSync window data changes.
            // We use a periodic refresh via EditorApplication.update as a fallback.
            EditorApplication.update += OnEditorUpdate;
            RefreshCacheAsync();
        }

        // ── Periodic refresh ─────────────────────────────────────────────────

        private static double _lastRefreshTime = -999;
        private const double RefreshIntervalSeconds = 30;

        private static void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRefreshTime > RefreshIntervalSeconds)
                RefreshCacheAsync();
        }

        /// <summary>
        /// Public entry point so GitSyncViewModel can trigger an immediate
        /// refresh of the overlay cache after a sync/lock operation.
        /// </summary>
        public static void RequestRefresh()
        {
            RefreshCacheAsync();
        }

        private static async void RefreshCacheAsync()
        {
            _lastRefreshTime = EditorApplication.timeSinceStartup;
            try
            {
                var repoRoot   = System.IO.Path.GetDirectoryName(Application.dataPath);
                var repository = new GitRepository(repoRoot);
                var useCase    = new GetGitStatusUseCase(repository);

                var (_, files) = await useCase.ExecuteAsync();

                var newCache = new Dictionary<string, FileState>();
                foreach (var f in files)
                {
                    // git paths are relative to repo root; Unity asset paths start with "Assets/"
                    var key = f.RelativePath.TrimStart('/');
                    newCache[key] = f;
                }
                _stateCache = newCache;

                // Ask Unity to repaint the Project window
                EditorApplication.RepaintProjectWindow();
            }
            catch
            {
                // Silently swallow errors — overlay is non-critical
            }
        }

        // ── Overlay drawing ───────────────────────────────────────────────────

        private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
        {
            if (Event.current.type != EventType.Repaint) return;

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath)) return;

            // Normalise to forward slashes, strip leading slash
            var key = assetPath.Replace('\\', '/').TrimStart('/');
            if (!_stateCache.TryGetValue(key, out var state)) return;

            // ── Change status badge (top-right of icon) ───────────────────────
            var badgeRect = GetTopRightBadgeRect(selectionRect);
            DrawChangeBadge(badgeRect, state.ChangeStatus);

            // ── LFS lock badge (bottom-right of icon) ─────────────────────────
            if (state.IsLocked)
            {
                var lockRect = GetBottomRightBadgeRect(selectionRect);
                DrawLockBadge(lockRect, state.IsLockedByMe);
            }
        }

        // ── Badge helpers ─────────────────────────────────────────────────────

        private static void DrawChangeBadge(Rect rect, FileChangeStatus status)
        {
            Color color;
            string label;

            switch (status)
            {
                case FileChangeStatus.Added:
                    color = ColorAdded; label = "+"; break;
                case FileChangeStatus.Deleted:
                    color = ColorDeleted; label = "−"; break;
                case FileChangeStatus.Renamed:
                    color = ColorModified; label = "R"; break;
                case FileChangeStatus.Untracked:
                    color = ColorModified; label = "?"; break;
                default: // Modified
                    color = ColorModified; label = "✎"; break;
            }

            DrawBadge(rect, color, label);
        }

        private static void DrawLockBadge(Rect rect, bool isOwnedByMe)
        {
            DrawBadge(rect, isOwnedByMe ? ColorLockedMe : ColorLockedOther, "🔒");
        }

        private static void DrawBadge(Rect rect, Color color, string label)
        {
            // Coloured circle background
            var prevColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, GetCircleTexture(), ScaleMode.ScaleToFit);
            GUI.color = prevColor;

            // Label on top
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize  = 7,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white },
            };
            GUI.Label(rect, label, labelStyle);
        }

        // ── Layout helpers ────────────────────────────────────────────────────

        private static Rect GetTopRightBadgeRect(Rect iconRect)
        {
            // For list view the icon is a small square on the left
            float iconSize = Mathf.Min(iconRect.width, iconRect.height);
            return new Rect(
                iconRect.x + iconSize - BadgeSize - BadgeInset,
                iconRect.y + BadgeInset,
                BadgeSize,
                BadgeSize
            );
        }

        private static Rect GetBottomRightBadgeRect(Rect iconRect)
        {
            float iconSize = Mathf.Min(iconRect.width, iconRect.height);
            return new Rect(
                iconRect.x + iconSize - BadgeSize - BadgeInset,
                iconRect.y + iconSize - BadgeSize - BadgeInset,
                BadgeSize,
                BadgeSize
            );
        }

        // ── Texture cache ─────────────────────────────────────────────────────

        private static Texture2D _circleTexture;

        private static Texture2D GetCircleTexture()
        {
            if (_circleTexture != null) return _circleTexture;

            const int size = 16;
            _circleTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            _circleTexture.hideFlags = HideFlags.HideAndDontSave;

            float center = size / 2f;
            float radius = center - 0.5f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - center + 0.5f;
                float dy = y - center + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                _circleTexture.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }

            _circleTexture.Apply();
            return _circleTexture;
        }
    }
}
