using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using Headwind.GitSync.Data;
using Headwind.GitSync.Data.Models;
using Headwind.GitSync.Domain.Interfaces;
using Headwind.GitSync.Domain.UseCases;

namespace Headwind.GitSync.Presentation.ViewModels
{
    /// <summary>
    /// Holds all UI-visible state and exposes async commands.
    /// The View observes <see cref="OnStateChanged"/> to know when to repaint.
    /// </summary>
    public class GitSyncViewModel
    {
        // ── Observable state ──────────────────────────────────────────────────

        public string CurrentBranch     { get; private set; } = "(loading…)";
        public List<FileState> Files    { get; private set; } = new List<FileState>();
        public bool IsBusy              { get; private set; }
        public string StatusMessage     { get; private set; } = string.Empty;
        public bool LastOperationFailed { get; private set; }
        public string CommitMessage     { get; set; } = string.Empty;

        // ── Remote state ──────────────────────────────────────────────────────

        public string RemoteUrl        { get; private set; } = string.Empty;
        public string PendingRemoteUrl { get; set; } = string.Empty;
        public bool HasRemote => !string.IsNullOrEmpty(RemoteUrl);

        public event Action OnStateChanged;

        // ── Use cases ─────────────────────────────────────────────────────────

        private readonly GetGitStatusUseCase _getStatusUseCase;
        private readonly FetchUseCase        _fetchUseCase;
        private readonly UploadUseCase       _uploadUseCase;
        private readonly LockFileUseCase     _lockUseCase;
        private readonly UnlockFileUseCase   _unlockUseCase;
        private readonly GetRemoteUrlUseCase _getRemoteUrlUseCase;
        private readonly SetRemoteUrlUseCase _setRemoteUrlUseCase;

        public GitSyncViewModel(IGitRepository repository)
        {
            _getStatusUseCase    = new GetGitStatusUseCase(repository);
            _fetchUseCase        = new FetchUseCase(repository);
            _uploadUseCase       = new UploadUseCase(repository);
            _lockUseCase         = new LockFileUseCase(repository);
            _unlockUseCase       = new UnlockFileUseCase(repository);
            _getRemoteUrlUseCase = new GetRemoteUrlUseCase(repository);
            _setRemoteUrlUseCase = new SetRemoteUrlUseCase(repository);
        }

        // ── Commands ──────────────────────────────────────────────────────────

        public async void RefreshAsync()
        {
            if (IsBusy) return;
            SetBusy(true, "Refreshing…");
            try
            {
                var statusTask = _getStatusUseCase.ExecuteAsync();
                var remoteTask = _getRemoteUrlUseCase.ExecuteAsync();
                await Task.WhenAll(statusTask, remoteTask);

                var (branch, files) = statusTask.Result;
                CurrentBranch    = branch;
                Files            = files;
                RemoteUrl        = remoteTask.Result;
                PendingRemoteUrl = RemoteUrl;

                SetBusy(false, files.Count == 0 ? "Working tree is clean." : string.Empty);
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Error: {ex.Message}", failed: true);
            }
        }

        public async void SetRemoteUrlAsync()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(PendingRemoteUrl))
            {
                SetStatus("URL을 입력해 주세요.", failed: true);
                return;
            }

            SetBusy(true, "Remote 설정 중…");
            try
            {
                var (success, message) = await _setRemoteUrlUseCase.ExecuteAsync(PendingRemoteUrl.Trim());
                if (success) RemoteUrl = PendingRemoteUrl.Trim();
                SetBusy(false, message, failed: !success);
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Remote 설정 오류: {ex.Message}", failed: true);
            }
        }

        /// <summary>git pull --rebase</summary>
        public async void FetchAsync()
        {
            if (IsBusy) return;
            SetBusy(true, "Fetching…");
            try
            {
                var (success, log) = await _fetchUseCase.ExecuteAsync();
                await RefreshInternalAsync();
                SetBusy(false, success ? "Fetch 완료." : $"Fetch 실패:\n{log}", failed: !success);
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Fetch 오류: {ex.Message}", failed: true);
            }
        }

        /// <summary>내가 Lock한 변경 파일 + 페어링 대상을 add → commit → push → unlock</summary>
        public async void UploadAsync()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                SetStatus("커밋 메시지를 입력해 주세요.", failed: true);
                return;
            }

            // Seed: 내가 Lock했고 실제로 변경된 파일 (Locked 상태 = 미변경 제외)
            var lockedTargets = new List<string>();
            foreach (var f in Files)
                if (f.IsLockedByMe && f.ChangeStatus != FileChangeStatus.Locked)
                    lockedTargets.Add(f.RelativePath);

            if (lockedTargets.Count == 0)
            {
                SetStatus("업로드할 파일이 없습니다. (Lock된 변경 파일 없음)", failed: true);
                return;
            }

            // 페어링 규칙 적용: (1) 자산 ↔ .meta 쌍  (2) 신규 상위 폴더 .meta 체인
            var uploadTargets = BuildPairedUploadTargets(lockedTargets);
            var pairedCount   = uploadTargets.Count - lockedTargets.Count;

            var busyMsg = pairedCount > 0
                ? $"{uploadTargets.Count}개 파일 업로드 중… (Lock {lockedTargets.Count} + 페어 {pairedCount})"
                : $"{uploadTargets.Count}개 파일 업로드 중…";
            SetBusy(true, busyMsg);
            try
            {
                var (success, log) = await _uploadUseCase.ExecuteAsync(CommitMessage, uploadTargets);
                if (success)
                {
                    // Push 성공 → 원래 Lock했던 파일만 Unlock (.meta / 폴더 .meta는 Lock 대상 아님)
                    var unlockFailed = new List<string>();
                    foreach (var path in lockedTargets)
                    {
                        var (ok, _) = await _unlockUseCase.ExecuteAsync(path);
                        if (!ok) unlockFailed.Add(path);
                    }

                    CommitMessage = string.Empty;
                    await RefreshInternalAsync();

                    var msg = unlockFailed.Count > 0
                        ? $"Upload 완료. Unlock 실패:\n{string.Join("\n", unlockFailed)}"
                        : $"Upload 완료. {lockedTargets.Count}개 파일 Lock 해제됨." +
                          (pairedCount > 0 ? $" (페어 {pairedCount}개 함께 커밋)" : string.Empty);
                    SetBusy(false, msg, failed: unlockFailed.Count > 0);
                }
                else
                {
                    SetBusy(false, $"Upload 실패:\n{log}", failed: true);
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Upload 오류: {ex.Message}", failed: true);
            }
        }

        // ── Pairing rules ─────────────────────────────────────────────────────

        /// <summary>
        /// 두 개의 페어링 규칙으로 업로드 대상을 확장:
        /// (1) 자산 ↔ .meta 쌍 (양방향, 짝이 Files 내에 존재할 때만 포함)
        /// (2) 대상 파일 경로를 따라 올라가며, Files 내에 존재하는 상위 폴더 .meta만 포함
        ///     (이미 tracked인 미변경 상위 폴더 .meta는 git status에 없으므로 자연 제외)
        /// </summary>
        private List<string> BuildPairedUploadTargets(List<string> seeds)
        {
            var byPath = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Files)
                byPath[f.RelativePath] = f;

            var targets = new HashSet<string>(seeds, StringComparer.OrdinalIgnoreCase);
            foreach (var path in seeds)
            {
                AddPairIfPresent(path, targets, byPath);
                AddParentMetaChain(path, targets, byPath);
            }
            return targets.ToList();
        }

        private static void AddPairIfPresent(
            string path,
            HashSet<string> targets,
            Dictionary<string, FileState> byPath)
        {
            string pair;
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                pair = path.Substring(0, path.Length - ".meta".Length);
            else
                pair = path + ".meta";

            if (byPath.ContainsKey(pair))
                targets.Add(pair);
        }

        private static void AddParentMetaChain(
            string path,
            HashSet<string> targets,
            Dictionary<string, FileState> byPath)
        {
            var idx = path.LastIndexOf('/');
            while (idx > 0)
            {
                var parent     = path.Substring(0, idx);
                var parentMeta = parent + ".meta";
                if (byPath.ContainsKey(parentMeta))
                    targets.Add(parentMeta);
                idx = parent.LastIndexOf('/');
            }
        }

        /// <summary>현재 내 Lock 전체 해제</summary>
        public async void UnlockAllAsync()
        {
            if (IsBusy) return;

            var myPaths = GitLockCache.GetMyLockedPaths();
            if (myPaths.Count == 0)
            {
                SetStatus("해제할 Lock이 없습니다.");
                return;
            }

            SetBusy(true, $"{myPaths.Count}개 Lock 해제 중…");
            try
            {
                var failed = new List<string>();
                foreach (var path in myPaths)
                {
                    var (ok, _) = await _unlockUseCase.ExecuteAsync(path);
                    if (!ok) failed.Add(path);
                }

                await RefreshInternalAsync();
                SetBusy(false,
                    failed.Count > 0
                        ? $"일부 Unlock 실패:\n{string.Join("\n", failed)}"
                        : $"{myPaths.Count}개 Lock 모두 해제됨.",
                    failed: failed.Count > 0);
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Unlock 오류: {ex.Message}", failed: true);
            }
        }

        public async void LockFileAsync(FileState file)
        {
            if (IsBusy) return;
            SetBusy(true, $"Locking {file.RelativePath}…");
            try
            {
                var (success, message) = await _lockUseCase.ExecuteAsync(file.RelativePath);
                SetBusy(false, message, failed: !success);
                if (success) await RefreshInternalAsync();
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Lock error: {ex.Message}", failed: true);
            }
        }

        public async void UnlockFileAsync(FileState file)
        {
            if (IsBusy) return;
            SetBusy(true, $"Unlocking {file.RelativePath}…");
            try
            {
                var (success, message) = await _unlockUseCase.ExecuteAsync(file.RelativePath);
                SetBusy(false, message, failed: !success);
                if (success) await RefreshInternalAsync();
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Unlock error: {ex.Message}", failed: true);
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private async Task RefreshInternalAsync()
        {
            var (branch, files) = await _getStatusUseCase.ExecuteAsync();
            CurrentBranch = branch;
            Files         = files;
        }

        private void SetBusy(bool busy, string message = "", bool failed = false)
        {
            IsBusy              = busy;
            StatusMessage       = message;
            LastOperationFailed = failed;
            NotifyStateChanged();
        }

        private void SetStatus(string message, bool failed = false)
        {
            StatusMessage       = message;
            LastOperationFailed = failed;
            NotifyStateChanged();
        }

        private void NotifyStateChanged()
        {
            EditorApplication.delayCall += () => OnStateChanged?.Invoke();
        }
    }
}
