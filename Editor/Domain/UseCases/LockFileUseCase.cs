using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    public class LockFileUseCase
    {
        private readonly IGitRepository _repository;

        public LockFileUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<(bool success, string message)> ExecuteAsync(string relativePath)
        {
            return _repository.LockFileAsync(relativePath);
        }
    }
}
