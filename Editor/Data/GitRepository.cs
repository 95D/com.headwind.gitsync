using System.Collections.Generic;
using System.Linq;
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
            var result = await GitProcessUtility.RunAsync(_repoRoot, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
            return result.IsSuccess ? result.Stdout.Trim() : "(unknown)";
        }

        public async Task<string> GetCurrentUserNameAsync()
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, "config user.name").ConfigureAwait(false);
            return result.IsSuccess ? result.Stdout.Trim() : string.Empty;
        }

        public async Task<List<FileState>> GetStatusAsync()
        {
            var statusTask = GitProcessUtility.RunAsync(_repoRoot, "status --porcelain");
            var userTask   = GetCurrentUserNameAsync();
            await Task.WhenAll(statusTask, userTask).ConfigureAwait(false);

            var fileStates = GitDataParser.ParseGitStatus(statusTask.Result.Stdout);
            var userName   = userTask.Result;

            // --verify 로 서버가 소유권을 직접 마킹. 실패 시 일반 lfs locks 로 fallback.
            var locksResult = await GitProcessUtility.RunAsync(_repoRoot, "lfs locks --verify").ConfigureAwait(false);
            if (!locksResult.IsSuccess)
                locksResult = await GitProcessUtility.RunAsync(_repoRoot, "lfs locks").ConfigureAwait(false);

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
                    bool alreadyListed = fileStates.Any(f =>
                        string.Equals(f.RelativePath, path, System.StringComparison.OrdinalIgnoreCase));
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

        public async Task<(bool success, string log)> FetchAsync()
        {
            var remoteCheck = await ValidateRemoteAsync().ConfigureAwait(false);
            if (!remoteCheck.valid) return (false, remoteCheck.message);

            var result = await GitProcessUtility.RunAsync(_repoRoot, "pull --rebase").ConfigureAwait(false);
            return result.IsSuccess
                ? (true, "pull --rebase 완료.")
                : (false, result.Stderr);
        }

        public async Task<(bool success, string log)> UploadAsync(
            string commitMessage, IEnumerable<string> paths)
        {
            var remoteCheck = await ValidateRemoteAsync().ConfigureAwait(false);
            if (!remoteCheck.valid) return (false, remoteCheck.message);

            var log      = new StringBuilder();
            var pathList = paths.ToList();

            // git add <locked files only>
            var quotedPaths = string.Join(" ", pathList.Select(p => $"\"{p}\""));
            var addResult   = await GitProcessUtility.RunAsync(_repoRoot, $"add {quotedPaths}").ConfigureAwait(false);
            log.AppendLine($"[add] {(addResult.IsSuccess ? "OK" : addResult.Stderr)}");
            if (!addResult.IsSuccess) return (false, log.ToString());

            // git commit
            var safeMsg     = commitMessage.Replace("\"", "\\\"");
            var commitResult = await GitProcessUtility.RunAsync(_repoRoot, $"commit -m \"{safeMsg}\"").ConfigureAwait(false);
            log.AppendLine($"[commit] {(commitResult.IsSuccess ? "OK" : commitResult.Stderr)}");

            bool nothingToCommit = commitResult.Stdout.Contains("nothing to commit")
                                || commitResult.Stderr.Contains("nothing to commit");
            if (!commitResult.IsSuccess && !nothingToCommit) return (false, log.ToString());

            // git push
            var pushResult = await GitProcessUtility.RunAsync(_repoRoot, "push").ConfigureAwait(false);
            log.AppendLine($"[push] {(pushResult.IsSuccess ? "OK" : pushResult.Stderr)}");

            return (pushResult.IsSuccess, log.ToString());
        }

        public async Task<(bool success, string message)> LockFileAsync(string relativePath)
        {
            var remoteCheck = await ValidateRemoteAsync().ConfigureAwait(false);
            if (!remoteCheck.valid) return (false, remoteCheck.message);

            var result = await GitProcessUtility.RunAsync(_repoRoot, $"lfs lock \"{relativePath}\"").ConfigureAwait(false);
            return result.IsSuccess
                ? (true, $"Locked: {relativePath}")
                : (false, result.Stderr);
        }

        public async Task<(bool success, string message)> UnlockFileAsync(string relativePath)
        {
            var remoteCheck = await ValidateRemoteAsync().ConfigureAwait(false);
            if (!remoteCheck.valid) return (false, remoteCheck.message);

            var result = await GitProcessUtility.RunAsync(_repoRoot, $"lfs unlock \"{relativePath}\"").ConfigureAwait(false);
            return result.IsSuccess
                ? (true, $"Unlocked: {relativePath}")
                : (false, result.Stderr);
        }

        public async Task<string> GetRemoteUrlAsync()
        {
            var result = await GitProcessUtility.RunAsync(_repoRoot, "remote get-url origin").ConfigureAwait(false);
            return result.IsSuccess ? result.Stdout.Trim() : string.Empty;
        }

        public async Task<(bool success, string message)> SetRemoteUrlAsync(string url)
        {
            var existing = await GetRemoteUrlAsync().ConfigureAwait(false);
            var command  = string.IsNullOrEmpty(existing)
                ? $"remote add origin \"{url}\""
                : $"remote set-url origin \"{url}\"";

            var result = await GitProcessUtility.RunAsync(_repoRoot, command).ConfigureAwait(false);
            return result.IsSuccess
                ? (true, $"Remote 설정 완료: {url}")
                : (false, result.Stderr);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<(bool valid, string message)> ValidateRemoteAsync()
        {
            var url = await GetRemoteUrlAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(url))
                return (false,
                    "원격 저장소(Remote)가 설정되지 않았습니다.\n" +
                    "GitSync 창 상단의 Remote 설정에서 URL을 입력해 주세요.");
            return (true, string.Empty);
        }
    }
}
