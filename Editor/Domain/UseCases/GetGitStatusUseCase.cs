using System.Collections.Generic;
using System.Threading.Tasks;
using Headwind.GitSync.Data.Models;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    /// <summary>
    /// Fetches the combined git status + LFS lock state and the current branch name.
    /// </summary>
    public class GetGitStatusUseCase
    {
        private readonly IGitRepository _repository;

        public GetGitStatusUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public async Task<(string branch, List<FileState> files)> ExecuteAsync()
        {
            var branchTask = _repository.GetCurrentBranchAsync();
            var filesTask  = _repository.GetStatusAsync();

            await Task.WhenAll(branchTask, filesTask);

            return (branchTask.Result, filesTask.Result);
        }
    }
}
