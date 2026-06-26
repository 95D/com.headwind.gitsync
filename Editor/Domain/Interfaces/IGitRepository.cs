using System.Collections.Generic;
using System.Threading.Tasks;
using Headwind.GitSync.Data.Models;

namespace Headwind.GitSync.Domain.Interfaces
{
    /// <summary>
    /// Abstraction over all git/git-lfs operations needed by this tool.
    /// Implementations live in the Data layer; consumers live in the Domain/Presentation layers.
    /// </summary>
    public interface IGitRepository
    {
        Task<string> GetCurrentBranchAsync();
        Task<string> GetCurrentUserNameAsync();
        Task<List<FileState>> GetStatusAsync();

        /// <summary>
        /// Runs: git add . → git commit -m message → git pull --rebase → git push
        /// </summary>
        Task<(bool success, string log)> SyncAsync(string commitMessage);

        Task<(bool success, string message)> LockFileAsync(string relativePath);
        Task<(bool success, string message)> UnlockFileAsync(string relativePath);

        // ── Remote ────────────────────────────────────────────────────────────

        /// <summary>origin의 fetch URL을 반환합니다. 없으면 빈 문자열.</summary>
        Task<string> GetRemoteUrlAsync();

        /// <summary>
        /// origin이 없으면 add, 있으면 set-url을 실행합니다.
        /// </summary>
        Task<(bool success, string message)> SetRemoteUrlAsync(string url);
    }
}
