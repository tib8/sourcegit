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

            Models.Branch local = branch.IsLocal ? branch : null;
            Models.Branch source = branch.IsLocal ? null : branch;
            if (branch.IsLocal)
            {
                if (!string.IsNullOrEmpty(branch.Upstream))
                    source = repo.Branches.Find(x => !x.IsLocal && x.FullName == branch.Upstream);
                else
                {
                    // A normal push can publish a branch without configuring its upstream.
                    // Infer only an exact, unique name-and-tip match on a supported remote.
                    var candidates = repo.Branches.Where(x => !x.IsLocal && x.Name == branch.Name && SameTip(branch, x) &&
                        repo.Remotes.Exists(r => r.Name == x.Remote && AzureDevOps.RemoteAddress.TryParse(r.URL, out _))).ToList();
                    if (candidates.Count > 1)
                        return Disabled(owner, "CreatePR.SelectPublishedBranch");
                    source = candidates.SingleOrDefault();
                }

                if (source == null)
                    return repo.Remotes.Exists(x => AzureDevOps.RemoteAddress.TryParse(x.URL, out _)) ? Disabled(owner, "CreatePR.PushFirst") : null;
            }

            var remote = repo.Remotes.Find(x => x.Name == source.Remote);
            if (remote == null || !AzureDevOps.RemoteAddress.TryParse(remote.URL, out _))
                return null;

            local ??= repo.Branches.Find(x => x.IsLocal && x.Upstream == source.FullName) ??
                repo.Branches.Find(x => x.IsLocal && string.IsNullOrEmpty(x.Upstream) && x.Name == source.Name);
            if (local != null && !SameTip(local, source))
                return Disabled(owner, "CreatePR.PushFirst");

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

        private static MenuItem Disabled(Control owner, string reason) => new()
        {
            Header = App.Text(reason),
            Icon = owner.CreateMenuIcon("Icons.CreatePR"),
            IsEnabled = false,
            Name = "CreateAzureDevOpsPullRequest",
        };
    }
}
