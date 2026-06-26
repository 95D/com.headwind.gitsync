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

        /// <summary>현재 저장된 origin URL. 없으면 빈 문자열.</summary>
        public string RemoteUrl        { get; private set; } = string.Empty;

        /// <summary>UI 입력 필드에 바인딩되는 임시 값.</summary>
        public string PendingRemoteUrl { get; set; } = string.Empty;

        public bool HasRemote => !string.IsNullOrEmpty(RemoteUrl);

        /// <summary>Raised on the Unity main thread after any state mutation.</summary>
        public event Action OnStateChanged;

        // ── Use cases ─────────────────────────────────────────────────────────

        private readonly GetGitStatusUseCase _getStatusUseCase;
        private readonly SyncUseCase         _syncUseCase;
        private readonly LockFileUseCase     _lockUseCase;
        private readonly UnlockFileUseCase   _unlockUseCase;
        private readonly GetRemoteUrlUseCase _getRemoteUrlUseCase;
        private readonly SetRemoteUrlUseCase _setRemoteUrlUseCase;

        public GitSyncViewModel(IGitRepository repository)
        {
            _getStatusUseCase  = new GetGitStatusUseCase(repository);
            _syncUseCase       = new SyncUseCase(repository);
            _lockUseCase       = new LockFileUseCase(repository);
            _unlockUseCase     = new UnlockFileUseCase(repository);
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
                CurrentBranch  = branch;
                Files          = files;
                RemoteUrl      = remoteTask.Result;
                PendingRemoteUrl = RemoteUrl; // 입력 필드를 현재 값으로 초기화

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
                if (success)
                {
                    RemoteUrl = PendingRemoteUrl.Trim();
                    SetBusy(false, message);
                }
                else
                {
                    SetBusy(false, message, failed: true);
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Remote 설정 오류: {ex.Message}", failed: true);
            }
        }

        public async void SyncAsync()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(CommitMessage))
            {
                SetStatus("커밋 메시지를 입력해 주세요.", failed: true);
                return;
            }

            SetBusy(true, "Syncing…");
            try
            {
                var (success, log) = await _syncUseCase.ExecuteAsync(CommitMessage);
                CommitMessage = string.Empty;
                if (success)
                {
                    await RefreshInternalAsync();
                    SetBusy(false, "Sync complete.");
                }
                else
                {
                    SetBusy(false, $"Sync failed:\n{log}", failed: true);
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Sync error: {ex.Message}", failed: true);
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
