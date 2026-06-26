using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Headwind.GitSync.Data.Models;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Data
{
    /// <summary>
    /// Concrete implementation of <see cref="IGitRepository"/>.
    /// Delegates all I/O to <see cref="GitProcessUtility"/> and parsing to
    /// <see cref="GitDataParser"/>.
    /// </summary>
    public class GitRepository : IGitRepository
    {
        private readonly string _repoRoot;

        public GitRepository(string repoRoot)
        {
            _repoRoot = repoRoot;
        }

        public async Task<string> GetCurrentBranchAsync()
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, "rev-parse --abbrev-ref HEAD");
            return result.IsSuccess ? result.Stdout.Trim() : "(unknown)";
        }

        public async Task<string> GetCurrentUserNameAsync()
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, "config user.name");
            return result.IsSuccess ? result.Stdout.Trim() : string.Empty;
        }

        public async Task<List<FileState>> GetStatusAsync()
        {
            // Run git status and git config user.name concurrently
            var statusTask = GitProcessUtility.RunAsync(_repoRoot, "status --porcelain");
            var userTask   = GetCurrentUserNameAsync();

            await Task.WhenAll(statusTask, userTask);

            var fileStates = GitDataParser.ParseGitStatus(statusTask.Result.Stdout);
            var userName   = userTask.Result;

            // LFS locks — --verify 로 서버가 직접 소유권을 마킹하도록 요청.
            // 실패(오프라인·구버전 git-lfs)하면 일반 lfs locks 로 fallback.
            var locksResult = await GitProcessUtility.RunAsync(_repoRoot, "lfs locks --verify");
            if (!locksResult.IsSuccess)
                locksResult = await GitProcessUtility.RunAsync(_repoRoot, "lfs locks");
            if (locksResult.IsSuccess)
            {
                var locks = GitDataParser.ParseLfsLocks(locksResult.Stdout, userName);
                GitLockCache.Update(locks, userName);
                GitDataParser.MergeLockInfo(fileStates, locks);

                // git status에 없지만 내가 Lock한 파일도 목록에 추가
                foreach (var kvp in locks)
                {
                    if (!kvp.Value.IsOwnedByMe) continue;
                    var path = kvp.Key;
                    bool alreadyListed = false;
                    foreach (var f in fileStates)
                    {
                        if (string.Equals(f.RelativePath, path, System.StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyListed = true;
                            break;
                        }
                    }
                    if (!alreadyListed)
                    {
                        fileStates.Add(new FileState
                        {
                            RelativePath = path,
                            ChangeStatus = FileChangeStatus.Locked,
                            LfsLock      = kvp.Value,
                        });
                    }
                }
            }

            return fileStates;
        }

        public async Task<(bool success, string log)> SyncAsync(string commitMessage)
        {
            var log = new StringBuilder();

            var remoteCheck = await ValidateRemoteAsync();
            if (!remoteCheck.valid)
                return (false, remoteCheck.message);

            var addResult = await GitProcessUtility.RunAsync(_repoRoot, "add .");
            log.AppendLine($"[add] {(addResult.IsSuccess ? "OK" : addResult.Stderr)}");
            if (!addResult.IsSuccess)
                return (false, log.ToString());

            var safeMessage  = commitMessage.Replace("\"", "\\\"");
            var commitResult = await GitProcessUtility.RunAsync(_repoRoot, $"commit -m \"{safeMessage}\"");
            log.AppendLine($"[commit] {(commitResult.IsSuccess ? "OK" : commitResult.Stderr)}");

            bool nothingToCommit = commitResult.Stdout.Contains("nothing to commit")
                                || commitResult.Stderr.Contains("nothing to commit");
            if (!commitResult.IsSuccess && !nothingToCommit)
                return (false, log.ToString());

            var pullResult = await GitProcessUtility.RunAsync(_repoRoot, "pull --rebase");
            log.AppendLine($"[pull --rebase] {(pullResult.IsSuccess ? "OK" : pullResult.Stderr)}");
            if (!pullResult.IsSuccess)
                return (false, log.ToString());

            var pushResult = await GitProcessUtility.RunAsync(_repoRoot, "push");
            log.AppendLine($"[push] {(pushResult.IsSuccess ? "OK" : pushResult.Stderr)}");

            return (pushResult.IsSuccess, log.ToString());
        }

        public async Task<(bool success, string message)> LockFileAsync(string relativePath)
        {
            var remoteCheck = await ValidateRemoteAsync();
            if (!remoteCheck.valid)
                return (false, remoteCheck.message);

            var result = await GitProcessUtility.RunAsync(_repoRoot, $"lfs lock \"{relativePath}\"");
            return result.IsSuccess
                ? (true, $"Locked: {relativePath}")
                : (false, result.Stderr);
        }

        public async Task<(bool success, string message)> UnlockFileAsync(string relativePath)
        {
            var remoteCheck = await ValidateRemoteAsync();
            if (!remoteCheck.valid)
                return (false, remoteCheck.message);

            var result = await GitProcessUtility.RunAsync(_repoRoot, $"lfs unlock \"{relativePath}\"");
            return result.IsSuccess
                ? (true, $"Unlocked: {relativePath}")
                : (false, result.Stderr);
        }

        public async Task<string> GetRemoteUrlAsync()
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, "remote get-url origin");
            return result.IsSuccess ? result.Stdout.Trim() : string.Empty;
        }

        public async Task<(bool success, string message)> SetRemoteUrlAsync(string url)
        {
            // origin이 이미 있으면 set-url, 없으면 add
            var existing = await GetRemoteUrlAsync();
            var command  = string.IsNullOrEmpty(existing)
                ? $"remote add origin \"{url}\""
                : $"remote set-url origin \"{url}\"";

            var result = await GitProcessUtility.RunAsync(_repoRoot, command);
            return result.IsSuccess
                ? (true, $"Remote 설정 완료: {url}")
                : (false, result.Stderr);
        }

        public async Task<bool> IsLfsTrackedAsync(string relativePath)
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, $"check-attr filter -- \"{relativePath}\"");
            return result.IsSuccess && result.Stdout.Contains(": filter: lfs");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// LFS lock/unlock, Sync 실행 전 remote 존재 여부를 확인합니다.
        /// </summary>
        private async Task<(bool valid, string message)> ValidateRemoteAsync()
        {
            var url = await GetRemoteUrlAsync();
            if (string.IsNullOrEmpty(url))
                return (false,
                    "원격 저장소(Remote)가 설정되지 않았습니다.\n" +
                    "GitSync 창 상단의 Remote 설정에서 URL을 입력해 주세요.");

            return (true, string.Empty);
        }
    }
}
