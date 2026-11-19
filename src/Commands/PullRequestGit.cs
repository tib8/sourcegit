using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class PullRequestGit(string repository, Models.Remote remote)
    {
        public async Task<List<string>> ReadMessagesAsync(string sourceTip, string targetRef, string targetTip, CancellationToken cancellationToken)
        {
            ValidateTip(sourceTip);
            ValidateTip(targetTip);
            ValidateRef(targetRef);
            var probe = await new Read(repository, $"cat-file -e {targetTip}^{{commit}}", cancellationToken).GetAsync().ConfigureAwait(false);
            if (!probe.IsSuccess)
            {
                await ValidateDestinationAsync(false, cancellationToken).ConfigureAwait(false);
                // Obtain the selected target's objects without changing a local branch or tracking settings.
                var fetch = MakeCommand($"fetch --no-tags --no-write-fetch-head -- {remote.Name.Quoted()} {targetRef.Quoted()}", cancellationToken);
                if (!await fetch.ExecAsync().ConfigureAwait(false))
                    throw new AzureDevOps.ServiceException("LocalCommitsUnavailable");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await new Read(repository, $"log --no-show-signature --no-decorate --no-color --no-notes --reverse --topo-order --format=%B%x00 {targetTip}..{sourceTip} --", cancellationToken).GetAsync().ConfigureAwait(false);
            if (!result.IsSuccess)
                throw new AzureDevOps.ServiceException("LocalCommitsUnavailable");
            return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        public async Task<bool> PushAsync(string sourceTip, string sourceRef, bool checkSubmodules, Models.ICommandLog log, CancellationToken cancellationToken)
        {
            ValidateTip(sourceTip);
            ValidateRef(sourceRef);
            await ValidateDestinationAsync(true, cancellationToken).ConfigureAwait(false);
            // Pin the reviewed commit; never force, push tags, or modify branch tracking.
            var command = MakeCommand($"push --progress --verbose --no-follow-tags --no-mirror --no-force {(checkSubmodules ? "--recurse-submodules=check " : string.Empty)}-- {remote.Name.Quoted()} {(sourceTip + ":" + sourceRef).Quoted()}", cancellationToken);
            command.Log = log;
            var succeeded = await command.ExecAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return succeeded;
        }

        private Command MakeCommand(string args, CancellationToken cancellationToken) => new()
        {
            WorkingDirectory = repository,
            Context = repository,
            SSHKey = remote.PrivateSSHKey,
            Args = args,
            RaiseError = false,
            CancellationToken = cancellationToken,
        };

        private async Task ValidateDestinationAsync(bool push, CancellationToken cancellationToken)
        {
            var result = await new Read(repository, $"remote get-url {(push ? "--push " : string.Empty)}--all {remote.Name.Quoted()}", cancellationToken).GetAsync().ConfigureAwait(false);
            var urls = result.StdOut?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            var expected = push && !string.IsNullOrEmpty(remote.PushURL) ? remote.PushURL : remote.URL;
            if (!result.IsSuccess || urls.Length != 1)
                throw new AzureDevOps.ServiceException("AmbiguousRemote");
            var same = AzureDevOps.RemoteAddress.TryParse(expected, out var address)
                ? AzureDevOps.RemoteAddress.TryParse(urls[0], out var actual) && actual.Key == address.Key
                : string.Equals(urls[0], expected, StringComparison.Ordinal);
            if (!same)
                throw new AzureDevOps.ServiceException("AmbiguousRemote");
        }

        private static void ValidateTip(string value)
        {
            if (value == null || (value.Length != 40 && value.Length != 64) || !value.All(Uri.IsHexDigit))
                throw new AzureDevOps.ServiceException("InvalidSource");
        }

        private static void ValidateRef(string value)
        {
            if (value == null || !value.StartsWith("refs/heads/", StringComparison.Ordinal) || value.IndexOfAny(['"', '\\', '\n', '\r']) >= 0)
                throw new AzureDevOps.ServiceException("InvalidSource");
        }

        private sealed class Read : Command
        {
            public Read(string repository, string args, CancellationToken cancellationToken)
            {
                WorkingDirectory = repository;
                Context = repository;
                Args = args;
                RaiseError = false;
                CancellationToken = cancellationToken;
            }

            public Task<Result> GetAsync() => ReadToEndAsync();
        }
    }
}
