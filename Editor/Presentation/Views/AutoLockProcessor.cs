using System.Collections.Generic;
using Headwind.GitSync.Data;
using UnityEditor;
using UnityEngine;

namespace Headwind.GitSync.Presentation.Views
{
    /// <summary>
    /// Unity 에디터에서 파일이 저장되기 직전에 호출되는 훅.
    /// LFS 추적 파일에 대해 자동 Lock을 시도하고,
    /// 타인이 Lock 중인 경우 저장 자체를 차단합니다.
    /// </summary>
    public class AutoLockProcessor : UnityEditor.AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            var repoRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            var allowed  = new List<string>();

            foreach (var path in paths)
            {
                // .meta 파일은 항상 텍스트 — LFS 대상 아님
                if (path.EndsWith(".meta"))
                {
                    allowed.Add(path);
                    continue;
                }

                // LFS 추적 여부 확인 (동기, git check-attr 호출)
                if (!IsLfsTracked(repoRoot, path))
                {
                    allowed.Add(path);
                    continue;
                }

                // 캐시에서 Lock 상태 조회
                if (GitLockCache.TryGetLock(path, out var lockInfo))
                {
                    if (!lockInfo.IsOwnedByMe)
                    {
                        // 타인이 Lock 중 → 저장 차단
                        EditorUtility.DisplayDialog(
                            "GitSync — 저장 불가",
                            $"{System.IO.Path.GetFileName(path)}\n\n" +
                            $"[{lockInfo.OwnerName}] 이(가) 편집 중입니다.\n" +
                            "저장할 수 없습니다.",
                            "확인");
                        continue;
                    }
                    // 내가 Lock 중 → 허용
                    allowed.Add(path);
                }
                else
                {
                    // Lock 없음 → 자동 Lock 시도 (동기)
                    var result = GitProcessUtility.RunBlocking(repoRoot, $"lfs lock \"{path}\"");
                    if (result.IsSuccess)
                    {
                        allowed.Add(path);
                    }
                    else
                    {
                        // Lock 실패 (Remote 미설정 등) → 경고 후 사용자가 선택
                        bool save = EditorUtility.DisplayDialog(
                            "GitSync — Lock 실패",
                            $"파일을 Lock하는 데 실패했습니다:\n{result.Stderr}\n\n그래도 저장하시겠습니까?",
                            "저장", "취소");
                        if (save) allowed.Add(path);
                    }
                }
            }

            return allowed.ToArray();
        }

        /// <summary>
        /// git check-attr 를 동기 실행하여 LFS 추적 여부를 반환합니다.
        /// </summary>
        static bool IsLfsTracked(string repoRoot, string path)
        {
            var result = GitProcessUtility.RunBlocking(repoRoot, $"check-attr filter -- \"{path}\"");
            return result.IsSuccess && result.Stdout.Contains(": filter: lfs");
        }
    }
}
