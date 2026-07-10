using System;
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
            // --untracked-files=all: 새 폴더를 하나의 엔트리로 집약하지 않고 내부 파일까지 개별 나열
            // (기본 'normal' 모드는 "?? Assets/NewFolder/" 처럼 trailing slash를 붙여서 트리 구성이 깨짐)
            var statusTask = GitProcessUtility.RunAsync(_repoRoot, "status --porcelain --untracked-files=all");
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
                        string.Equals(f.RelativePath, path, StringComparison.OrdinalIgnoreCase));
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

            await PopulateLfsTrackedAsync(fileStates).ConfigureAwait(false);

            return fileStates;
        }

        /// <summary>
        /// <c>git check-attr --stdin filter</c> 을 배치 호출해 각 파일의 LFS 추적 여부를 채웁니다.
        /// 명령 실패 시 <see cref="FileState.IsLfsTracked"/> 기본값(true)이 유지되어
        /// UI가 파일을 숨기지 않도록 안전하게 동작합니다.
        /// </summary>
        private async Task PopulateLfsTrackedAsync(List<FileState> fileStates)
        {
            if (fileStates == null || fileStates.Count == 0) return;

            var stdin = string.Join("\n", fileStates.Select(f => f.RelativePath));
            var result = await GitProcessUtility
                .RunAsync(_repoRoot, "check-attr --stdin filter", stdin)
                .ConfigureAwait(false);
            if (!result.IsSuccess) return;

            // 결과 라인 형식: "<path>: filter: <value>" (value = "lfs" 이면 LFS 추적 대상)
            var byPath = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fileStates)
                byPath[f.RelativePath] = f;

            foreach (var rawLine in result.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;

                var sepIdx = line.LastIndexOf(": filter:", StringComparison.Ordinal);
                if (sepIdx < 0) continue;

                var path  = line.Substring(0, sepIdx);
                var value = line.Substring(sepIdx + ": filter:".Length).Trim();

                if (byPath.TryGetValue(path, out var state))
                    state.IsLfsTracked = string.Equals(value, "lfs", StringComparison.Ordinal);
            }
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
