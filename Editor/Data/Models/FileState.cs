namespace Headwind.GitSync.Data.Models
{
    public enum FileChangeStatus
    {
        Modified,
        Added,
        Deleted,
        Renamed,
        Untracked,
    }

    /// <summary>
    /// Combined view of a file's git working-tree status and its LFS lock state.
    /// </summary>
    public class FileState
    {
        /// <summary>Repo-relative path (forward slashes).</summary>
        public string RelativePath { get; set; }

        /// <summary>How the file was changed relative to HEAD.</summary>
        public FileChangeStatus ChangeStatus { get; set; }

        /// <summary>
        /// Active LFS lock on this file, or null when the file is not tracked by LFS
        /// or has no lock.
        /// </summary>
        public LfsLockInfo LfsLock { get; set; }

        /// <summary>Convenience: true when any lock exists on this file.</summary>
        public bool IsLocked => LfsLock != null;

        /// <summary>Convenience: true when the local user holds the lock.</summary>
        public bool IsLockedByMe => LfsLock?.IsOwnedByMe ?? false;
    }
}
