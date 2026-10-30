using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class ReadPullRequestBranch : Command
    {
        public ReadPullRequestBranch(string repository, string fullName)
        {
            if (!fullName.StartsWith("refs/heads/", StringComparison.Ordinal) || fullName.Contains('"') || fullName.Contains('\\') || fullName.Contains('\n') || fullName.Contains('\r'))
                throw new ArgumentException("Expected a local branch reference", nameof(fullName));
            WorkingDirectory = repository;
            Context = repository;
            RaiseError = false;
            Args = $"rev-parse --verify --end-of-options \"{fullName}\"";
        }

        public async Task<string> GetTipAsync()
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            if (!result.IsSuccess)
                throw new AzureDevOps.ServiceException("InvalidSource");
            return result.StdOut.Trim();
        }
    }
}
