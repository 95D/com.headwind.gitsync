using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    public class GetRemoteUrlUseCase
    {
        private readonly IGitRepository _repository;

        public GetRemoteUrlUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<string> ExecuteAsync() => _repository.GetRemoteUrlAsync();
    }
}
