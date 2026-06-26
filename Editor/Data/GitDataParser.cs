using System;
using System.Collections.Generic;
using Headwind.GitSync.Data.Models;

namespace Headwind.GitSync.Data
{
    /// <summary>
    /// Converts raw git CLI text output into typed C# model objects.
    /// All methods are pure (no I/O) to allow easy unit testing.
    /// </summary>
    public static class GitDataParser
    {
        // ── git status --porcelain ────────────────────────────────────────────

        /// <summary>
        /// Parses <c>git status --porcelain</c> output into a list of <see cref="FileState"/>.
        /// </summary>
        /// <param name="porcelainOutput">Raw stdout of the command.</param>
        public static List<FileState> ParseGitStatus(string porcelainOutput)
        {
            var result = new List<FileState>();
            if (string.IsNullOrWhiteSpace(porcelainOutput))
                return result;

            foreach (var line in porcelainOutput.Split('\n'))
            {
                if (line.Length < 3)
                    continue;

                char indexStatus = line[0];   // staged
                char workStatus  = line[1];   // unstaged
                string rawPath   = line.Substring(3).Trim();

                // Handle rename notation: "old -> new"
                string path = rawPath.Contains(" -> ")
                    ? rawPath.Substring(rawPath.IndexOf(" -> ", StringComparison.Ordinal) + 4)
                    : rawPath;

                path = path.Replace('\\', '/');

                var changeStatus = DetermineStatus(indexStatus, workStatus);
                result.Add(new FileState
                {
                    RelativePath = path,
                    ChangeStatus = changeStatus,
                });
            }

            return result;
        }

        private static FileChangeStatus DetermineStatus(char index, char work)
        {
            // Untracked files
            if (index == '?' && work == '?')
                return FileChangeStatus.Untracked;

            // Prefer the more "interesting" of the two status chars
            char dominant = (index != ' ' && index != '?') ? index : work;

            return dominant switch
            {
                'A' => FileChangeStatus.Added,
                'D' => FileChangeStatus.Deleted,
                'R' => FileChangeStatus.Renamed,
                _   => FileChangeStatus.Modified,
            };
        }

        // ── git lfs locks ────────────────────────────────────────────────────

        /// <summary>
        /// Parses tab-separated <c>git lfs locks</c> output into a dictionary keyed by
        /// repo-relative file path.
        /// </summary>
        /// <param name="lfsLocksOutput">Raw stdout from <c>git lfs locks</c>.</param>
        /// <param name="currentUserName">Value of <c>git config user.name</c> for the local user.</param>
        public static Dictionary<string, LfsLockInfo> ParseLfsLocks(
            string lfsLocksOutput, string currentUserName)
        {
            var result = new Dictionary<string, LfsLockInfo>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(lfsLocksOutput))
                return result;

            // Expected line format:  <path>\t<owner>\tID:<id>
            foreach (var line in lfsLocksOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                var parts = trimmed.Split('\t');
                if (parts.Length < 3)
                    continue;

                var filePath  = parts[0].Trim().Replace('\\', '/');
                var ownerName = parts[1].Trim();
                var lockIdRaw = parts[2].Trim(); // "ID:123"
                var lockId    = lockIdRaw.StartsWith("ID:", StringComparison.OrdinalIgnoreCase)
                    ? lockIdRaw.Substring(3)
                    : lockIdRaw;

                result[filePath] = new LfsLockInfo
                {
                    FilePath    = filePath,
                    OwnerName   = ownerName,
                    LockId      = lockId,
                    IsOwnedByMe = string.Equals(ownerName, currentUserName,
                                                StringComparison.OrdinalIgnoreCase),
                };
            }

            return result;
        }

        // ── Merge helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Merges lock information into an existing list of file states, matching
        /// by relative path.
        /// </summary>
        public static void MergeLockInfo(
            List<FileState> fileStates,
            Dictionary<string, LfsLockInfo> locks)
        {
            foreach (var state in fileStates)
            {
                if (locks.TryGetValue(state.RelativePath, out var lockInfo))
                    state.LfsLock = lockInfo;
            }
        }
    }
}
