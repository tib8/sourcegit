using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public class HistoriesLayout : Grid
    {
        public static readonly DirectProperty<HistoriesLayout, bool> UseHorizontalProperty =
            AvaloniaProperty.RegisterDirect<HistoriesLayout, bool>(
                nameof(UseHorizontal),
                static o => o.UseHorizontal,
                static (o, v) => o.UseHorizontal = v);

        public bool UseHorizontal
        {
            get => _useHorizontal;
            set => SetAndRaise(UseHorizontalProperty, ref _useHorizontal, value);
        }

        protected override Type StyleKeyOverride => typeof(Grid);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == UseHorizontalProperty && IsLoaded)
                RefreshLayout();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RefreshLayout();
        }

        private void RefreshLayout()
        {
            if (UseHorizontal)
            {
                var rowSpan = RowDefinitions.Count;
                for (int i = 0; i < Children.Count; i++)
                {
                    var child = Children[i];
                    child.SetValue(RowProperty, 0);
                    child.SetValue(RowSpanProperty, rowSpan);
                    child.SetValue(ColumnProperty, i);
                    child.SetValue(ColumnSpanProperty, 1);

                    if (child is GridSplitter splitter)
                        splitter.BorderThickness = new Thickness(1, 0, 0, 0);
                }
            }
            else
            {
                var colSpan = ColumnDefinitions.Count;
                for (int i = 0; i < Children.Count; i++)
                {
                    var child = Children[i];
                    child.SetValue(RowProperty, i);
                    child.SetValue(RowSpanProperty, 1);
                    child.SetValue(ColumnProperty, 0);
                    child.SetValue(ColumnSpanProperty, colSpan);

                    if (child is GridSplitter splitter)
                        splitter.BorderThickness = new Thickness(0, 1, 0, 0);
                }
            }
        }

        private bool _useHorizontal = false;
    }

    public class HistoriesCommitList : DataGrid
    {
        public static readonly DirectProperty<HistoriesCommitList, int> TotalCommitsProperty =
            AvaloniaProperty.RegisterDirect<HistoriesCommitList, int>(
                nameof(TotalCommits),
                static o => o.TotalCommits,
                static (o, v) => o.TotalCommits = v);

        public int TotalCommits
        {
            get => _totalCommits;
            set => SetAndRaise(TotalCommitsProperty, ref _totalCommits, value);
        }

        public static readonly DirectProperty<HistoriesCommitList, List<Models.Commit>> SelectedCommitsProperty =
            AvaloniaProperty.RegisterDirect<HistoriesCommitList, List<Models.Commit>>(
                nameof(SelectedCommits),
                static o => o.SelectedCommits,
                static (o, v) => o.SelectedCommits = v);

        public List<Models.Commit> SelectedCommits
        {
            get => _selectedCommits;
            set => SetAndRaise(SelectedCommitsProperty, ref _selectedCommits, value);
        }

        protected override Type StyleKeyOverride => typeof(DataGrid);

        public HistoriesCommitList()
        {
            SelectionMode = DataGridSelectionMode.Extended;
            CanUserReorderColumns = true;
            CanUserResizeColumns = false;
            CanUserSortColumns = false;
            AutoGenerateColumns = false;
            IsReadOnly = true;
            HeadersVisibility = DataGridHeadersVisibility.Column;
            ClipboardCopyMode = DataGridClipboardCopyMode.None;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            ApplySelection();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SelectedCommitsProperty && IsLoaded && !_ignoreSelectionChanged)
            {
                ApplySelection();
                if (change.NewValue is List<Models.Commit> { Count: 1 } selected)
                    ScrollIntoView(selected[0], null);
                else if (DataContext is ViewModels.Histories { IsWorkingDirectorySelected: true } histories)
                    ScrollIntoView(histories.WorkingDirectory, null);
            }
        }

        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            base.OnSelectionChanged(e);

            if (_ignoreSelectionChanged || DataContext is ViewModels.Histories { IsUpdatingEntries: true } or ViewModels.Histories { IsUpdatingQuickSearchFilter: true })
                return;

            if (ItemsSource is not IList<object> items)
                return;

            if (DataContext is ViewModels.Histories vm)
            {
                vm.CancelPendingHeadSelection();
                var containsWorking = SelectedItems.Contains(vm.WorkingDirectory);
                if (containsWorking && SelectedItems.Count > 2)
                {
                    _ignoreSelectionChanged = true;
                    SelectedItems.Remove(vm.WorkingDirectory);
                    _ignoreSelectionChanged = false;
                }
            }

            var commits = new List<Models.Commit>();
            foreach (var o in SelectedItems)
            {
                if (o is Models.Commit c)
                    commits.Add(c);
            }

            if (e.AddedItems.Count == 1)
            {
                ScrollIntoView(e.AddedItems[0], null);
            }
            else if (e.AddedItems.Count > 1 && e.AddedItems[0] is Models.Commit first)
            {
                var firstIndex = items.IndexOf(first);
                if (firstIndex > 0)
                {
                    var prev = items[firstIndex - 1];
                    if (prev is Models.Commit previous && commits.Contains(previous))
                        ScrollIntoView(e.AddedItems[^1], null);
                    else
                        ScrollIntoView(first, null);
                }
            }

            if (!_ignoreSelectionChanged)
            {
                _ignoreSelectionChanged = true;

                if (DataContext is ViewModels.Histories histories)
                    histories.SelectHistoryEntries(commits, SelectedItems.Contains(histories.WorkingDirectory));
                else
                    SelectedCommits = commits;

                _ignoreSelectionChanged = false;
            }
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyModifiers == KeyModifiers.Alt)
            {
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    if (this.FindAncestorOfType<Histories>() is { } histories)
                        await histories.GotoChild();
                }
                else if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    if (this.FindAncestorOfType<Histories>() is { } histories)
                        await histories.GotoParent();
                }
            }
            else if (e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) &&
                e.Key == Key.A && DataContext is ViewModels.Histories histories)
            {
                histories.SelectedCommits = new List<Models.Commit>(histories.DisplayedCommits);
                e.Handled = true;
            }
            else if (e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) &&
                SelectedItems is { Count: > 0 } selected &&
                e.Key == Key.C)
            {
                var builder = new StringBuilder();
                foreach (var item in selected)
                {
                    if (item is Models.Commit commit)
                        builder.Append(commit.SHA.AsSpan(0, 10)).Append(" - ").AppendLine(commit.Subject);
                }

                e.Handled = true;
                if (builder.Length > 0)
                    await this.CopyTextAsync(builder.ToString());
            }

            if (!e.Handled)
                base.OnKeyDown(e);
        }

        private void ApplySelection()
        {
            _ignoreSelectionChanged = true;
            SelectedItems.Clear();
            if (DataContext is ViewModels.Histories { IsWorkingDirectorySelected: true } vm)
                SelectedItems.Add(vm.WorkingDirectory);
            if (SelectedCommits != null)
            {
                foreach (var c in SelectedCommits)
                    SelectedItems.Add(c);
            }
            _ignoreSelectionChanged = false;
        }

        private bool _ignoreSelectionChanged = false;
        private int _totalCommits = 0;
        private List<Models.Commit> _selectedCommits = [];
    }

    public partial class Histories : UserControl
    {
        internal Grid SidebarHost => HistoryArea;

        public static readonly DirectProperty<Histories, Models.Branch> CurrentBranchProperty =
            AvaloniaProperty.RegisterDirect<Histories, Models.Branch>(
                nameof(CurrentBranch),
                static o => o.CurrentBranch,
                static (o, v) => o.CurrentBranch = v);

        public Models.Branch CurrentBranch
        {
            get => _currentBranch;
            set => SetAndRaise(CurrentBranchProperty, ref _currentBranch, value);
        }

        public static readonly DirectProperty<Histories, Models.Bisect> BisectProperty =
            AvaloniaProperty.RegisterDirect<Histories, Models.Bisect>(
                nameof(Bisect),
                static o => o.Bisect,
                static (o, v) => o.Bisect = v);

        public Models.Bisect Bisect
        {
            get => _bisect;
            set => SetAndRaise(BisectProperty, ref _bisect, value);
        }

        public static readonly DirectProperty<Histories, bool> HasSingleRemoteProperty =
            AvaloniaProperty.RegisterDirect<Histories, bool>(
                nameof(HasSingleRemote),
                static o => o.HasSingleRemote,
                static (o, v) => o.HasSingleRemote = v);

        public bool HasSingleRemote
        {
            get => _hasSingleRemote;
            set => SetAndRaise(HasSingleRemoteProperty, ref _hasSingleRemote, value);
        }

        public static readonly DirectProperty<Histories, AvaloniaList<Models.IssueTracker>> IssueTrackersProperty =
            AvaloniaProperty.RegisterDirect<Histories, AvaloniaList<Models.IssueTracker>>(
                nameof(IssueTrackers),
                static o => o.IssueTrackers,
                static (o, v) => o.IssueTrackers = v);

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get => _issueTrackers;
            set => SetAndRaise(IssueTrackersProperty, ref _issueTrackers, value);
        }

        public static readonly DirectProperty<Histories, bool> IsScrollToTopVisibleProperty =
            AvaloniaProperty.RegisterDirect<Histories, bool>(
                nameof(IsScrollToTopVisible),
                static o => o.IsScrollToTopVisible,
                static (o, v) => o.IsScrollToTopVisible = v);

        public bool IsScrollToTopVisible
        {
            get => _isScrollToTopVisible;
            set => SetAndRaise(IsScrollToTopVisibleProperty, ref _isScrollToTopVisible, value);
        }

        public static readonly DirectProperty<Histories, bool> IsDetailsPanelExpandedProperty =
            AvaloniaProperty.RegisterDirect<Histories, bool>(
                nameof(IsDetailsPanelExpanded),
                static o => o.IsDetailsPanelExpanded,
                static (o, v) => o.IsDetailsPanelExpanded = v);

        public bool IsDetailsPanelExpanded
        {
            get => _isDetailsPanelExpanded;
            set => SetAndRaise(IsDetailsPanelExpandedProperty, ref _isDetailsPanelExpanded, value);
        }

        public Histories()
        {
            InitializeComponent();
            CommitListContainer.Columns[1].PropertyChanged += OnAuthorColumnPropertyChanged;
            CommitListContainer.Columns[5].PropertyChanged += OnBranchesColumnPropertyChanged;
            AddHandler(KeyDownEvent, OnQuickSearchKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Tunnel);
            Loaded += (_, _) => (DataContext as ViewModels.Histories)?.ResumeNavigationVisits();
            Unloaded += (_, _) => (DataContext as ViewModels.Histories)?.SuspendNavigationVisits();
            Loaded += OnQuickSearchLoaded;
            Unloaded += OnQuickSearchUnloaded;
        }

        public void FocusQuickSearch()
        {
            if (TopLevel.GetTopLevel(this) is Window { IsActive: false })
                return;

            QuickSearchBox.Focus();
            QuickSearchBox.SelectAll();
        }

        private void OnQuickSearchLoaded(object sender, RoutedEventArgs e)
        {
            (DataContext as ViewModels.Histories)?.ResumeQuickSearch();
        }

        private void OnQuickSearchUnloaded(object sender, RoutedEventArgs e)
        {
            (DataContext as ViewModels.Histories)?.CancelQuickSearch();
        }

        private void OnQuickSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.Histories vm)
                return;

            if (!QuickSearchBox.IsKeyboardFocusWithin &&
                e.Source is Visual source && source != CommitListContainer && !CommitListContainer.IsVisualAncestorOf(source))
                return;

            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.F && e.KeyModifiers == command)
            {
                FocusQuickSearch();
                e.Handled = true;
            }
            else if ((e.Key == Key.F3 && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift)) ||
                     (e.Key == Key.Enter && QuickSearchBox.IsKeyboardFocusWithin &&
                      (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift)))
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    vm.QuickSearchPrevious();
                else
                    vm.QuickSearchNext();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (!string.IsNullOrEmpty(vm.QuickSearchText) || vm.IsQuickSearchFilterEnabled))
            {
                vm.ClearQuickSearch();
                CommitListContainer.Focus();
                e.Handled = true;
            }
        }

        private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.Histories vm)
                return;

            var macCommand = OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta;
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Home)
            {
                e.Handled = true;
                await vm.NavigateHomeAsync();
            }
            else if ((e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left) || (macCommand && e.Key == Key.OemOpenBrackets))
            {
                e.Handled = true;
                await vm.NavigateBackAsync();
            }
            else if ((e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right) || (macCommand && e.Key == Key.OemCloseBrackets))
            {
                e.Handled = true;
                await vm.NavigateForwardAsync();
            }
        }

        public async Task GotoParent()
        {
            if (DataContext is not ViewModels.Histories vm)
                return;

            if (!CommitListContainer.IsKeyboardFocusWithin)
                return;

            if (CommitListContainer.SelectedItems is not { Count: 1 } selected)
                return;

            if (selected[0] is not Models.Commit { Parents.Count: > 0 } commit)
                return;

            if (commit.Parents.Count == 1)
            {
                vm.NavigateTo(commit.Parents[0]);
                return;
            }

            var parents = new List<Models.Commit>();
            foreach (var sha in commit.Parents)
            {
                var c = await vm.GetCommitAsync(sha);
                if (c != null)
                    parents.Add(c);
            }

            if (parents.Count == 1)
            {
                vm.NavigateTo(parents[0].SHA);
            }
            else if (parents.Count > 1 && TopLevel.GetTopLevel(this) is Window owner)
            {
                var dialog = new GotoRevisionSelector();
                dialog.RevisionList.ItemsSource = parents;

                var c = await dialog.ShowDialog<Models.Commit>(owner);
                if (c != null)
                    vm.NavigateTo(c.SHA);
            }
        }

        public async Task GotoChild()
        {
            if (DataContext is not ViewModels.Histories vm)
                return;

            if (!CommitListContainer.IsKeyboardFocusWithin)
                return;

            if (CommitListContainer.SelectedItems is not { Count: 1 } selected)
                return;

            if (selected[0] is not Models.Commit { Parents.Count: > 0 } commit)
                return;

            var children = new List<Models.Commit>();
            var sha = commit.SHA;
            foreach (var c in vm.Commits)
            {
                foreach (var p in c.Parents)
                {
                    if (sha.StartsWith(p, StringComparison.Ordinal))
                        children.Add(c);
                }

                if (sha.Equals(c.SHA, StringComparison.Ordinal))
                    break;
            }

            if (children.Count == 1)
            {
                vm.NavigateTo(children[0].SHA);
            }
            else if (children.Count > 1 && TopLevel.GetTopLevel(this) is Window owner)
            {
                var dialog = new GotoRevisionSelector();
                dialog.RevisionList.ItemsSource = children;

                var c = await dialog.ShowDialog<Models.Commit>(owner);
                if (c != null)
                    vm.NavigateTo(c.SHA);
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            _navigationHistories?.SuspendNavigationVisits();
            base.OnDataContextChanged(e);

            _navigationHistories = DataContext as ViewModels.Histories;
            if (IsLoaded)
                _navigationHistories?.ResumeNavigationVisits();

            if (DataContext is ViewModels.Histories vm)
            {
                CommitListContainer.Columns[1].Width = new(vm.AuthorColumnWidth, DataGridLengthUnitType.Pixel);
                CommitListContainer.Columns[5].Width = new(vm.BranchesColumnWidth, DataGridLengthUnitType.Pixel);
                RestoreColumnOrder(vm);
            }
        }

        private void RestoreColumnOrder(ViewModels.Histories vm)
        {
            var columns = CommitListContainer.Columns;
            var order = new List<int>();
            var seen = new HashSet<int>();
            // Keep existing source-column indexes stable, but place the new column before the graph.
            if (vm.ColumnOrder == null || !vm.ColumnOrder.Contains(5))
            {
                order.Add(5);
                seen.Add(5);
            }
            foreach (var index in vm.ColumnOrder ?? [])
            {
                if (index >= 0 && index < columns.Count && seen.Add(index))
                    order.Add(index);
            }
            for (var i = 0; i < columns.Count; i++)
            {
                if (seen.Add(i))
                    order.Add(i);
            }

            // Older saved orders have no TREE column. Keep the other source-column indexes stable.
            if (vm.ColumnOrder == null || !vm.ColumnOrder.Contains(6))
            {
                order.Remove(6);
                order.Insert(order.IndexOf(0), 6);
            }

            _restoringColumnOrder = true;
            try
            {
                for (var i = 0; i < order.Count; i++)
                    columns[order[i]].DisplayIndex = i;
            }
            finally
            {
                _restoringColumnOrder = false;
            }
        }

        private void OnColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            if (_restoringColumnOrder || _pendingColumnOrderSave || DataContext is not ViewModels.Histories)
                return;

            // DisplayIndex changes arrive while the grid adjusts the remaining columns.
            // Save the complete permutation after those updates have finished.
            _pendingColumnOrderSave = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _pendingColumnOrderSave = false;
                if (DataContext is not ViewModels.Histories vm)
                    return;

                var columns = CommitListContainer.Columns;
                var order = new int[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                    order[columns[i].DisplayIndex] = i;
                vm.ColumnOrder = [.. order];
            });
        }

        private void OnBranchesColumnPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == DataGridColumn.WidthProperty &&
                sender is DataGridColumn column && column.Width.UnitType == DataGridLengthUnitType.Pixel &&
                DataContext is ViewModels.Histories vm)
                vm.BranchesColumnWidth = column.Width.Value;
        }

        private void OnBranchesResizePressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && DataContext is ViewModels.Histories vm && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            {
                _branchesResizeStartX = e.GetPosition(CommitListContainer).X;
                _branchesResizeStartWidth = vm.BranchesColumnWidth;
                _resizingBranchesColumn = true;
                e.Pointer.Capture(border);
                e.Handled = true;
            }
        }

        private void OnBranchesResizeMoved(object sender, PointerEventArgs e)
        {
            if (_resizingBranchesColumn)
            {
                var width = Math.Max(100, _branchesResizeStartWidth + e.GetPosition(CommitListContainer).X - _branchesResizeStartX);
                CommitListContainer.Columns[5].Width = new(width, DataGridLengthUnitType.Pixel);
                e.Handled = true;
            }
        }

        private void OnBranchesResizeReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_resizingBranchesColumn)
            {
                _resizingBranchesColumn = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        private void OnBranchesResizeCaptureLost(object sender, PointerCaptureLostEventArgs e)
        {
            _resizingBranchesColumn = false;
        }

        private void OnAuthorColumnPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == DataGridColumn.WidthProperty &&
                sender is DataGridColumn column && column.Width.UnitType == DataGridLengthUnitType.Pixel &&
                DataContext is ViewModels.Histories vm)
                vm.AuthorColumnWidth = column.Width.Value;
        }

        private void OnAuthorResizePressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && DataContext is ViewModels.Histories vm && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            {
                _authorResizeStartX = e.GetPosition(CommitListContainer).X;
                _authorResizeStartWidth = vm.AuthorColumnWidth;
                _resizingAuthorColumn = true;
                e.Pointer.Capture(border);
                e.Handled = true;
            }
        }

        private void OnAuthorResizeMoved(object sender, PointerEventArgs e)
        {
            if (_resizingAuthorColumn && DataContext is ViewModels.Histories vm)
            {
                var width = Math.Max(80, _authorResizeStartWidth + _authorResizeStartX - e.GetPosition(CommitListContainer).X);
                CommitListContainer.Columns[1].Width = new(width, DataGridLengthUnitType.Pixel);
                vm.AuthorColumnWidth = width;
                e.Handled = true;
            }
        }

        private void OnAuthorResizeReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_resizingAuthorColumn)
            {
                _resizingAuthorColumn = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        private void OnAuthorResizeCaptureLost(object sender, PointerCaptureLostEventArgs e)
        {
            _resizingAuthorColumn = false;
        }

        private void OnOpenAdvancedOptions(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            if (DataContext is not ViewModels.Histories histories)
                return;

            var pref = ViewModels.Preferences.Instance;

            var layout = new MenuItem();
            layout.Header = new TextBlock() { Text = App.Text("Repository.HistoriesLayout"), FontWeight = FontWeight.Bold };
            layout.IsEnabled = false;

            var isHorizontal = pref.UseTwoColumnsLayoutInHistories;
            var horizontal = new MenuItem();
            horizontal.Header = App.Text("Repository.HistoriesLayout.Horizontal");
            if (isHorizontal)
                horizontal.Icon = this.CreateMenuIcon("Icons.Check");
            horizontal.Click += (_, ev) =>
            {
                pref.UseTwoColumnsLayoutInHistories = true;
                ev.Handled = true;
            };

            var vertical = new MenuItem();
            vertical.Header = App.Text("Repository.HistoriesLayout.Vertical");
            if (!isHorizontal && !pref.UseWideDetailsLayoutInHistories)
                vertical.Icon = this.CreateMenuIcon("Icons.Check");
            vertical.Click += (_, ev) =>
            {
                pref.UseTwoColumnsLayoutInHistories = false;
                pref.UseWideDetailsLayoutInHistories = false;
                ev.Handled = true;
            };

            var wideDetails = new MenuItem();
            wideDetails.Header = App.Text("Repository.HistoriesLayout.WideDetails");
            if (pref.UseWideDetailsLayoutInHistories)
                wideDetails.Icon = this.CreateMenuIcon("Icons.Check");
            wideDetails.Click += (_, ev) =>
            {
                pref.UseWideDetailsLayoutInHistories = true;
                ev.Handled = true;
            };

            var showFlags = new MenuItem();
            showFlags.Header = new TextBlock() { Text = App.Text("Repository.ShowFlags"), FontWeight = FontWeight.Bold };
            showFlags.IsEnabled = false;

            var reflog = new MenuItem();
            reflog.Header = App.Text("Repository.ShowLostCommits");
            reflog.Tag = "--reflog";
            reflog.IsEnabled = histories.CanToggleReflog;
            if (histories.HasShowFlag(Models.HistoryShowFlags.Reflog))
                reflog.Icon = this.CreateMenuIcon("Icons.Check");
            reflog.Click += (_, ev) =>
            {
                histories.ToggleShowFlag(Models.HistoryShowFlags.Reflog);
                ev.Handled = true;
            };

            var firstParentOnly = new MenuItem();
            firstParentOnly.Header = App.Text("Repository.ShowFirstParentOnly");
            firstParentOnly.Tag = "--first-parent";
            if (histories.HasShowFlag(Models.HistoryShowFlags.FirstParentOnly))
                firstParentOnly.Icon = this.CreateMenuIcon("Icons.Check");
            firstParentOnly.Click += (_, ev) =>
            {
                histories.ToggleShowFlag(Models.HistoryShowFlags.FirstParentOnly);
                ev.Handled = true;
            };

            var simplifyByDecoration = new MenuItem();
            simplifyByDecoration.Header = App.Text("Repository.ShowDecoratedCommitsOnly");
            simplifyByDecoration.Tag = "--simplify-by-decoration";
            if (histories.HasShowFlag(Models.HistoryShowFlags.SimplifyByDecoration))
                simplifyByDecoration.Icon = this.CreateMenuIcon("Icons.Check");
            simplifyByDecoration.Click += (_, ev) =>
            {
                histories.ToggleShowFlag(Models.HistoryShowFlags.SimplifyByDecoration);
                ev.Handled = true;
            };

            var order = new MenuItem();
            order.Header = new TextBlock() { Text = App.Text("Repository.HistoriesOrder"), FontWeight = FontWeight.Bold };
            order.IsEnabled = false;

            var dateOrder = new MenuItem();
            dateOrder.Header = App.Text("Repository.HistoriesOrder.ByDate");
            dateOrder.Tag = "--date-order";
            if (!histories.EnableTopoOrder)
                dateOrder.Icon = this.CreateMenuIcon("Icons.Check");
            dateOrder.Click += (_, ev) =>
            {
                histories.EnableTopoOrder = false;
                ev.Handled = true;
            };

            var topoOrder = new MenuItem();
            topoOrder.Header = App.Text("Repository.HistoriesOrder.Topo");
            topoOrder.Tag = "--topo-order";
            if (histories.EnableTopoOrder)
                topoOrder.Icon = this.CreateMenuIcon("Icons.Check");
            topoOrder.Click += (_, ev) =>
            {
                histories.EnableTopoOrder = true;
                ev.Handled = true;
            };

            var highlights = new MenuItem();
            highlights.Header = new TextBlock() { Text = App.Text("Histories.HighlightsInGraph"), FontWeight = FontWeight.Bold };
            highlights.IsEnabled = false;

            var all = new MenuItem();
            all.Header = App.Text("Histories.HighlightsInGraph.All");
            if (histories.GraphHighlighting == Models.CommitGraphHighlighting.All)
                all.Icon = this.CreateMenuIcon("Icons.Check");
            all.Click += (_, ev) =>
            {
                histories.GraphHighlighting = Models.CommitGraphHighlighting.All;
                ev.Handled = true;
            };

            var currentBranchOnly = new MenuItem();
            currentBranchOnly.Header = App.Text("Histories.HighlightsInGraph.CurrentBranchOnly");
            if (histories.GraphHighlighting == Models.CommitGraphHighlighting.CurrentBranchOnly)
                currentBranchOnly.Icon = this.CreateMenuIcon("Icons.Check");
            currentBranchOnly.Click += (_, ev) =>
            {
                histories.GraphHighlighting = Models.CommitGraphHighlighting.CurrentBranchOnly;
                ev.Handled = true;
            };

            var selectedCommitsOnly = new MenuItem();
            selectedCommitsOnly.Header = App.Text("Histories.HighlightsInGraph.SelectedCommitsOnly");
            if (histories.GraphHighlighting == Models.CommitGraphHighlighting.SelectedCommitsOnly)
                selectedCommitsOnly.Icon = this.CreateMenuIcon("Icons.Check");
            selectedCommitsOnly.Click += (_, ev) =>
            {
                histories.GraphHighlighting = Models.CommitGraphHighlighting.SelectedCommitsOnly;
                ev.Handled = true;
            };

            var selectedCommitsOnlyFirstParent = new MenuItem();
            selectedCommitsOnlyFirstParent.Header = App.Text("Histories.HighlightsInGraph.SelectedCommitsOnlyFirstParent");
            if (histories.GraphHighlighting == Models.CommitGraphHighlighting.SelectedCommitsOnlyFirstParent)
                selectedCommitsOnlyFirstParent.Icon = this.CreateMenuIcon("Icons.Check");
            selectedCommitsOnlyFirstParent.Click += (_, ev) =>
            {
                histories.GraphHighlighting = Models.CommitGraphHighlighting.SelectedCommitsOnlyFirstParent;
                ev.Handled = true;
            };

            var currentBranchAndSelectedCommits = new MenuItem();
            currentBranchAndSelectedCommits.Header = App.Text("Histories.HighlightsInGraph.CurrentBranchAndSelectedCommits");
            if (histories.GraphHighlighting == Models.CommitGraphHighlighting.CurrentBranchAndSelectedCommits)
                currentBranchAndSelectedCommits.Icon = this.CreateMenuIcon("Icons.Check");
            currentBranchAndSelectedCommits.Click += (_, ev) =>
            {
                histories.GraphHighlighting = Models.CommitGraphHighlighting.CurrentBranchAndSelectedCommits;
                ev.Handled = true;
            };

            var columnsHeader = new MenuItem();
            columnsHeader.Header = new TextBlock() { Text = App.Text("Histories.ShowColumns"), FontWeight = FontWeight.Bold };
            columnsHeader.IsEnabled = false;

            var branchesColumn = new MenuItem();
            branchesColumn.Header = App.Text("Histories.Header.Branches");
            if (histories.IsBranchesColumnVisible)
                branchesColumn.Icon = this.CreateMenuIcon("Icons.Check");
            branchesColumn.Click += (_, ev) =>
            {
                histories.IsBranchesColumnVisible = !histories.IsBranchesColumnVisible;
                ev.Handled = true;
            };

            var treeColumn = new MenuItem();
            treeColumn.Header = App.Text("Histories.Header.Tree");
            if (histories.IsTreeColumnVisible)
                treeColumn.Icon = this.CreateMenuIcon("Icons.Check");
            treeColumn.Click += (_, ev) =>
            {
                histories.IsTreeColumnVisible = !histories.IsTreeColumnVisible;
                ev.Handled = true;
            };

            var authorColumn = new MenuItem();
            authorColumn.Header = App.Text("Histories.Header.Author");
            if (histories.IsAuthorColumnVisible)
                authorColumn.Icon = this.CreateMenuIcon("Icons.Check");
            authorColumn.Click += (_, ev) =>
            {
                histories.IsAuthorColumnVisible = !histories.IsAuthorColumnVisible;
                ev.Handled = true;
            };

            var shaColumn = new MenuItem();
            shaColumn.Header = App.Text("Histories.Header.SHA");
            if (histories.IsSHAColumnVisible)
                shaColumn.Icon = this.CreateMenuIcon("Icons.Check");
            shaColumn.Click += (_, ev) =>
            {
                histories.IsSHAColumnVisible = !histories.IsSHAColumnVisible;
                ev.Handled = true;
            };

            var authorTimeColumn = new MenuItem();
            authorTimeColumn.Header = App.Text("Histories.Header.AuthorTime");
            if (histories.IsAuthorTimeColumnVisible)
                authorTimeColumn.Icon = this.CreateMenuIcon("Icons.Check");
            authorTimeColumn.Click += (_, ev) =>
            {
                histories.IsAuthorTimeColumnVisible = !histories.IsAuthorTimeColumnVisible;
                ev.Handled = true;
            };

            var commitTimeColumn = new MenuItem();
            commitTimeColumn.Header = App.Text("Histories.Header.CommitTime");
            if (histories.IsCommitTimeColumnVisible)
                commitTimeColumn.Icon = this.CreateMenuIcon("Icons.Check");
            commitTimeColumn.Click += (_, ev) =>
            {
                histories.IsCommitTimeColumnVisible = !histories.IsCommitTimeColumnVisible;
                ev.Handled = true;
            };

            var menu = new ContextMenu();
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.Items.Add(layout);
            menu.Items.Add(horizontal);
            menu.Items.Add(vertical);
            menu.Items.Add(wideDetails);
            menu.Items.Add(new MenuItem() { Header = "-" });
            menu.Items.Add(showFlags);
            menu.Items.Add(reflog);
            menu.Items.Add(firstParentOnly);
            menu.Items.Add(simplifyByDecoration);
            menu.Items.Add(new MenuItem() { Header = "-" });
            menu.Items.Add(order);
            menu.Items.Add(dateOrder);
            menu.Items.Add(topoOrder);
            menu.Items.Add(new MenuItem() { Header = "-" });
            menu.Items.Add(highlights);
            menu.Items.Add(all);
            menu.Items.Add(currentBranchOnly);
            menu.Items.Add(selectedCommitsOnly);
            menu.Items.Add(selectedCommitsOnlyFirstParent);
            menu.Items.Add(currentBranchAndSelectedCommits);
            menu.Items.Add(new MenuItem() { Header = "-" });
            menu.Items.Add(columnsHeader);
            menu.Items.Add(branchesColumn);
            menu.Items.Add(treeColumn);
            menu.Items.Add(authorColumn);
            menu.Items.Add(shaColumn);
            menu.Items.Add(authorTimeColumn);
            menu.Items.Add(commitTimeColumn);
            menu.Open(button);
        }

        private void OnCommitListLayoutUpdated(object _1, EventArgs _2)
        {
            if (!IsLoaded)
                return;

            var dataGrid = CommitListContainer;
            var rowsPresenter = dataGrid.FindDescendantOfType<DataGridRowsPresenter>();
            if (rowsPresenter == null)
                return;

            double rowHeight = dataGrid.RowHeight;
            double startY = 0;
            // The header must not add space beyond the visible graph lanes.
            double treeWidth = 0;
            foreach (var child in rowsPresenter.Children)
            {
                if (child is DataGridRow { IsVisible: true } row)
                {
                    rowHeight = row.Bounds.Height;

                    if (row.Bounds.Bottom > 0 && row.Bounds.Top < rowsPresenter.Bounds.Height)
                    {
                        if (row.DataContext is Models.Commit commit)
                            treeWidth = Math.Max(treeWidth, commit.LeftMargin + 4);
                        else if (row.DataContext is ViewModels.WorkingDirectoryEntry workingDirectory)
                            treeWidth = Math.Max(treeWidth, workingDirectory.LeftMargin + 4);
                    }

                    if (row.Bounds.Top <= 0 && row.Bounds.Top > -rowHeight)
                    {
                        var test = rowHeight * row.Index - row.Bounds.Top;
                        if (startY < test)
                            startY = test;
                    }
                }
            }

            IsScrollToTopVisible = startY >= rowHeight;

            var separateTree = DataContext is ViewModels.Histories { IsTreeColumnVisible: true };
            var graphColumn = dataGrid.Columns[separateTree ? 6 : 0];
            if (separateTree && Math.Abs(graphColumn.Width.Value - treeWidth) > 0.01)
                graphColumn.Width = new(treeWidth, DataGridLengthUnitType.Pixel);
            double graphOffset = 0;
            foreach (var column in dataGrid.Columns)
            {
                if (column.IsVisible && column.DisplayIndex < graphColumn.DisplayIndex)
                    graphOffset += column.ActualWidth;
            }
            if (Math.Abs(CommitGraph.Margin.Left - graphOffset) > 0.01)
                CommitGraph.Margin = new Thickness(graphOffset, 24, 0, 0);

            var branchesColumn = dataGrid.Columns[5];
            var branchesRight = branchesColumn.ActualWidth;
            foreach (var column in dataGrid.Columns)
            {
                if (column.IsVisible && column.DisplayIndex < branchesColumn.DisplayIndex)
                    branchesRight += column.ActualWidth;
            }
            var branchesResizeOffset = Math.Max(0, branchesRight - 2);
            if (Math.Abs(BranchesColumnResizeHandle.Margin.Left - branchesResizeOffset) > 0.01)
                BranchesColumnResizeHandle.Margin = new Thickness(branchesResizeOffset, 0, 0, 0);

            var authorColumn = dataGrid.Columns[1];
            double authorOffset = 0;
            foreach (var column in dataGrid.Columns)
            {
                if (column.IsVisible && column.DisplayIndex < authorColumn.DisplayIndex)
                    authorOffset += column.ActualWidth;
            }
            var resizeOffset = Math.Max(0, authorOffset - 2);
            if (Math.Abs(AuthorColumnResizeHandle.Margin.Left - resizeOffset) > 0.01)
                AuthorColumnResizeHandle.Margin = new Thickness(resizeOffset, 0, 0, 0);

            var clipWidth = Math.Max(0, graphColumn.ActualWidth - 4);
            var lastLayout = CommitGraph.Layout;
            if (lastLayout == null ||
                Math.Abs(lastLayout.StartY - startY) > 0.01 ||
                Math.Abs(lastLayout.ClipWidth - clipWidth) > 0.01 ||
                Math.Abs(lastLayout.RowHeight - rowHeight) > 0.01)
                CommitGraph.Layout = new(startY, clipWidth, rowHeight);

            UpdateBranchConnections(rowsPresenter, graphColumn, startY, rowHeight);
        }

        private void UpdateBranchConnections(DataGridRowsPresenter rowsPresenter, DataGridColumn graphColumn, double startY, double rowHeight)
        {
            var connections = new List<CommitBranchConnection>();
            var branchesColumn = CommitListContainer.Columns[5];
            var adjacent = branchesColumn.IsVisible && branchesColumn.DisplayIndex < graphColumn.DisplayIndex;
            foreach (var column in CommitListContainer.Columns)
            {
                if (column.IsVisible && column.DisplayIndex > branchesColumn.DisplayIndex && column.DisplayIndex < graphColumn.DisplayIndex)
                    adjacent = false;
            }

            if (adjacent && CommitGraph.Graph is { } graph)
            {
                foreach (var child in rowsPresenter.Children)
                {
                    if (child is not DataGridRow { IsVisible: true, DataContext: Models.Commit } row ||
                        row.Index < 0 || row.Index >= graph.Dots.Count)
                        continue;

                    foreach (var visual in row.GetVisualDescendants())
                    {
                        if (visual is not CommitRefsPresenter { ShowBranchSummary: true, IsEffectivelyVisible: true } label ||
                            label.BranchSummaryBrush == null || label.IsContextBranchSummary)
                            continue;

                        var dot = graph.Dots[row.Index];
                        var end = CommitGraph.TranslatePoint(new Point(dot.Center.X, dot.Center.Y * rowHeight - startY), BranchConnections);
                        var start = label.TranslatePoint(new Point(System.Math.Min(label.BranchSummaryRight, label.Bounds.Width), 0), BranchConnections);
                        if (start.HasValue && end.HasValue && start.Value.X < end.Value.X &&
                            end.Value.Y >= 0 && end.Value.Y < BranchConnections.Bounds.Height)
                        {
                            var branch = label.DecoratorAt(new Point(0, 0));
                            var active = branch?.Type == Models.DecoratorType.CurrentBranchHead ||
                                (branch?.Type == Models.DecoratorType.LocalBranchHead &&
                                 DataContext is ViewModels.Histories { CurrentBranch: { IsDetachedHead: false } current } &&
                                 string.Equals(branch.Name, current.Name, StringComparison.Ordinal));
                            connections.Add(new(new Point(start.Value.X, end.Value.Y), end.Value, label.BranchSummaryBrush, active, label.BranchSummarySuffixWidth));
                        }
                        break;
                    }
                }
            }

            BranchConnections.UpdateConnections(connections);
        }

        private void OnScrollToTopPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.Histories histories)
            {
                if (histories.Entries.Count > 0)
                    CommitListContainer.ScrollIntoView(histories.Entries[0], null);
            }
        }

        private void OnCommitListContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var repoView = this.FindAncestorOfType<Repository>();
            if (repoView is not { DataContext: ViewModels.Repository repo })
                return;

            if (e.Source is Control { DataContext: ViewModels.WorkingDirectoryEntry })
            {
                CreateContextMenuForWorkingDirectory(repo).Open(CommitListContainer);
                e.Handled = true;
                return;
            }

            var selected = CommitListContainer.SelectedItems;
            if (selected is not { Count: > 0 })
                return;

            var commits = new List<Models.Commit>();
            for (var i = selected.Count - 1; i >= 0; i--)
            {
                if (selected[i] is Models.Commit c)
                    commits.Add(c);
            }

            if (commits.Count != selected.Count)
            {
                e.Handled = true;
                return;
            }

            if (selected.Count > 1)
            {
                var menu = CreateContextMenuForMultipleCommits(repo, commits);
                menu.Open(CommitListContainer);
            }
            else if (selected.Count == 1)
            {
                var menu = CreateContextMenuForSingleCommit(repo, commits[0]);
                menu.Open(CommitListContainer);
            }

            e.Handled = true;
        }

        private async void OnCommitListDoubleTapped(object sender, TappedEventArgs e)
        {
            e.Handled = true;

            if (e.Source is Control { DataContext: ViewModels.WorkingDirectoryEntry } &&
                this.FindAncestorOfType<Repository>() is { DataContext: ViewModels.Repository repo })
            {
                repo.SelectedViewIndex = 1;
                return;
            }

            if (DataContext is ViewModels.Histories histories &&
                CommitListContainer.SelectedItems is { Count: 1 } &&
                e.Source is Control { DataContext: Models.Commit c })
            {
                if (histories.Bisect != null)
                {
                    histories.CheckoutCommitDetached(c);
                    return;
                }

                if (e.Source is CommitRefsPresenter crp)
                {
                    var decorator = crp.DecoratorAt(e.GetPosition(crp));
                    var succ = await histories.CheckoutBranchByDecoratorAsync(decorator);
                    if (succ)
                        return;
                }

                await histories.CheckoutBranchByCommitAsync(c);
            }
        }

        private ContextMenu CreateContextMenuForWorkingDirectory(ViewModels.Repository repo)
        {
            var stash = new MenuItem { Header = App.Text("Stash.Title"), Icon = this.CreateMenuIcon("Icons.Stashes.Add"), IsEnabled = repo.Histories.WorkingDirectory.HasChanges };
            stash.Click += async (_, e) =>
            {
                await repo.StashAllAsync(false);
                e.Handled = true;
            };

            var discard = new MenuItem { Header = App.Text("Repository.DiscardAll"), Icon = this.CreateMenuIcon("Icons.Undo"), IsEnabled = repo.Histories.WorkingDirectory.HasChanges };
            discard.Click += (_, e) =>
            {
                repo.WorkingCopy.DiscardAllChanges();
                e.Handled = true;
            };

            var openPage = new MenuItem { Header = App.Text("Histories.WorkingDirectory.OpenPage"), Icon = this.CreateMenuIcon("Icons.Changes") };
            openPage.Click += (_, e) =>
            {
                repo.SelectedViewIndex = 1;
                e.Handled = true;
            };

            return new ContextMenu { Items = { stash, discard, new Separator(), openPage } };
        }

        private void OnCommitGraphLoaded(object sender, RoutedEventArgs e)
        {
            // Force-update the graph layout to ensure the graph is correctly rendered when it's loaded.
            OnCommitListLayoutUpdated(sender, e);
        }

        private void OnTabHeaderPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (ViewModels.Preferences.Instance.UseTwoColumnsLayoutInHistories)
                return;

            if (DataContext is not ViewModels.Histories vm)
                return;

            if (vm.IsCollapseDetails)
                vm.IsCollapseDetails = false;
        }

        private void OnOpenDetailsAsStandalone(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Histories vm)
            {
                if (vm.DetailContext is ViewModels.CommitDetail detail)
                {
                    var standalone = new CommitDetailStandalone();
                    standalone.DataContext = detail.Clone();
                    this.ShowWindow(standalone);
                }
                else if (vm.DetailContext is ViewModels.RevisionCompare compare)
                {
                    var standalone = new RevisionCompareStandalone();
                    standalone.DataContext = compare.Clone();
                    this.ShowWindow(standalone);
                }
            }

            e.Handled = true;
        }

        private ContextMenu CreateContextMenuForMultipleCommits(ViewModels.Repository repo, List<Models.Commit> selected)
        {
            var canCherryPick = true;
            var canMerge = true;

            foreach (var c in selected)
            {
                if (c.IsMerged)
                {
                    canMerge = false;
                }

                if (c.Parents.Count > 1)
                {
                    canCherryPick = false;
                }
            }

            var menu = new ContextMenu();

            if (!repo.IsBare)
            {
                if (canCherryPick)
                {
                    var cherryPick = new MenuItem();
                    cherryPick.Header = App.ContextMenuText("CommitCM.CherryPickMultiple");
                    cherryPick.Icon = this.CreateMenuIcon("Icons.CherryPick");
                    cherryPick.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.CherryPick(repo, selected));
                        e.Handled = true;
                    };
                    menu.Items.Add(cherryPick);
                }

                if (canMerge)
                {
                    var merge = new MenuItem();
                    merge.Header = App.Text("CommitCM.MergeMultiple");
                    merge.Icon = this.CreateMenuIcon("Icons.Merge");
                    merge.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.MergeMultiple(repo, selected));
                        e.Handled = true;
                    };
                    menu.Items.Add(merge);
                }

                if (canCherryPick || canMerge)
                    menu.Items.Add(new MenuItem() { Header = "-" });
            }

            var saveToPatch = new MenuItem();
            saveToPatch.Icon = this.CreateMenuIcon("Icons.Save");
            saveToPatch.Header = App.Text("CommitCM.SaveAsPatch");
            saveToPatch.Click += async (_, e) =>
            {
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider == null)
                    return;

                var options = new FolderPickerOpenOptions() { AllowMultiple = false };
                try
                {
                    var picker = await storageProvider.OpenFolderPickerAsync(options);
                    if (picker.Count == 1)
                    {
                        var folder = picker[0];
                        var folderPath = folder is { Path: { IsAbsoluteUri: true } path } ? path.LocalPath : folder.Path.ToString();
                        var succ = false;
                        for (var i = 0; i < selected.Count; i++)
                        {
                            succ = await repo.SaveCommitAsPatchAsync(selected[i], folderPath, i);
                            if (!succ)
                                break;
                        }

                        if (succ)
                            repo.SendNotification(App.Text("SaveAsPatchSuccess"));
                    }
                }
                catch (Exception exception)
                {
                    repo.SendNotification($"Failed to save as patch: {exception.Message}", true);
                }

                e.Handled = true;
            };
            menu.Items.Add(saveToPatch);
            menu.Items.Add(new MenuItem() { Header = "-" });

            var copyInfos = new MenuItem();
            copyInfos.Header = App.Text("CommitCM.CopySHA") + " - " + App.Text("CommitCM.CopySubject");
            copyInfos.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
            copyInfos.Click += async (_, e) =>
            {
                var builder = new StringBuilder();
                foreach (var c in selected)
                    builder.Append(c.SHA.AsSpan(0, 10)).Append(" - ").AppendLine(c.Subject);

                await this.CopyTextAsync(builder.ToString());
                e.Handled = true;
            };

            var copyShas = new MenuItem();
            copyShas.Header = App.Text("CommitCM.CopySHA");
            copyShas.Icon = this.CreateMenuIcon("Icons.Hash");
            copyShas.Click += async (_, e) =>
            {
                var builder = new StringBuilder();
                foreach (var c in selected)
                    builder.AppendLine(c.SHA);

                await this.CopyTextAsync(builder.ToString());
                e.Handled = true;
            };

            var copySubjects = new MenuItem();
            copySubjects.Header = App.Text("CommitCM.CopySubject");
            copySubjects.Icon = this.CreateMenuIcon("Icons.Subject");
            copySubjects.Click += async (_, e) =>
            {
                var builder = new StringBuilder();
                foreach (var c in selected)
                    builder.AppendLine(c.Subject);

                await this.CopyTextAsync(builder.ToString());
                e.Handled = true;
            };

            var copyMessage = new MenuItem();
            copyMessage.Header = App.Text("CommitCM.CopyCommitMessage");
            copyMessage.Icon = this.CreateMenuIcon("Icons.Message");
            copyMessage.Click += async (_, e) =>
            {
                var vm = DataContext as ViewModels.Histories;
                var messages = new List<string>();
                foreach (var c in selected)
                {
                    var message = await vm!.GetCommitFullMessageAsync(c);
                    messages.Add(message);
                }

                await this.CopyTextAsync(string.Join("\n-----\n", messages));
                e.Handled = true;
            };

            var copy = new MenuItem();
            copy.Header = App.Text("Copy");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Items.Add(copyInfos);
            copy.Items.Add(new MenuItem() { Header = "-" });
            copy.Items.Add(copyShas);
            copy.Items.Add(copySubjects);
            copy.Items.Add(copyMessage);
            menu.Items.Add(copy);
            return menu;
        }

        private ContextMenu CreateContextMenuForSingleCommit(ViewModels.Repository repo, Models.Commit commit)
        {
            var current = repo.CurrentBranch;
            var vm = DataContext as ViewModels.Histories;
            if (current == null || vm == null)
                return null;

            var menu = new ContextMenu();
            var tags = new List<Models.Tag>();
            var isHead = commit.IsCurrentHead;

            if (commit.HasDecorators)
            {
                foreach (var d in commit.Decorators)
                {
                    switch (d.Type)
                    {
                        case Models.DecoratorType.CurrentBranchHead:
                            FillCurrentBranchMenu(menu, repo, current);
                            break;
                        case Models.DecoratorType.LocalBranchHead:
                            var lb = repo.Branches.Find(x => x.IsLocal && d.Name.Equals(x.Name, StringComparison.Ordinal));
                            FillOtherLocalBranchMenu(menu, repo, lb, current, commit.IsMerged);
                            break;
                        case Models.DecoratorType.RemoteBranchHead:
                            var rb = repo.Branches.Find(x => !x.IsLocal && d.Name.Equals(x.FriendlyName, StringComparison.Ordinal));
                            FillRemoteBranchMenu(menu, repo, rb, current, commit.IsMerged);
                            break;
                        case Models.DecoratorType.Tag:
                            var t = repo.Tags.Find(x => d.Name.Equals(x.Name, StringComparison.Ordinal));
                            if (t != null)
                                tags.Add(t);
                            break;
                    }
                }

                if (menu.Items.Count > 0)
                    menu.Items.Add(new MenuItem() { Header = "-" });
            }

            if (tags.Count > 0)
            {
                foreach (var tag in tags)
                    FillTagMenu(menu, repo, tag, current);
                menu.Items.Add(new MenuItem() { Header = "-" });
            }

            var createBranch = new MenuItem();
            createBranch.Icon = this.CreateMenuIcon("Icons.Branch.Add");
            createBranch.Header = App.ContextMenuText("CreateBranch");
            createBranch.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+B" : "Ctrl+Shift+B";
            createBranch.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.CreateBranch(repo, commit));
                e.Handled = true;
            };
            menu.Items.Add(createBranch);

            var createTag = new MenuItem();
            createTag.Icon = this.CreateMenuIcon("Icons.Tag.Add");
            createTag.Header = App.Text("CreateTag");
            createTag.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+T" : "Ctrl+Shift+T";
            createTag.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.CreateTag(repo, commit));
                e.Handled = true;
            };
            menu.Items.Add(createTag);
            menu.Items.Add(new MenuItem() { Header = "-" });

            if (!repo.IsBare)
            {
                var target = commit.GetFriendlyName();
                if (target.Length > 40)
                    target = commit.SHA.Substring(0, 10);

                if (!isHead)
                {
                    var reset = new MenuItem();
                    reset.Header = App.Text("CommitCM.Reset", current.Name, target);
                    reset.Icon = this.CreateMenuIcon("Icons.Reset");
                    reset.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.Reset(repo, current, commit));
                        e.Handled = true;
                    };
                    menu.Items.Add(reset);
                }

                if (!commit.IsMerged)
                {
                    var rebase = new MenuItem();
                    rebase.Header = App.Text("CommitCM.Rebase", current.Name, target);
                    rebase.Icon = this.CreateMenuIcon("Icons.Rebase");
                    rebase.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.Rebase(repo, current, commit));
                        e.Handled = true;
                    };
                    menu.Items.Add(rebase);

                    var merge = new MenuItem();
                    merge.Header = App.Text("BranchCM.Merge", target, current.Name);
                    merge.Icon = this.CreateMenuIcon("Icons.Merge");
                    merge.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                        {
                            var found = false;
                            foreach (var d in commit.Decorators)
                            {
                                if (d.Type == Models.DecoratorType.LocalBranchHead)
                                {
                                    var b = repo.Branches.Find(x => x.IsLocal && x.Name.Equals(d.Name, StringComparison.Ordinal));
                                    if (b != null)
                                    {
                                        found = true;
                                        repo.ShowPopup(new ViewModels.Merge(repo, b, current.Name, false));
                                        break;
                                    }
                                }
                                else if (d.Type == Models.DecoratorType.RemoteBranchHead)
                                {
                                    var rb = repo.Branches.Find(x => !x.IsLocal && x.FriendlyName.Equals(d.Name, StringComparison.Ordinal));
                                    if (rb != null)
                                    {
                                        found = true;
                                        repo.ShowPopup(new ViewModels.Merge(repo, rb, current.Name, false));
                                        break;
                                    }
                                }
                                else if (d.Type == Models.DecoratorType.Tag)
                                {
                                    var t = repo.Tags.Find(x => x.Name.Equals(d.Name, StringComparison.Ordinal));
                                    if (t != null)
                                    {
                                        found = true;
                                        repo.ShowPopup(new ViewModels.Merge(repo, t, current.Name));
                                        break;
                                    }
                                }
                            }

                            if (!found)
                                repo.ShowPopup(new ViewModels.Merge(repo, commit, current.Name));
                        }

                        e.Handled = true;
                    };
                    menu.Items.Add(merge);
                }

                var cherryPick = new MenuItem();
                cherryPick.Header = App.ContextMenuText("CommitCM.CherryPick");
                cherryPick.Icon = this.CreateMenuIcon("Icons.CherryPick");
                cherryPick.Click += async (_, e) =>
                {
                    await vm.CherryPickAsync(commit);
                    e.Handled = true;
                };
                menu.Items.Add(cherryPick);

                var revert = new MenuItem();
                revert.Header = App.ContextMenuText("CommitCM.Revert");
                revert.Icon = this.CreateMenuIcon("Icons.Undo");
                revert.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.Revert(repo, commit));
                    e.Handled = true;
                };
                menu.Items.Add(revert);

                if (!isHead)
                {
                    var checkoutCommit = new MenuItem();
                    checkoutCommit.Header = App.ContextMenuText("CommitCM.Checkout");
                    checkoutCommit.Icon = this.CreateMenuIcon("Icons.Detached");
                    checkoutCommit.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.CheckoutDetached(repo, commit));
                        e.Handled = true;
                    };
                    menu.Items.Add(checkoutCommit);
                }

                if (commit.IsMerged && commit.Parents.Count > 0)
                {
                    var interactiveRebase = new MenuItem();
                    interactiveRebase.Header = App.ContextMenuText("CommitCM.InteractiveRebase");
                    interactiveRebase.Icon = this.CreateMenuIcon("Icons.InteractiveRebase");

                    if (!isHead)
                    {
                        var manually = new MenuItem();
                        manually.Header = App.ContextMenuText("CommitCM.InteractiveRebase.Manually", current.Name, target);
                        manually.Click += async (_, e) =>
                        {
                            await this.ShowDialogAsync(new ViewModels.InteractiveRebase(repo, commit));
                            e.Handled = true;
                        };

                        interactiveRebase.Items.Add(manually);
                        interactiveRebase.Items.Add(new MenuItem() { Header = "-" });
                    }

                    var reword = new MenuItem();
                    reword.Header = App.ContextMenuText("CommitCM.InteractiveRebase.Reword");
                    reword.Icon = this.CreateMenuIcon("Icons.Rename");
                    reword.Click += async (_, e) =>
                    {
                        await InteractiveRebaseWithPrefillActionAsync(repo, commit, Models.InteractiveRebaseAction.Reword);
                        e.Handled = true;
                    };

                    var edit = new MenuItem();
                    edit.Header = App.Text("CommitCM.InteractiveRebase.Edit");
                    edit.Icon = this.CreateMenuIcon("Icons.Edit");
                    edit.Click += async (_, e) =>
                    {
                        await InteractiveRebaseWithPrefillActionAsync(repo, commit, Models.InteractiveRebaseAction.Edit);
                        e.Handled = true;
                    };

                    var squash = new MenuItem();
                    squash.Header = App.ContextMenuText("CommitCM.InteractiveRebase.Squash");
                    squash.Icon = this.CreateMenuIcon("Icons.SquashIntoParent");
                    squash.Click += async (_, e) =>
                    {
                        await InteractiveRebaseWithPrefillActionAsync(repo, commit, Models.InteractiveRebaseAction.Squash);
                        e.Handled = true;
                    };

                    var fixup = new MenuItem();
                    fixup.Header = App.ContextMenuText("CommitCM.InteractiveRebase.Fixup");
                    fixup.Icon = this.CreateMenuIcon("Icons.Fix");
                    fixup.Click += async (_, e) =>
                    {
                        await InteractiveRebaseWithPrefillActionAsync(repo, commit, Models.InteractiveRebaseAction.Fixup);
                        e.Handled = true;
                    };

                    var drop = new MenuItem();
                    drop.Header = App.Text("CommitCM.InteractiveRebase.Drop");
                    drop.Icon = this.CreateMenuIcon("Icons.Clear");
                    drop.Click += async (_, e) =>
                    {
                        await InteractiveRebaseWithPrefillActionAsync(repo, commit, Models.InteractiveRebaseAction.Drop);
                        e.Handled = true;
                    };

                    interactiveRebase.Items.Add(reword);
                    interactiveRebase.Items.Add(edit);
                    interactiveRebase.Items.Add(squash);
                    interactiveRebase.Items.Add(fixup);
                    interactiveRebase.Items.Add(drop);

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(interactiveRebase);
                }
                else
                {
                    var interactiveRebase = new MenuItem();
                    interactiveRebase.Header = App.ContextMenuText("CommitCM.InteractiveRebase.Manually", current.Name, target);
                    interactiveRebase.Icon = this.CreateMenuIcon("Icons.InteractiveRebase");
                    interactiveRebase.Click += async (_, e) =>
                    {
                        await this.ShowDialogAsync(new ViewModels.InteractiveRebase(repo, commit));
                        e.Handled = true;
                    };

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(interactiveRebase);
                }

                menu.Items.Add(new MenuItem() { Header = "-" });
            }

            if (!isHead)
            {
                if (current.Ahead.Contains(commit.SHA))
                {
                    var upstream = repo.Branches.Find(x => x.FullName.Equals(current.Upstream, StringComparison.Ordinal));
                    var pushRevision = new MenuItem();
                    pushRevision.Header = App.Text("CommitCM.PushRevision", commit.SHA.Substring(0, 10), upstream.FriendlyName);
                    pushRevision.Icon = this.CreateMenuIcon("Icons.Push");
                    pushRevision.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.PushRevision(repo, commit, upstream));
                        e.Handled = true;
                    };
                    menu.Items.Add(pushRevision);
                    menu.Items.Add(new MenuItem() { Header = "-" });
                }

                var compareWithHead = new MenuItem();
                compareWithHead.Header = App.Text("CommitCM.CompareWithHead");
                compareWithHead.Icon = this.CreateMenuIcon("Icons.Compare");
                compareWithHead.Click += async (_, e) =>
                {
                    var head = await vm.CompareWithHeadAsync(commit);
                    if (head != null)
                        CommitListContainer.SelectedItems.Add(head);

                    e.Handled = true;
                };
                menu.Items.Add(compareWithHead);

                if (repo.LocalChangesCount > 0)
                {
                    var compareWithWorktree = new MenuItem();
                    compareWithWorktree.Header = App.Text("CommitCM.CompareWithWorktree");
                    compareWithWorktree.Icon = this.CreateMenuIcon("Icons.Compare");
                    compareWithWorktree.Click += (_, e) =>
                    {
                        vm.CompareWithWorktree(commit);
                        e.Handled = true;
                    };
                    menu.Items.Add(compareWithWorktree);
                }

                menu.Items.Add(new MenuItem() { Header = "-" });
            }

            var saveToPatch = new MenuItem();
            saveToPatch.Icon = this.CreateMenuIcon("Icons.Save");
            saveToPatch.Header = App.Text("CommitCM.SaveAsPatch");
            saveToPatch.Click += async (_, e) =>
            {
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider == null)
                    return;

                var options = new FolderPickerOpenOptions() { AllowMultiple = false };
                try
                {
                    var selected = await storageProvider.OpenFolderPickerAsync(options);
                    if (selected.Count == 1)
                    {
                        var folder = selected[0];
                        var folderPath = folder is { Path: { IsAbsoluteUri: true } path } ? path.LocalPath : folder.Path.ToString();
                        var succ = await repo.SaveCommitAsPatchAsync(commit, folderPath);
                        if (succ)
                            repo.SendNotification(App.Text("SaveAsPatchSuccess"));
                    }
                }
                catch (Exception exception)
                {
                    repo.SendNotification($"Failed to save as patch: {exception.Message}", true);
                }

                e.Handled = true;
            };
            menu.Items.Add(saveToPatch);

            var archive = new MenuItem();
            archive.Icon = this.CreateMenuIcon("Icons.Archive");
            archive.Header = App.Text("Archive");
            archive.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.Archive(repo, commit));
                e.Handled = true;
            };
            menu.Items.Add(archive);
            menu.Items.Add(new MenuItem() { Header = "-" });

            var actions = repo.GetCustomActions(Models.CustomActionScope.Commit);
            if (actions.Count > 0)
            {
                var custom = new MenuItem();
                custom.Header = App.Text("CommitCM.CustomAction");
                custom.Icon = this.CreateMenuIcon("Icons.Action");

                foreach (var action in actions)
                {
                    var (dup, label) = action;
                    var item = new MenuItem();
                    item.Icon = this.CreateMenuIcon("Icons.Action");
                    item.Header = label;
                    item.Click += async (_, e) =>
                    {
                        await repo.ExecCustomActionAsync(dup, commit);
                        e.Handled = true;
                    };

                    custom.Items.Add(item);
                }

                menu.Items.Add(custom);
                menu.Items.Add(new MenuItem() { Header = "-" });
            }

            var copyInfo = new MenuItem();
            copyInfo.Header = App.Text("CommitCM.CopySHA") + " - " + App.Text("CommitCM.CopySubject");
            copyInfo.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
            copyInfo.Click += async (_, e) =>
            {
                await this.CopyTextAsync($"{commit.SHA.AsSpan(0, 10)} - {commit.Subject}");
                e.Handled = true;
            };

            var copySHA = new MenuItem();
            copySHA.Header = App.Text("CommitCM.CopySHA");
            copySHA.Icon = this.CreateMenuIcon("Icons.Hash");
            copySHA.Click += async (_, e) =>
            {
                await this.CopyTextAsync(commit.SHA);
                e.Handled = true;
            };

            var copySubject = new MenuItem();
            copySubject.Header = App.Text("CommitCM.CopySubject");
            copySubject.Icon = this.CreateMenuIcon("Icons.Subject");
            copySubject.Click += async (_, e) =>
            {
                await this.CopyTextAsync(commit.Subject);
                e.Handled = true;
            };

            var copyMessage = new MenuItem();
            copyMessage.Header = App.Text("CommitCM.CopyCommitMessage");
            copyMessage.Icon = this.CreateMenuIcon("Icons.Message");
            copyMessage.Click += async (_, e) =>
            {
                var message = await vm.GetCommitFullMessageAsync(commit);
                await this.CopyTextAsync(message);
                e.Handled = true;
            };

            var copyAuthor = new MenuItem();
            copyAuthor.Header = App.Text("CommitCM.CopyAuthor");
            copyAuthor.Icon = this.CreateMenuIcon("Icons.User");
            copyAuthor.Click += async (_, e) =>
            {
                await this.CopyTextAsync(commit.Author.ToString());
                e.Handled = true;
            };

            var copyCommitter = new MenuItem();
            copyCommitter.Header = App.Text("CommitCM.CopyCommitter");
            copyCommitter.Icon = this.CreateMenuIcon("Icons.User");
            copyCommitter.Click += async (_, e) =>
            {
                await this.CopyTextAsync(commit.Committer.ToString());
                e.Handled = true;
            };

            var copyAuthorTime = new MenuItem();
            copyAuthorTime.Header = App.Text("CommitCM.CopyAuthorTime");
            copyAuthorTime.Icon = this.CreateMenuIcon("Icons.DateTime");
            copyAuthorTime.Click += async (_, e) =>
            {
                await this.CopyTextAsync(Models.DateTimeFormat.Format(commit.AuthorTime));
                e.Handled = true;
            };

            var copyCommitterTime = new MenuItem();
            copyCommitterTime.Header = App.Text("CommitCM.CopyCommitterTime");
            copyCommitterTime.Icon = this.CreateMenuIcon("Icons.DateTime");
            copyCommitterTime.Click += async (_, e) =>
            {
                await this.CopyTextAsync(Models.DateTimeFormat.Format(commit.CommitterTime));
                e.Handled = true;
            };

            var copy = new MenuItem();
            copy.Header = App.Text("Copy");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Items.Add(copyInfo);
            copy.Items.Add(new MenuItem() { Header = "-" });
            copy.Items.Add(copySHA);
            copy.Items.Add(copySubject);
            copy.Items.Add(copyMessage);
            copy.Items.Add(copyAuthor);
            copy.Items.Add(copyCommitter);
            copy.Items.Add(copyAuthorTime);
            copy.Items.Add(copyCommitterTime);
            menu.Items.Add(copy);

            return menu;
        }

        private void FillCurrentBranchMenu(ContextMenu menu, ViewModels.Repository repo, Models.Branch current)
        {
            var submenu = new MenuItem();
            submenu.Icon = this.CreateMenuIcon("Icons.Branch");
            submenu.Header = current.Name;

            var createPR = AzureDevOpsPullRequestMenu.Create(this, repo, current);
            if (createPR != null)
                submenu.Items.Add(createPR);

            var visibility = new MenuItem();
            visibility.Classes.Add("filter_mode_switcher");
            visibility.Header = new ViewModels.FilterModeInGraph(repo, current);
            submenu.Items.Add(visibility);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            if (!string.IsNullOrEmpty(current.Upstream))
            {
                var upstream = current.Upstream.Substring(13);

                var fastForward = new MenuItem();
                fastForward.Header = App.ContextMenuText("BranchCM.FastForward", upstream);
                fastForward.Icon = this.CreateMenuIcon("Icons.FastForward");
                fastForward.IsEnabled = current.Ahead.Count == 0 && current.Behind.Count > 0;
                fastForward.Click += async (_, e) =>
                {
                    var b = repo.Branches.Find(x => x.FriendlyName == upstream);
                    if (b == null)
                        return;

                    if (repo.CanCreatePopup())
                        await repo.ShowAndStartPopupAsync(new ViewModels.Merge(repo, b, current.Name, true));

                    e.Handled = true;
                };
                submenu.Items.Add(fastForward);

                var pull = new MenuItem();
                pull.Header = App.Text("BranchCM.Pull", upstream);
                pull.Icon = this.CreateMenuIcon("Icons.Pull");
                pull.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.Pull(repo, null));
                    e.Handled = true;
                };
                submenu.Items.Add(pull);
            }

            var push = new MenuItem();
            push.Header = App.Text("BranchCM.Push", current.Name);
            push.Icon = this.CreateMenuIcon("Icons.Push");
            push.IsEnabled = repo.Remotes.Count > 0;
            push.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.Push(repo, current));
                e.Handled = true;
            };
            submenu.Items.Add(push);

            var rename = new MenuItem();
            rename.Header = App.ContextMenuText("BranchCM.Rename", current.Name);
            rename.Icon = this.CreateMenuIcon("Icons.Rename");
            rename.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.RenameBranch(repo, current));
                e.Handled = true;
            };
            submenu.Items.Add(rename);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            if (!repo.IsBare)
            {
                var type = repo.GetGitFlowType(current);
                if (type != Models.GitFlowBranchType.None)
                {
                    var finish = new MenuItem();
                    finish.Header = App.Text("BranchCM.Finish", current.Name);
                    finish.Icon = this.CreateMenuIcon("Icons.GitFlow.Finish");
                    finish.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.GitFlowFinish(repo, current, type));
                        e.Handled = true;
                    };
                    submenu.Items.Add(finish);
                    submenu.Items.Add(new MenuItem() { Header = "-" });
                }
            }

            var copy = new MenuItem();
            copy.Header = App.Text("BranchCM.CopyName");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, e) =>
            {
                await this.CopyTextAsync(current.Name);
                e.Handled = true;
            };
            submenu.Items.Add(copy);

            menu.Items.Add(submenu);
        }

        private void FillOtherLocalBranchMenu(ContextMenu menu, ViewModels.Repository repo, Models.Branch branch, Models.Branch current, bool merged)
        {
            var submenu = new MenuItem();
            submenu.Icon = this.CreateMenuIcon("Icons.Branch");
            submenu.Header = branch.Name;

            var createPR = AzureDevOpsPullRequestMenu.Create(this, repo, branch);
            if (createPR != null)
                submenu.Items.Add(createPR);

            var visibility = new MenuItem();
            visibility.Classes.Add("filter_mode_switcher");
            visibility.Header = new ViewModels.FilterModeInGraph(repo, branch);
            submenu.Items.Add(visibility);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            if (!repo.IsBare)
            {
                var hasNoWorktree = string.IsNullOrEmpty(branch.WorktreePath);

                if (hasNoWorktree)
                {
                    var checkout = new MenuItem();
                    checkout.Header = App.ContextMenuText("BranchCM.Checkout", branch.Name);
                    checkout.Icon = this.CreateMenuIcon("Icons.Check");
                    checkout.Click += async (_, e) =>
                    {
                        await repo.CheckoutBranchAsync(branch);
                        e.Handled = true;
                    };
                    submenu.Items.Add(checkout);

                    var checkoutAsWorktree = new MenuItem();
                    checkoutAsWorktree.Header = App.Text("BranchCM.CheckoutAsWorktree", branch.Name);
                    checkoutAsWorktree.Icon = this.CreateMenuIcon("Icons.Worktree.Add");
                    checkoutAsWorktree.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.CheckoutAsWorktree(repo, branch));

                        e.Handled = true;
                    };
                    submenu.Items.Add(checkoutAsWorktree);
                }
                else
                {
                    var checkout = new MenuItem();
                    checkout.Header = App.Text("BranchCM.SwitchToWorktree", branch.Name);
                    checkout.Icon = this.CreateMenuIcon("Icons.Check");
                    checkout.Click += async (_, e) =>
                    {
                        await repo.CheckoutBranchAsync(branch);
                        e.Handled = true;
                    };
                    submenu.Items.Add(checkout);
                }

                var merge = new MenuItem();
                merge.Header = App.Text("BranchCM.Merge", branch.Name, current.Name);
                merge.Icon = this.CreateMenuIcon("Icons.Merge");
                merge.IsEnabled = !merged;
                merge.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.Merge(repo, branch, current.Name, false));
                    e.Handled = true;
                };
                submenu.Items.Add(merge);
            }
            else
            {
                var checkout = new MenuItem();
                checkout.Header = App.Text("BranchCM.SwitchToWorktree", branch.Name);
                checkout.Icon = this.CreateMenuIcon("Icons.Check");
                checkout.Click += async (_, e) =>
                {
                    await repo.CheckoutBranchAsync(branch);
                    e.Handled = true;
                };
                submenu.Items.Add(checkout);
            }

            var push = new MenuItem();
            push.Header = App.Text("BranchCM.Push", branch.Name);
            push.Icon = this.CreateMenuIcon("Icons.Push");
            push.IsEnabled = repo.Remotes.Count > 0;
            push.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.Push(repo, branch));
                e.Handled = true;
            };
            submenu.Items.Add(push);

            var rename = new MenuItem();
            rename.Header = App.ContextMenuText("BranchCM.Rename", branch.Name);
            rename.Icon = this.CreateMenuIcon("Icons.Rename");
            rename.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.RenameBranch(repo, branch));
                e.Handled = true;
            };
            submenu.Items.Add(rename);

            var delete = new MenuItem();
            delete.Header = App.ContextMenuText("BranchCM.Delete", branch.Name);
            delete.Icon = this.CreateMenuIcon("Icons.Clear");
            delete.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.DeleteBranch(repo, branch));
                e.Handled = true;
            };
            submenu.Items.Add(delete);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            if (!repo.IsBare)
            {
                var type = repo.GetGitFlowType(branch);
                if (type != Models.GitFlowBranchType.None)
                {
                    var finish = new MenuItem();
                    finish.Header = App.Text("BranchCM.Finish", branch.Name);
                    finish.Icon = this.CreateMenuIcon("Icons.GitFlow.Finish");
                    finish.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.GitFlowFinish(repo, branch, type));
                        e.Handled = true;
                    };
                    submenu.Items.Add(finish);
                    submenu.Items.Add(new MenuItem() { Header = "-" });
                }
            }

            var compare = new MenuItem();
            compare.Header = App.Text("BranchCM.CompareWithSpecial", current.Name);
            compare.Icon = this.CreateMenuIcon("Icons.Compare");
            compare.Click += (_, e) =>
            {
                this.ShowWindow(new ViewModels.Compare(repo, current, branch));
                e.Handled = true;
            };

            submenu.Items.Add(compare);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var copy = new MenuItem();
            copy.Header = App.Text("BranchCM.CopyName");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, e) =>
            {
                await this.CopyTextAsync(branch.Name);
                e.Handled = true;
            };
            submenu.Items.Add(copy);

            menu.Items.Add(submenu);
        }

        private void FillRemoteBranchMenu(ContextMenu menu, ViewModels.Repository repo, Models.Branch branch, Models.Branch current, bool merged)
        {
            if (branch == null)
                return;

            var name = branch.FriendlyName;

            var submenu = new MenuItem();
            submenu.Icon = this.CreateMenuIcon("Icons.Branch");
            submenu.Header = name;

            var createPR = AzureDevOpsPullRequestMenu.Create(this, repo, branch);
            if (createPR != null)
                submenu.Items.Add(createPR);

            var visibility = new MenuItem();
            visibility.Classes.Add("filter_mode_switcher");
            visibility.Header = new ViewModels.FilterModeInGraph(repo, branch);
            submenu.Items.Add(visibility);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var checkout = new MenuItem();
            checkout.Header = App.ContextMenuText("BranchCM.Checkout", name);
            checkout.Icon = this.CreateMenuIcon("Icons.Check");
            checkout.IsEnabled = !repo.IsBare;
            checkout.Click += async (_, e) =>
            {
                await repo.CheckoutBranchAsync(branch);
                e.Handled = true;
            };
            submenu.Items.Add(checkout);

            var checkoutAsWorktree = new MenuItem();
            checkoutAsWorktree.Header = App.Text("BranchCM.CheckoutAsWorktree", name);
            checkoutAsWorktree.Icon = this.CreateMenuIcon("Icons.Worktree.Add");
            checkoutAsWorktree.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.CheckoutRemoteBranchAsWorktree(repo, branch));
                e.Handled = true;
            };
            submenu.Items.Add(checkoutAsWorktree);

            var merge = new MenuItem();
            merge.Header = App.Text("BranchCM.Merge", name, current.Name);
            merge.Icon = this.CreateMenuIcon("Icons.Merge");
            merge.IsEnabled = !merged;
            merge.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.Merge(repo, branch, current.Name, false));
                e.Handled = true;
            };
            submenu.Items.Add(merge);

            var delete = new MenuItem();
            delete.Header = App.ContextMenuText("BranchCM.Delete", name);
            delete.Icon = this.CreateMenuIcon("Icons.Clear");
            delete.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.DeleteBranch(repo, branch));
                e.Handled = true;
            };
            submenu.Items.Add(delete);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var compare = new MenuItem();
            compare.Header = App.Text("BranchCM.CompareWithSpecial", current.Name);
            compare.Icon = this.CreateMenuIcon("Icons.Compare");
            compare.Click += (_, e) =>
            {
                this.ShowWindow(new ViewModels.Compare(repo, current, branch));
                e.Handled = true;
            };

            submenu.Items.Add(compare);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var copy = new MenuItem();
            copy.Header = App.Text("BranchCM.CopyName");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, e) =>
            {
                await this.CopyTextAsync(name);
                e.Handled = true;
            };
            submenu.Items.Add(copy);

            menu.Items.Add(submenu);
        }

        private void FillTagMenu(ContextMenu menu, ViewModels.Repository repo, Models.Tag tag, Models.Branch current)
        {
            var submenu = new MenuItem();
            submenu.Header = tag.Name;
            submenu.Icon = this.CreateMenuIcon("Icons.Tag");
            submenu.MinWidth = 200;

            var visibility = new MenuItem();
            visibility.Classes.Add("filter_mode_switcher");
            visibility.Header = new ViewModels.FilterModeInGraph(repo, tag);
            submenu.Items.Add(visibility);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var push = new MenuItem();
            push.Header = App.Text("TagCM.Push", tag.Name);
            push.Icon = this.CreateMenuIcon("Icons.Push");
            push.IsEnabled = repo.Remotes.Count > 0;
            push.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.PushTag(repo, tag));
                e.Handled = true;
            };
            submenu.Items.Add(push);

            var delete = new MenuItem();
            delete.Header = App.Text("TagCM.Delete", tag.Name);
            delete.Icon = this.CreateMenuIcon("Icons.Clear");
            delete.Click += (_, e) =>
            {
                if (repo.CanCreatePopup())
                    repo.ShowPopup(new ViewModels.DeleteTag(repo, tag));
                e.Handled = true;
            };
            submenu.Items.Add(delete);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var compare = new MenuItem();
            compare.Header = App.Text("BranchCM.CompareWithSpecial", current.Name);
            compare.Icon = this.CreateMenuIcon("Icons.Compare");
            compare.Click += (_, e) =>
            {
                this.ShowWindow(new ViewModels.Compare(repo, current, tag));
                e.Handled = true;
            };

            submenu.Items.Add(compare);
            submenu.Items.Add(new MenuItem() { Header = "-" });

            var copy = new MenuItem();
            copy.Header = App.Text("TagCM.CopyName");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, e) =>
            {
                await this.CopyTextAsync(tag.Name);
                e.Handled = true;
            };
            submenu.Items.Add(copy);

            menu.Items.Add(submenu);
        }

        private async Task InteractiveRebaseWithPrefillActionAsync(ViewModels.Repository repo, Models.Commit target, Models.InteractiveRebaseAction action)
        {
            var prefill = new ViewModels.InteractiveRebasePrefill(target.SHA, action);
            var start = action switch
            {
                Models.InteractiveRebaseAction.Squash or Models.InteractiveRebaseAction.Fixup => $"{target.SHA}~~",
                _ => $"{target.SHA}~",
            };

            var on = await new Commands.QuerySingleCommit(repo.FullPath, start).GetResultAsync();
            if (on == null)
                repo.SendNotification($"Commit '{start}' is not a valid revision for `git rebase -i`!", true);
            else
                await this.ShowDialogAsync(new ViewModels.InteractiveRebase(repo, on, prefill));
        }

        private ViewModels.Histories _navigationHistories = null;
        private Models.Branch _currentBranch = null;
        private Models.Bisect _bisect = null;
        private bool _hasSingleRemote = false;
        private AvaloniaList<Models.IssueTracker> _issueTrackers = null;
        private bool _isScrollToTopVisible = false;
        private bool _isDetailsPanelExpanded = true;
        private bool _resizingAuthorColumn = false;
        private bool _resizingBranchesColumn = false;
        private double _branchesResizeStartX = 0;
        private double _branchesResizeStartWidth = 0;
        private double _authorResizeStartX = 0;
        private double _authorResizeStartWidth = 0;
        private bool _restoringColumnOrder = false;
        private bool _pendingColumnOrderSave = false;
    }
}
