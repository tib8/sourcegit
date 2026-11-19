using System;
using System.Linq;

using Avalonia.Controls;

namespace SourceGit.Views
{
    public static class AzureDevOpsPullRequestMenu
    {
        public static MenuItem Create(Control owner, ViewModels.Repository repo, Models.Branch branch)
        {
            if (branch == null || branch.IsDetachedHead || !branch.IsHeadAuthoredByUser)
                return null;

            if (!branch.IsLocal)
            {
                var remote = repo.Remotes.Find(x => x.Name == branch.Remote);
                if (remote == null || !AzureDevOps.RemoteAddress.TryParse(remote.URL, out _))
                    return null;
                // A remote-branch action uses the published revision, not unrelated local work.
                var local = repo.Branches.Find(x => x.IsLocal && SameTip(x, branch) &&
                    (x.Upstream == branch.FullName || (string.IsNullOrEmpty(x.Upstream) && x.Name == branch.Name)));
                return CreateItem(owner, repo, remote, branch, local);
            }

            if (!string.IsNullOrEmpty(branch.Upstream))
            {
                var remote = repo.Remotes.OrderByDescending(x => x.Name.Length)
                    .FirstOrDefault(x => branch.Upstream.StartsWith($"refs/remotes/{x.Name}/", StringComparison.Ordinal));
                if (remote == null || !AzureDevOps.RemoteAddress.TryParse(remote.URL, out _))
                    return null;
                var name = branch.Upstream[($"refs/remotes/{remote.Name}/").Length..];
                return CreateItem(owner, repo, remote, Destination(repo, remote, name), branch);
            }

            var remotes = repo.Remotes.Where(x => AzureDevOps.RemoteAddress.TryParse(x.URL, out _)).ToList();
            var matching = remotes.Where(x => repo.Branches.Exists(b => !b.IsLocal && b.Remote == x.Name && b.Name == branch.Name)).ToList();
            if (matching.Count > 0)
                remotes = matching;
            var preferred = remotes.Find(x => x.Name == repo.Settings.DefaultRemote);
            if (preferred != null)
                remotes = [preferred];
            if (remotes.Count == 0)
                return null;
            if (remotes.Count == 1)
                return CreateItem(owner, repo, remotes[0], Destination(repo, remotes[0], branch.Name), branch);

            var choices = new MenuItem { Header = App.Text("BranchCM.CreatePR"), Icon = owner.CreateMenuIcon("Icons.CreatePR"), Name = "CreateAzureDevOpsPullRequest" };
            foreach (var remote in remotes)
            {
                var item = CreateItem(owner, repo, remote, Destination(repo, remote, branch.Name), branch);
                item.Header = $"{remote.Name}/{branch.Name}";
                choices.Items.Add(item);
            }
            return choices;
        }

        private static Models.Branch Destination(ViewModels.Repository repo, Models.Remote remote, string name) =>
            repo.Branches.Find(x => !x.IsLocal && x.Remote == remote.Name && x.Name == name) ??
            new() { Name = name, FullName = $"refs/remotes/{remote.Name}/{name}", Remote = remote.Name };

        private static MenuItem CreateItem(Control owner, ViewModels.Repository repo, Models.Remote remote, Models.Branch source, Models.Branch local)
        {
            var item = new MenuItem
            {
                Header = App.Text("BranchCM.CreatePRForUpstream", source.FriendlyName),
                Icon = owner.CreateMenuIcon("Icons.CreatePR"),
                Name = "CreateAzureDevOpsPullRequest",
            };
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                await owner.ShowDialogAsync(new ViewModels.CreatePullRequest(repo, remote, source, local));
            };
            return item;
        }

        private static bool SameTip(Models.Branch local, Models.Branch remote) => !string.IsNullOrEmpty(local.Head) &&
            string.Equals(local.Head, remote.Head, StringComparison.OrdinalIgnoreCase);

    }
}
