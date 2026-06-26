namespace Headwind.GitSync.Data.Models
{
    /// <summary>
    /// Represents an active Git LFS file lock.
    /// </summary>
    public class LfsLockInfo
    {
        /// <summary>Numeric lock ID assigned by the LFS server.</summary>
        public string LockId { get; set; }

        /// <summary>Repo-relative path of the locked file (forward slashes).</summary>
        public string FilePath { get; set; }

        /// <summary>Display name of the user who owns the lock.</summary>
        public string OwnerName { get; set; }

        /// <summary>True when the lock is owned by the local git user.</summary>
        public bool IsOwnedByMe { get; set; }
    }
}
