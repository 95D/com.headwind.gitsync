using System;
using System.Collections.Generic;
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

        /// <summary>내가 Lock한 변경 파일만 add → commit → push → unlock</summary>
        public async void UploadAsync()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                SetStatus("커밋 메시지를 입력해 주세요.", failed: true);
                return;
            }

            // Lock되고 실제로 변경된 파일만 (Locked 상태 = 미변경 제외)
            var targets = new List<string>();
            foreach (var f in Files)
                if (f.IsLockedByMe && f.ChangeStatus != FileChangeStatus.Locked)
                    targets.Add(f.RelativePath);

            if (targets.Count == 0)
            {
                SetStatus("업로드할 파일이 없습니다. (Lock된 변경 파일 없음)", failed: true);
                return;
            }

            SetBusy(true, $"{targets.Count}개 파일 업로드 중…");
            try
            {
                var (success, log) = await _uploadUseCase.ExecuteAsync(CommitMessage, targets);
                if (success)
                {
                    // Push 성공 → 업로드한 파일 Unlock
                    var unlockFailed = new List<string>();
                    foreach (var path in targets)
                    {
                        var (ok, _) = await _unlockUseCase.ExecuteAsync(path);
                        if (!ok) unlockFailed.Add(path);
                    }

                    CommitMessage = string.Empty;
                    await RefreshInternalAsync();

                    var msg = unlockFailed.Count > 0
                        ? $"Upload 완료. Unlock 실패:\n{string.Join("\n", unlockFailed)}"
                        : $"Upload 완료. {targets.Count}개 파일 Lock 해제됨.";
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
