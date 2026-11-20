using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class StashHistoryEntry(Models.Stash stash) : ObservableObject
    {
        public Models.Stash Stash { get; } = stash;
        public double LeftMargin { get => _leftMargin; set => SetProperty(ref _leftMargin, value); }

        private double _leftMargin = 24;
    }
}
