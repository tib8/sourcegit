using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public partial class Histories
    {
        public bool CanNavigateBack => !_isResolvingNavigation && (_navigationHistory.CanGoBack || HasUnrecordedNavigationSelection);
        public bool CanNavigateForward => !_isResolvingNavigation && _navigationHistory.CanGoForward;

        public async Task NavigateHomeAsync()
        {
            CancelNavigationVisit();
            if (!_repo.IsBare && (Preferences.Instance.WorkingDirectoryHeadMode == 2 ||
                (Preferences.Instance.WorkingDirectoryHeadMode == 1 && WorkingDirectory.HasChanges)))
            {
                IsQuickSearchFilterEnabled = false;
                CancelPendingHeadSelection();
                SelectHistoryEntries([], true);
                return;
            }

            var request = _navigationRequest;
            // Resolve HEAD in this worktree, even if the loaded history omits it
            // or its decorations belong to an earlier checkout.
            var commit = await GetCommitAsync("HEAD");
            if (request != _navigationRequest)
                return;

            if (commit == null && _commits.Count > 0)
                commit = _commits[0];
            if (commit != null)
                SelectedCommits = [EnsureNavigationCommitVisible(commit)];
        }

        public Task NavigateBackAsync() => NavigateHistoryAsync(-1);
        public Task NavigateForwardAsync() => NavigateHistoryAsync(1);

        public async Task RevealStashAsync(Models.Stash stash)
        {
            if (TrySelectStash(stash))
                return;

            CancelNavigationVisit();
            var request = _navigationRequest;
            Models.Commit parent = null;
            if (stash.Parents.Count > 0)
            {
                var sha = stash.Parents[0];
                parent = _commits.Find(commit => commit.SHA == sha) ?? await GetCommitAsync(sha);
            }

            if (request != _navigationRequest || !_repo.IsDashboardVisible || !_repo.ShowStashes)
                return;

            var current = _repo.StashesPage.Stashes.Find(entry => entry.Name == stash.Name && entry.SHA == stash.SHA);
            if (current == null)
                return;

            if (parent != null)
            {
                _revealedStashParent = parent;
                RefreshStashRows();
                if (TrySelectStash(current))
                    return;
            }

            _repo.OpenStashPage(current);
        }

        public void ResumeNavigationVisits()
        {
            _isNavigationActive = true;
            ScheduleNavigationVisit();
        }

        public void SuspendNavigationVisits()
        {
            _isNavigationActive = false;
            CancelNavigationVisit();
        }

        public void CancelNavigationVisit()
        {
            _navigationRequest++;
            _navigationVisitTimer?.Stop();
        }

        private void ScheduleNavigationVisit()
        {
            CancelNavigationVisit();
            NotifyNavigationChanged();
            if (!_isNavigationActive || _isNavigatingHistory || _ignoreSelectionChange || _updatingSelection ||
                IsWorkingDirectorySelected || _selectedCommits.Count != 1)
                return;

            if (_navigationVisitTimer == null)
            {
                _navigationVisitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _navigationVisitTimer.Tick += (_, _) =>
                {
                    _navigationVisitTimer.Stop();
                    if (_selectedCommits.Count == 1)
                    {
                        _navigationHistory.Visit(_selectedCommits[0].SHA);
                        NotifyNavigationChanged();
                    }
                };
            }

            _navigationVisitTimer.Start();
        }

        private async Task NavigateHistoryAsync(int direction)
        {
            if (direction < 0 ? !CanNavigateBack : !CanNavigateForward)
                return;

            CancelNavigationVisit();
            var request = _navigationRequest;
            _isResolvingNavigation = true;
            NotifyNavigationChanged();
            try
            {
                while (true)
                {
                    var returnToCurrent = direction < 0 && HasUnrecordedNavigationSelection;
                    var sha = direction < 0 ?
                        returnToCurrent ? _navigationHistory.Current : _navigationHistory.Previous :
                        _navigationHistory.Next;
                    if (sha == null)
                        return;

                    var commit = _commits.Find(c => c.SHA == sha);
                    if (commit == null)
                    {
                        commit = await GetCommitAsync(sha);
                        if (request != _navigationRequest)
                            return;
                        if (commit == null)
                        {
                            // A rewritten, unreachable commit may have been pruned by Git.
                            _navigationHistory.Forget(sha);
                            continue;
                        }
                    }

                    if (!returnToCurrent)
                    {
                        if (direction < 0)
                            _navigationHistory.Back();
                        else
                            _navigationHistory.Forward();
                    }

                    _isNavigatingHistory = true;
                    SelectedCommits = [EnsureNavigationCommitVisible(commit)];
                    return;
                }
            }
            finally
            {
                _isNavigatingHistory = false;
                _isResolvingNavigation = false;
                NotifyNavigationChanged();
            }
        }

        private void NotifyNavigationChanged()
        {
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
        }

        private Models.Commit EnsureNavigationCommitVisible(Models.Commit commit)
        {
            if (IsQuickSearchFilterActive && !_displayedCommits.Exists(c => c.SHA == commit.SHA))
                IsQuickSearchFilterEnabled = false;

            var existing = _commits.Find(c => c.SHA == commit.SHA);
            if (existing != null)
                return existing;

            Commits = InsertNavigationCommit(_commits, commit);
            return commit;
        }

        private static List<Models.Commit> InsertNavigationCommit(List<Models.Commit> source, Models.Commit commit)
        {
            var commits = new List<Models.Commit>(source);
            // Keep children above their parents when restoring an omitted row.
            var parentIndex = commits.FindIndex(c => commit.Parents.Contains(c.SHA));
            commits.Insert(parentIndex < 0 ? commits.Count : parentIndex, commit);
            return commits;
        }

        private bool HasUnrecordedNavigationSelection => _navigationHistory.Current != null &&
            (_selectedCommits.Count != 1 || _selectedCommits[0].SHA != _navigationHistory.Current);

        private readonly Models.CommitNavigationHistory _navigationHistory = new();
        private DispatcherTimer _navigationVisitTimer;
        private bool _isNavigatingHistory;
        private bool _isResolvingNavigation;
        private bool _isNavigationActive;
        private int _navigationRequest;
    }
}
