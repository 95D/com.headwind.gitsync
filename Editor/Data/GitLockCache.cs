using System;
using System.Collections.Generic;
using Headwind.GitSync.Data.Models;

namespace Headwind.GitSync.Data
{
    /// <summary>
    /// 컴포넌트 간 공유하는 LFS Lock 상태 인메모리 캐시.
    /// GitRepository.GetStatusAsync() 호출 시 갱신되며,
    /// AutoLockProcessor 가 저장 시점에 동기적으로 읽어 사용합니다.
    /// </summary>
    public static class GitLockCache
    {
        static Dictionary<string, LfsLockInfo> _locks
            = new Dictionary<string, LfsLockInfo>(StringComparer.OrdinalIgnoreCase);

        public static string CurrentUserName { get; private set; } = string.Empty;

        /// <summary>
        /// GetStatusAsync() 가 파싱한 최신 lock 목록으로 캐시를 교체합니다.
        /// </summary>
        public static void Update(Dictionary<string, LfsLockInfo> locks, string userName)
        {
            _locks = new Dictionary<string, LfsLockInfo>(locks, StringComparer.OrdinalIgnoreCase);
            CurrentUserName = userName;
        }

        public static bool TryGetLock(string path, out LfsLockInfo info)
            => _locks.TryGetValue(Normalize(path), out info);

        public static bool IsLockedByMe(string path)
            => _locks.TryGetValue(Normalize(path), out var info) && info.IsOwnedByMe;

        public static bool IsLockedByOther(string path)
            => _locks.TryGetValue(Normalize(path), out var info) && !info.IsOwnedByMe;

        /// <summary>현재 캐시 기준으로 내가 Lock한 파일 경로 목록을 반환합니다.</summary>
        public static List<string> GetMyLockedPaths()
            => _locks.Where(kvp => kvp.Value.IsOwnedByMe)
                     .Select(kvp => kvp.Key)
                     .ToList();

        static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    }
}
