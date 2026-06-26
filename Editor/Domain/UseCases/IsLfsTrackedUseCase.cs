using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    /// <summary>
    /// .gitattributes 기준으로 파일이 Git LFS 추적 대상인지 확인합니다.
    /// </summary>
    public class IsLfsTrackedUseCase
    {
        private readonly IGitRepository _repository;

        public IsLfsTrackedUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<bool> ExecuteAsync(string relativePath)
            => _repository.IsLfsTrackedAsync(relativePath);
    }
}
