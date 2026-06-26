using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    /// <summary>git pull --rebase 로 원격 변경사항을 가져옵니다.</summary>
    public class FetchUseCase
    {
        private readonly IGitRepository _repository;

        public FetchUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<(bool success, string log)> ExecuteAsync()
            => _repository.FetchAsync();
    }
}
