using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    /// <summary>
    /// Executes the one-button sync workflow:
    /// git add . → git commit -m message → git pull --rebase → git push
    /// </summary>
    public class SyncUseCase
    {
        private readonly IGitRepository _repository;

        public SyncUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<(bool success, string log)> ExecuteAsync(string commitMessage)
        {
            return _repository.SyncAsync(commitMessage);
        }
    }
}
