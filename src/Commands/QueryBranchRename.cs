using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryBranchRename : Command
    {
        public QueryBranchRename(string repo, string reference)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"reflog show --no-show-signature --format=%gs --grep-reflog=\"^{Prefix}refs/heads/\" {reference.Quoted()} --";
            _reference = reference;
        }

        public async Task<string> GetSourceAsync(IReadOnlySet<string> missingRefs)
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            if (!result.IsSuccess)
                return null;

            using var entries = new StringReader(result.StdOut);
            var reference = _reference;
            while (entries.ReadLine() is { } entry)
            {
                var destination = $" to {reference}";
                if (!entry.StartsWith(Prefix, StringComparison.Ordinal) || !entry.EndsWith(destination, StringComparison.Ordinal))
                    break;

                reference = entry[Prefix.Length..^destination.Length];
                if (missingRefs.Contains(reference))
                    return reference;
            }

            return null;
        }

        private const string Prefix = "Branch: renamed ";
        private readonly string _reference;
    }
}
