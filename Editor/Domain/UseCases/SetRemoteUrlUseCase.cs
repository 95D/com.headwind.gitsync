using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    public class SetRemoteUrlUseCase
    {
        private readonly IGitRepository _repository;

        public SetRemoteUrlUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<(bool success, string message)> ExecuteAsync(string url)
            => _repository.SetRemoteUrlAsync(url);
    }
}
