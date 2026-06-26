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

        private readonly GetGitStatusUseCase  _getStatusUseCase;
        private readonly SyncUseCase          _syncUseCase;
        private readonly LockFileUseCase      _lockUseCase;
        private readonly UnlockFileUseCase    _unlockUseCase;
        private readonly GetRemoteUrlUseCase  _getRemoteUrlUseCase;
        private readonly SetRemoteUrlUseCase  _setRemoteUrlUseCase;
        private readonly IsLfsTrackedUseCase  _isLfsTrackedUseCase;

        public GitSyncViewModel(IGitRepository repository)
        {
            _getStatusUseCase    = new GetGitStatusUseCase(repository);
            _syncUseCase         = new SyncUseCase(repository);
            _lockUseCase         = new LockFileUseCase(repository);
            _unlockUseCase       = new UnlockFileUseCase(repository);
            _getRemoteUrlUseCase = new GetRemoteUrlUseCase(repository);
            _setRemoteUrlUseCase = new SetRemoteUrlUseCase(repository);
            _isLfsTrackedUseCase = new IsLfsTrackedUseCase(repository);
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

            // ── Sync 전 LFS Lock 검증 ─────────────────────────────────────────
            SetBusy(true, "Lock 상태 확인 중…");
            try
            {
                var blockReason = await ValidateLfsLocksAsync();
                if (blockReason != null)
                {
                    SetBusy(false, blockReason, failed: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Lock 검증 오류: {ex.Message}", failed: true);
                return;
            }

            // Sync 전에 내가 Lock한 파일 목록을 캡처 (Sync 후 Unlock에 사용)
            var myLockedPaths = new List<string>();
            foreach (var f in Files)
                if (f.IsLockedByMe) myLockedPaths.Add(f.RelativePath);

            // ── Sync 실행 ─────────────────────────────────────────────────────
            SetBusy(true, "Syncing…");
            try
            {
                var (success, log) = await _syncUseCase.ExecuteAsync(CommitMessage);
                if (success)
                {
                    // Push 성공 → Lock 걸었던 파일 자동 Unlock
                    var unlockFailed = new List<string>();
                    foreach (var path in myLockedPaths)
                    {
                        var (unlocked, _) = await _unlockUseCase.ExecuteAsync(path);
                        if (!unlocked) unlockFailed.Add(path);
                    }

                    CommitMessage = string.Empty;
                    await RefreshInternalAsync();

                    var statusMsg = unlockFailed.Count > 0
                        ? $"Sync 완료. 일부 파일 Unlock 실패:\n{string.Join("\n", unlockFailed)}"
                        : myLockedPaths.Count > 0
                            ? $"Sync 완료. {myLockedPaths.Count}개 파일 Lock 해제됨."
                            : "Sync 완료.";
                    SetBusy(false, statusMsg, failed: unlockFailed.Count > 0);
                }
                else
                {
                    // Push 실패 → Lock 유지 (의도적)
                    SetBusy(false, $"Sync 실패:\n{log}", failed: true);
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Sync 오류: {ex.Message}", failed: true);
            }
        }

        /// <summary>
        /// 변경된 LFS 파일 중 내 Lock이 없거나 타인이 Lock한 경우 오류 메시지를 반환합니다.
        /// 문제가 없으면 null을 반환합니다.
        /// </summary>
        private async Task<string> ValidateLfsLocksAsync()
        {
            if (Files == null || Files.Count == 0) return null;

            // 각 파일의 LFS 추적 여부를 병렬 확인
            var checks = new List<Task<(FileState file, bool isLfs)>>();
            foreach (var f in Files)
            {
                var file = f;
                checks.Add(Task.Run(async () =>
                    (file, await _isLfsTrackedUseCase.ExecuteAsync(file.RelativePath))));
            }
            var results = await Task.WhenAll(checks);

            // 타인이 Lock 중인 LFS 파일
            var lockedByOther = new List<FileState>();
            // Lock 없이 수정된 LFS 파일
            var notLocked = new List<FileState>();

            foreach (var (file, isLfs) in results)
            {
                if (!isLfs) continue;
                if (file.IsLocked && !file.IsLockedByMe) lockedByOther.Add(file);
                else if (!file.IsLocked)                 notLocked.Add(file);
            }

            if (lockedByOther.Count > 0)
            {
                var names = string.Join("\n", lockedByOther.ConvertAll(
                    f => $"  {f.RelativePath}  ({f.LfsLock.OwnerName})"));
                return $"다른 사용자가 Lock 중인 파일이 있어 Sync할 수 없습니다:\n{names}";
            }

            if (notLocked.Count > 0)
            {
                var names = string.Join("\n", notLocked.ConvertAll(f => $"  {f.RelativePath}"));
                return $"Lock 없이 편집된 LFS 파일이 있습니다:\n{names}";
            }

            return null;
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
