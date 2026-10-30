using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed record PullRequestBranchOrigin(string StartPoint, string CommitId);

    public sealed class ReadPullRequestBranchOrigin : Command
    {
        public ReadPullRequestBranchOrigin(string repository, string fullName)
        {
            if (!fullName.StartsWith("refs/heads/", StringComparison.Ordinal) || fullName.Contains('"') || fullName.Contains('\\') || fullName.Contains('\n') || fullName.Contains('\r'))
                throw new ArgumentException("Expected a local branch reference", nameof(fullName));
            WorkingDirectory = repository;
            Context = repository;
            RaiseError = false;
            Args = $"reflog show --format=%H%x09%gs --grep-reflog=\"^branch: Created from \" \"{fullName}\" --";
        }

        public async Task<PullRequestBranchOrigin> GetOriginAsync(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            var result = await ReadToEndAsync().ConfigureAwait(false);
            if (!result.IsSuccess)
                return null;
            var entries = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length != 1)
                return null;
            var fields = entries[0].TrimEnd('\r').Split('\t', 2);
            const string prefix = "branch: Created from ";
            if (fields.Length != 2 || fields[0].Length != 40 || !fields[0].All(Uri.IsHexDigit) || !fields[1].StartsWith(prefix, StringComparison.Ordinal))
                return null;
            return new(fields[1][prefix.Length..], fields[0]);
        }
    }
}
