using System.Collections.Generic;
using System.Threading.Tasks;
using Headwind.GitSync.Domain.Interfaces;

namespace Headwind.GitSync.Domain.UseCases
{
    /// <summary>Lock된 파일만 add → commit → push 합니다.</summary>
    public class UploadUseCase
    {
        private readonly IGitRepository _repository;

        public UploadUseCase(IGitRepository repository)
        {
            _repository = repository;
        }

        public Task<(bool success, string log)> ExecuteAsync(
            string commitMessage, IEnumerable<string> lockedPaths)
            => _repository.UploadAsync(commitMessage, lockedPaths);
    }
}
