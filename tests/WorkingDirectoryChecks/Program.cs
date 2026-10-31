using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.Models;
using SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--record-diff", var record, var local, var remote])
        {
            File.WriteAllText(record + ".tmp", JsonSerializer.Serialize(new[] { File.ReadAllText(local), File.ReadAllText(remote) }));
            File.Move(record + ".tmp", record, true);
            return;
        }

        var fixture = Path.Combine(Path.GetTempPath(), "sourcegit-working-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = fixture;
        SourceGit.Native.OS.BasicDirectories.CacheDir = fixture;
        Repository repo = null;
        Window window = null;
        try
        {
            App.BuildAvaloniaApp().SetupWithoutStarting();
            CheckHeadPreference(fixture);
            Preferences.Instance.WorkingDirectoryHeadMode = 1;
            CheckClassification();
            CheckGraph();
            CheckScrolledGraphRendering();
            if (args.Contains("--graph-only"))
            {
                Console.WriteLine($"PASS: {_checks} graph checks");
                return;
            }
            CheckBranchSummaryRendering();
            CheckGraphBranchContext();
            Git(fixture, "init -b main");
            Git(fixture, "config user.name Test");
            Git(fixture, "config user.email test@example.com");
            File.WriteAllText(Path.Combine(fixture, "tracked.txt"), "initial\n");
            Git(fixture, "add tracked.txt");
            Git(fixture, "commit -m initial");
            Git(fixture, "tag v1");
            File.WriteAllText(Path.Combine(fixture, "tracked.txt"), "edited\n");
            repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
            repo.UIStates.IsBranchesColumnVisibleInHistory = false;
            repo.UIStates.IsTreeColumnVisibleInHistory = false;
            repo.UIStates.HistoryColumnOrder = [2, 0, 1, 3, 4];
            repo.Open();
            window = new Window { Title = "SourceGit Working Directory Checks", Width = 1100, Height = 800,
                Content = new Views.Repository { DataContext = repo } };
            window.Show();
            PumpUntil(() => !repo.Histories.IsLoading && repo.LocalChangesCount == 1);
            CheckBranchesColumn(repo, window, fixture);
            CheckBranchContext(repo, window);
            CheckTreeColumn(repo, window, fixture);
            CheckExternalDiffDoubleClick(repo, window, fixture);
            CheckSelection(repo, window);
            CheckWorkingDirectoryActions(repo, window);
            CheckLiveTranslations(window);
            CheckRowHitTesting(repo, window);
            CheckStatusPipeline(repo, fixture);
            CheckCommitRefreshRace(repo, fixture);
            CheckMultipleCommitSelection(repo, window);
            CheckHeadModes(repo, window);
            CheckLayouts(repo, window, args.Contains("--render"));
            CheckHeadDropdown(args.Contains("--render"));
            Console.WriteLine($"PASS: {_checks} working-directory checks");
            if (args.Contains("--ui"))
            {
                using var cancellation = new CancellationTokenSource();
                window.Closed += (_, _) => cancellation.Cancel();
                Dispatcher.UIThread.MainLoop(cancellation.Token);
            }
        }
        finally
        {
            window?.Close();
            repo?.Close();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Fixture must stay inside the temporary directory");
                    foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(fixture, true);
                    break;
                }
                catch (IOException) when (attempt < 9) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(50); }
            }
        }
    }

    private static void CheckExternalDiffDoubleClick(Repository repo, Window window, string fixture)
    {
        var previousType = SourceGit.Native.OS.ExternalMergerType;
        var previousPath = SourceGit.Native.OS.ExternalMergerExecFile;
        var previousArgs = SourceGit.Native.OS.ExternalDiffArgs;
        var previousView = repo.SelectedViewIndex;
        var transcript = Path.Combine(fixture, ".git", "external-diff.json");
        var configKeys = new[] { "diff.guitool", "merge.guitool", "diff.tool", "merge.tool" };
        var wc = repo.WorkingCopy;
        try
        {
            foreach (var key in configKeys)
                Git(fixture, $"config --local {key} \"\"");
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "WorkingDirectoryChecks.exe" : "WorkingDirectoryChecks");
            SourceGit.Native.OS.ExternalMergerType = 1;
            SourceGit.Native.OS.ExternalMergerExecFile = executable;
            SourceGit.Native.OS.ExternalDiffArgs = $"--record-diff \"{transcript}\" \"$LOCAL\" \"$REMOTE\"";
            Check(SourceGit.Commands.DiffTool.IsConfigured(fixture), "configured application diff tool is detected");
            File.WriteAllText(Path.Combine(fixture, "tracked.txt"), "staged\n");
            Git(fixture, "add tracked.txt");
            File.WriteAllText(Path.Combine(fixture, "tracked.txt"), "unstaged\n");
            repo.MarkWorkingCopyDirtyManually();
            repo.SelectedViewIndex = 1;
            PumpUntil(() => wc.Unstaged.Count == 1 && wc.Staged.Count == 1 && window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
            var editor = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single();
            var unstaged = editor.FindControl<Views.ChangeCollectionView>("UnstagedChangesView");
            var staged = editor.FindControl<Views.ChangeCollectionView>("StagedChangesView");
            wc.SelectedUnstaged = new(new List<Change> { wc.Unstaged[0] });
            Dispatcher.UIThread.RunJobs();
            unstaged.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Views.ChangeCollectionView.ChangeDoubleTappedEvent));
            PumpUntil(() => File.Exists(transcript));
            var comparison = JsonSerializer.Deserialize<string[]>(File.ReadAllText(transcript));
            Check(comparison.SequenceEqual(new[] { "staged\n", "unstaged\n" }), "unstaged double-click opens the existing index-to-worktree external diff");
            Check(Git(fixture, "show :tracked.txt") == "staged\n", "external diff does not stage the file");
            File.Delete(transcript);
            wc.SelectedStaged = new(new List<Change> { wc.Staged[0] });
            Dispatcher.UIThread.RunJobs();
            staged.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Views.ChangeCollectionView.ChangeDoubleTappedEvent));
            PumpUntil(() => File.Exists(transcript));
            comparison = JsonSerializer.Deserialize<string[]>(File.ReadAllText(transcript));
            Check(comparison.SequenceEqual(new[] { "initial\n", "staged\n" }), "staged double-click opens the existing HEAD-to-index external diff");
            Check(Git(fixture, "show :tracked.txt") == "staged\n", "external diff does not unstage the file");
            SourceGit.Native.OS.ExternalMergerType = 0;
            SourceGit.Native.OS.ExternalMergerExecFile = string.Empty;
            SourceGit.Native.OS.ExternalDiffArgs = string.Empty;
            Check(!SourceGit.Commands.DiffTool.IsConfigured(fixture), "no configured external diff tool is detected without opening one");
            wc.SelectedUnstaged = new(new List<Change> { wc.Unstaged[0] });
            Dispatcher.UIThread.RunJobs();
            unstaged.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Views.ChangeCollectionView.ChangeDoubleTappedEvent));
            PumpUntil(() => wc.Unstaged.Count == 0 && wc.Staged.Count == 1);
            Check(Git(fixture, "show :tracked.txt") == "unstaged\n", "unstaged double-click keeps staging behavior when no diff tool is configured");
            wc.SelectedStaged = new(new List<Change> { wc.Staged[0] });
            Dispatcher.UIThread.RunJobs();
            staged.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Views.ChangeCollectionView.ChangeDoubleTappedEvent));
            PumpUntil(() => wc.Staged.Count == 0 && wc.Unstaged.Count == 1);
            Check(Git(fixture, "show :tracked.txt") == "initial\n", "staged double-click keeps unstaging behavior when no diff tool is configured");
            Git(fixture, "config --local diff.guitool capture");
            Check(SourceGit.Commands.DiffTool.IsConfigured(fixture), "repository-configured Git diff tools are detected");
            Git(fixture, "config --local diff.guitool vimdiff");
            Check(!SourceGit.Commands.DiffTool.IsConfigured(fixture), "unsupported CLI diff tools do not replace staging actions");
        }
        finally
        {
            SourceGit.Native.OS.ExternalMergerType = previousType;
            SourceGit.Native.OS.ExternalMergerExecFile = previousPath;
            SourceGit.Native.OS.ExternalDiffArgs = previousArgs;
            foreach (var key in configKeys)
                Git(fixture, $"config --local --unset-all {key}");
            Git(fixture, "reset -q HEAD -- tracked.txt");
            File.WriteAllText(Path.Combine(fixture, "tracked.txt"), "edited\n");
            repo.MarkWorkingCopyDirtyManually();
            repo.SelectedViewIndex = previousView;
            PumpUntil(() => wc.Staged.Count == 0 && wc.Unstaged.Count == 1);
        }
    }

    private static void CheckBranchSummaryRendering()
    {
        var commit = new Commit();
        commit.ParseDecorators("HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1");
        var label = new Views.CommitRefsPresenter { ShowBranchSummary = true, FontSize = 12, DataContext = commit };
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(label.BranchSummaryHasRemote && label.BranchSummaryText == "main", "matching local and remote use one local badge with both icons");
        Check(label.BranchSummaryAdditionalCount == 0 && label.BranchSummarySuffixWidth == 0, "matching local and remote count as one label");
        Check(Math.Abs(label.DesiredSize.Width - label.BranchSummaryRight - 1.5) <= 1,
            "single summary badge reserves no count space or multi-label gutter");
        var pairedWidth = label.BranchSummaryRight;
        var local = new Commit();
        local.ParseDecorators("HEAD -> refs/heads/main");
        label.DataContext = local;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(Math.Abs(pairedWidth - label.BranchSummaryRight - 12) < 0.01, "combined badge reserves compact space for the cloud icon");
        label.DataContext = commit;
        commit.ParseDecorators("refs/heads/topic, refs/remotes/origin/topic, refs/remotes/upstream/topic, refs/remotes/origin/other");
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(label.BranchSummaryAdditionalCount == 2, "extra count groups matching branches even when they are not the displayed label");
        Check(Math.Abs(label.DesiredSize.Width - label.BranchSummaryRight - 1.5 - label.BranchSummarySuffixWidth) <= 1,
            "summary reserves postfix space only when additional labels exist");
        Check(commit.BranchToolTip.Contains("upstream/topic") && commit.BranchToolTip.Contains("origin/main"), "tooltip retains every grouped ref name");

        var remoteOnly = new Commit();
        remoteOnly.ParseDecorators("refs/remotes/origin/topic, refs/remotes/upstream/topic");
        label.DataContext = remoteOnly;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(!label.BranchSummaryHasRemote && label.BranchSummaryAdditionalCount == 1, "remote refs stay separate when no matching local is on the commit");
        Check(label.BranchSummaryText == "origin/topic", "remote-only labels retain the remote prefix");

        var longName = "feature/" + string.Concat(Enumerable.Repeat("árvíztűrő-e\u0301-😀/", 15));
        var longCommit = new Commit();
        longCommit.ParseDecorators($"HEAD -> refs/heads/{longName}, refs/remotes/origin/{longName}, refs/heads/other");
        label.DataContext = longCommit;
        label.Measure(new Size(84, 26));
        label.Arrange(new Rect(0, 0, 84, 26));
        Check(label.BranchSummaryText.EndsWith("...", StringComparison.Ordinal), "long branch names use a trailing ellipsis");
        Check(label.BranchSummaryRight + label.BranchSummarySuffixWidth <= 84, "badge and external count fit inside a minimum-width column");
        Check(label.BranchSummaryHasRemote && label.BranchSummaryAdditionalCount == 1, "truncation preserves icons and the grouped count");
        Check(!label.BranchSummaryText.EnumerateRunes().Any(x => x.Value == 0xFFFD), "truncation preserves Unicode characters");
        Check(longCommit.BranchToolTip.Contains(longName), "tooltip retains the full untruncated branch name");
        label.Measure(new Size(4096, 26));
        label.Arrange(new Rect(0, 0, 4096, 26));
        Check(label.BranchSummaryText == longName, "widening the column restores the full name");
        label.Arrange(new Rect(0, 0, 84, 26));
        Check(label.BranchSummaryText.EndsWith("...", StringComparison.Ordinal) && label.BranchSummaryRight + label.BranchSummarySuffixWidth <= 84,
            "arrange also constrains a badge measured at a larger width");
        label.ShowBranchSummary = false;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(!label.BranchSummaryHasRemote && label.BranchSummarySuffixWidth == 0 && label.DesiredSize.Width > 84,
            "other ref presenters retain their existing rendering behavior");
    }

    private static void CheckGraphBranchContext()
    {
        var root = new Commit { SHA = "root" };
        var mainParent = new Commit { SHA = "main-parent", Parents = [root.SHA] };
        var featureParent = new Commit { SHA = "feature-parent", Parents = [root.SHA] };
        var feature = new Commit { SHA = "feature", Parents = [featureParent.SHA] };
        feature.ParseDecorators("refs/heads/feature, refs/remotes/origin/feature");
        var main = new Commit { SHA = "main", Parents = [mainParent.SHA, feature.SHA] };
        main.ParseDecorators("HEAD -> refs/heads/main");
        CommitGraph.Generate([main, feature, mainParent, featureParent, root], false, CommitGraphHighlighting.All, []);
        Check(mainParent.GraphBranches.Single().Name == "main", "main ancestry inherits the main lane branch");
        Check(featureParent.GraphBranches.Select(x => x.Name).SequenceEqual(new[] { "feature", "origin/feature" }),
            "merged feature ancestry inherits its own lane branch and remote");
        Check(root.GraphBranches.Single().Name == "main", "shared ancestry follows the active branch lane");
        CommitGraph.Generate([root], false, CommitGraphHighlighting.All, []);
        Check(root.GraphBranches.Count == 0, "graph regeneration clears branch context when no known branch reaches a commit");
    }

    private static void CheckBranchContext(Repository repo, Window window)
    {
        var vm = repo.Histories;
        var original = vm.Commits;
        var ancestor = new Commit { SHA = new string('a', 40), Subject = "Ancestor without refs" };
        var tip = new Commit { SHA = original[0].SHA, Subject = original[0].Subject, Parents = [ancestor.SHA] };
        tip.ParseDecorators("HEAD -> refs/heads/main, refs/remotes/origin/main");
        try
        {
            vm.Commits = [tip, ancestor];
            vm.IsBranchesColumnVisible = true;
            var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
            PumpUntil(() => list.GetVisualDescendants().OfType<DataGridRow>().Any(x => ReferenceEquals(x.DataContext, ancestor)));
            var row = list.GetVisualDescendants().OfType<DataGridRow>().Single(x => ReferenceEquals(x.DataContext, ancestor));
            var cell = row.GetVisualDescendants().OfType<Border>().Single(x => x.Child is Views.CommitRefsPresenter { ShowBranchSummary: true });
            var label = (Views.CommitRefsPresenter)cell.Child;
            Check(label.BranchSummaryRight == 0 && !label.IsContextBranchSummary, "unlabeled branch cell is blank before hover");
            list.SelectedItem = ancestor;
            PumpUntil(() => label.BranchSummaryText == "main");
            Check(label.Opacity == 0.7 && label.BranchSummaryHasRemote && label.BranchSummaryAdditionalCount == 0,
                "selected row shows a grouped branch badge at 70 percent opacity");
            Check(label.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.LocalBranchHead, "ancestor hover badge does not claim to be HEAD");
            var connections = window.GetVisualDescendants().OfType<Views.CommitBranchConnections>().Single();
            var graph = window.GetVisualDescendants().OfType<Views.CommitGraph>().Single();
            var ancestorDot = vm.Graph.Dots[2];
            var ancestorY = graph.TranslatePoint(new Point(ancestorDot.Center.X, ancestorDot.Center.Y * graph.Layout.RowHeight - graph.Layout.StartY), connections).Value.Y;
            Check(!connections.Connections.Any(x => x.End.Y == ancestorY), "hover badges do not draw connecting lines");
            Check(ToolTip.GetTip(cell) as string == "main\norigin/main", "hover tooltip includes all full branch names");
            Check(ancestor.Decorators.Count == 0, "hover does not add fake branch refs to the commit");
            list.SelectedItem = tip;
            PumpUntil(() => label.BranchSummaryRight == 0);
            Check(!label.IsContextBranchSummary && label.Opacity == 1 && ToolTip.GetTip(cell) == null, "deselecting the unhovered row removes its branch badge and tooltip");
            var tipRow = list.GetVisualDescendants().OfType<DataGridRow>().Single(x => ReferenceEquals(x.DataContext, tip));
            var tipCell = tipRow.GetVisualDescendants().OfType<Border>().Single(x => x.Child is Views.CommitRefsPresenter { ShowBranchSummary: true });
            var tipLabel = (Views.CommitRefsPresenter)tipCell.Child;
            Check(tipLabel.Opacity == 1 && !tipLabel.IsContextBranchSummary, "existing branch tip labels stay opaque on hover");
            label.ShowBranchContext = true;
            label.Measure(new Size(180, 26));
            label.DataContext = tip;
            label.Measure(new Size(180, 26));
            Check(!label.IsContextBranchSummary && label.Opacity == 1, "recycled cells clear hover state when their commit changes");
        }
        finally
        {
            vm.IsBranchesColumnVisible = false;
            vm.Commits = original;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void CheckBranchesColumn(Repository repo, Window window, string fixture)
    {
        var commit = new Commit();
        Check(commit.BranchSummary.Length == 0 && commit.BranchToolTip == null, "undecorated commits have blank branch cells");
        commit.ParseDecorators("HEAD, tag: refs/tags/v1");
        Check(commit.BranchSummary.Length == 0 && commit.BranchToolTip == "HEAD", "detached HEAD is included in branch-column tooltips while tags are excluded");
        commit.Decorators.Clear();
        commit.ParseDecorators("refs/remotes/origin/feature/topic");
        Check(commit.BranchSummary == "origin/feature/topic", "single remote branch retains its full name");
        commit.ParseDecorators("refs/heads/feature/topic");
        Check(commit.BranchSummary == "feature/topic +1", "local branch takes priority over remote branch");
        commit.ParseDecorators("HEAD -> refs/heads/main");
        Check(commit.BranchSummary == "main +2", "current branch takes priority and counts additional branches");
        Check(commit.BranchToolTip == "main\nfeature/topic\norigin/feature/topic", "tooltip includes every full branch name and no other refs");

        var vm = repo.Histories;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        var column = list.Columns[5];
        Check(!vm.IsBranchesColumnVisible && !column.IsVisible, "fixture starts with branches column hidden");
        var message = list.GetVisualDescendants().OfType<Views.CommitMessagePanel>().Single();
        Check(!message.RefsAfterSubject && message.Children[0].Bounds.Right <= message.Children[1].Bounds.Left,
            "message refs precede the subject when the branch column is hidden");
        Check(list.Columns.OrderBy(x => x.DisplayIndex).Select(x => list.Columns.IndexOf(x)).SequenceEqual(new[] { 5, 2, 6, 0, 1, 3, 4 }),
            "new branch and tree columns preserve existing saved source-column indexes");
        var graph = window.GetVisualDescendants().OfType<Views.CommitGraph>().Single();
        vm.IsBranchesColumnVisible = true;
        PumpUntil(() => column.IsVisible && column.ActualWidth > 0 && graph.Margin.Left >= column.ActualWidth);
        PumpUntil(() => message.RefsAfterSubject && message.Children[1].Bounds.Right < message.Children[0].Bounds.Left);
        Check(message.Children[0].Bounds.Left == message.Children[1].Bounds.Right + 4,
            "message refs follow immediately after the subject when the branch column is visible");
        var messageRefs = (Views.CommitRefsPresenter)message.Children[0];
        Check(!messageRefs.ShowBranches && messageRefs.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.Tag,
            "branch column hides message branch badges while preserving tag badges");
        var subject = (Views.CommitSubjectPresenter)message.Children[1];
        var originalSubject = subject.Subject;
        subject.Subject = new string('x', 1000);
        PumpUntil(() => subject.Bounds.Width > 0 && message.Children[0].Bounds.Right <= message.Bounds.Width);
        Check(message.Children[0].Bounds.Width > 0, "long messages leave room for trailing tag labels");
        subject.Subject = originalSubject;
        var workingRow = list.GetVisualDescendants().OfType<DataGridRow>().Single(x => ReferenceEquals(x.DataContext, vm.WorkingDirectory));
        var workingCell = workingRow.GetVisualDescendants().OfType<Border>().Single(x => x.Child is Views.CommitRefsPresenter { ShowBranchSummary: true });
        var workingLabel = (Views.CommitRefsPresenter)workingCell.Child;
        PumpUntil(() => workingLabel.BranchSummaryText == "main");
        Check(workingLabel.Opacity == 0.7 && workingLabel.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.LocalBranchHead,
            "working-directory row permanently shows the active branch at 70 percent opacity");
        Check(ReferenceEquals(workingLabel.BranchSummaryBrush, vm.WorkingDirectory.BranchBrush) && ToolTip.GetTip(workingCell) as string == "main",
            "working-directory badge uses the graph color and full-name tooltip");
        var activeBranch = vm.CurrentBranch;
        vm.CurrentBranch = new Branch { Name = "next-branch", IsLocal = true };
        PumpUntil(() => workingLabel.BranchSummaryText == "next-branch");
        Check(workingLabel.Opacity == 0.7, "working-directory branch changes preserve the badge opacity");
        vm.CurrentBranch = new Branch { IsDetachedHead = true };
        PumpUntil(() => workingLabel.BranchSummaryText == "HEAD");
        Check(workingLabel.Opacity == 0.7 && workingLabel.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.CurrentCommitHead,
            "detached working-directory HEAD uses the HEAD badge at 70 percent opacity");
        Check(ReferenceEquals(workingLabel.BranchSummaryBrush, vm.WorkingDirectory.BranchBrush) && ToolTip.GetTip(workingCell) as string == "HEAD",
            "detached working-directory HEAD retains graph color and updates its tooltip");
        vm.CurrentBranch = null;
        PumpUntil(() => workingLabel.BranchSummaryRight == 0);
        Check(vm.WorkingDirectory.Branch == null && ToolTip.GetTip(workingCell) == null,
            "missing current branch clears the working-directory badge and tooltip");
        vm.CurrentBranch = activeBranch;
        PumpUntil(() => workingLabel.BranchSummaryText == "main");
        var cell = list.GetVisualDescendants().OfType<Border>().Single(x => x.DataContext is Commit && ToolTip.GetTip(x) as string == "main");
        Check(cell.Bounds.Width > 0, "branch tooltip is attached to the visible cell");
        var label = cell.GetVisualDescendants().OfType<Views.CommitRefsPresenter>().Single();
        Check(label.ShowBranchSummary && label.DecoratorAt(new Point(4, 4))?.Name == "main", "branch column uses the shared colored label renderer");
        Check(label.UseGraphColor == vm.Commits[0].IsHighlightedInGraph, "branch column follows the message label graph colors");
        label.DataContext = commit;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(label.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.CurrentBranchHead, "colored summary badge uses the primary branch icon");
        var badgeRight = label.BranchSummaryRight;
        Check(label.BranchSummarySuffixWidth > 0, "multiple branches have a separate count postfix");
        var singleBranch = new Commit();
        singleBranch.ParseDecorators("HEAD -> refs/heads/main");
        label.DataContext = singleBranch;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(label.BranchSummaryRight == badgeRight && label.BranchSummarySuffixWidth == 0,
            "count postfix does not change the colored badge width and is omitted for one branch");
        label.DataContext = commit;
        label.Measure(new Size(double.PositiveInfinity, 26));
        var summaryWidth = label.DesiredSize.Width;
        label.ShowBranchSummary = false;
        label.Measure(new Size(double.PositiveInfinity, 26));
        Check(label.DesiredSize.Width > summaryWidth, "summary renders one badge instead of every ref");
        label.ShowBranchSummary = true;
        label.DataContext = vm.Commits[0];
        var connections = window.GetVisualDescendants().OfType<Views.CommitBranchConnections>().Single();
        PumpUntil(() => connections.Connections.Count == 0);
        Check(connections.Connections.Count == 0, "intervening SHA column suppresses branch connectors");
        vm.IsSHAColumnVisible = false;
        PumpUntil(() => connections.Connections.Count == 1);
        var connection = connections.Connections[0];
        Check(connection.IsActive && ReferenceEquals(connection.Brush, label.BranchSummaryBrush), "active branch connector uses the badge color at full opacity");
        Check(connection.Start.Y == connection.End.Y && connection.Start.X < connection.End.X, "connector runs horizontally from badge toward commit dot");
        var dot = vm.Graph.Dots[1];
        var expectedEnd = graph.TranslatePoint(new Point(dot.Center.X, dot.Center.Y * graph.Layout.RowHeight - graph.Layout.StartY), connections).Value;
        Check(connection.End == expectedEnd, "connector ends at the corresponding graph dot");
        label.DataContext = commit;
        PumpUntil(() => connections.Connections.Count == 1 && connections.Connections[0].TextGapWidth > 0);
        Check(connections.Connections[0].TextGapWidth == label.BranchSummarySuffixWidth,
            "connector leaves a gap for the count outside the badge");
        label.DataContext = vm.Commits[0];
        PumpUntil(() => connections.Connections[0].TextGapWidth == 0);
        var header = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "BranchesColumnResizeHandle");
        var pointer = new Pointer(7, PointerType.Mouse, true);
        var resizeStart = header.TranslatePoint(new Point(header.Bounds.Width - 1, header.Bounds.Height / 2), window).Value;
        Check(ReferenceEquals(window.InputHitTest(resizeStart), header), "branch resize handle is reachable beside graph header tools");
        var resizeEnd = resizeStart + new Vector(60, 0);
        var originalWidth = column.ActualWidth;
        header.RaiseEvent(new PointerPressedEventArgs(header, pointer, window, resizeStart, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        header.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, header, pointer, window, resizeEnd, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        header.RaiseEvent(new PointerReleasedEventArgs(header, pointer, window, resizeEnd, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        PumpUntil(() => column.ActualWidth > originalWidth + 50);
        Check(Math.Abs(vm.BranchesColumnWidth - column.ActualWidth) < 0.01, "dragging branch header saves its width");
        PumpUntil(() => connections.Connections[0].End.X > connection.End.X + 50);
        Check(Math.Abs(connections.Connections[0].End.X - connection.End.X - 60) < 0.01, "connector follows column resizing");
        var inactive = new Commit();
        inactive.ParseDecorators("refs/heads/topic");
        label.DataContext = inactive;
        PumpUntil(() => connections.Connections.Count == 1 && !connections.Connections[0].IsActive);
        Check(!connections.Connections[0].IsActive, "other branch connectors use the reduced-opacity rendering path");
        label.DataContext = new Commit();
        PumpUntil(() => connections.Connections.Count == 0);
        label.DataContext = vm.Commits[0];
        PumpUntil(() => connections.Connections.Count == 1);
        vm.IsSHAColumnVisible = true;
        PumpUntil(() => connections.Connections.Count == 0);
        repo.UIStates.Save();
        Check(RepositoryUIStates.Load(Path.Combine(fixture, ".git")).IsBranchesColumnVisibleInHistory, "branch column choice persists per repository");
        Check(Math.Abs(RepositoryUIStates.Load(Path.Combine(fixture, ".git")).BranchesColumnWidth - vm.BranchesColumnWidth) < 0.01,
            "resized branch column width persists per repository");
        var restored = new Views.Histories { DataContext = vm };
        Check(restored.FindControl<Views.HistoriesCommitList>("CommitListContainer").Columns[5].Width.Value == vm.BranchesColumnWidth,
            "new history views restore the saved branch column width");
        vm.IsBranchesColumnVisible = false;
        PumpUntil(() => !column.IsVisible);
        PumpUntil(() => !message.RefsAfterSubject && message.Children[0].Bounds.Right <= message.Children[1].Bounds.Left);
        Check(messageRefs.ShowBranches && messageRefs.DecoratorAt(new Point(4, 4))?.Type == DecoratorType.CurrentBranchHead,
            "hiding the branch column restores message branch badges");
        var expectedOffset = list.Columns.Where(x => x.IsVisible && x.DisplayIndex < list.Columns[0].DisplayIndex).Sum(x => x.ActualWidth);
        PumpUntil(() => Math.Abs(graph.Margin.Left - expectedOffset) < 0.01);
        // Leave the remaining interaction checks in their usual column order.
        for (var i = 0; i < 5; i++)
            list.Columns[i].DisplayIndex = i + 1;
    }

    private static void CheckTreeColumn(Repository repo, Window window, string fixture)
    {
        var vm = repo.Histories;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        var graph = window.GetVisualDescendants().OfType<Views.CommitGraph>().Single();
        var tree = list.Columns[6];
        var originalOrder = list.Columns.OrderBy(x => x.DisplayIndex).ToArray();
        var commit = vm.Commits[0];
        var originalMargin = commit.LeftMargin;
        Check(!vm.IsTreeColumnVisible && !tree.IsVisible, "fixture starts with tree column hidden");
        try
        {
            vm.IsTreeColumnVisible = true;
            PumpUntil(() => tree.IsVisible && tree.ActualWidth > 0);
            var content = list.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("history_message") || x.Classes.Contains("history_working_directory")).ToArray();
            Check(content.Any(x => x.DataContext is WorkingDirectoryEntry) && content.Any(x => x.DataContext is Commit),
                "tree mode retains working-directory and commit message rows");
            Check(content.All(x => x.Padding.Left == 0), "tree mode aligns working-directory and commit message rows");
            var expectedWidth = Math.Max(commit.LeftMargin, vm.WorkingDirectory.LeftMargin) + 4;
            PumpUntil(() => Math.Abs(tree.ActualWidth - expectedWidth) < 0.01);
            Check(tree.ActualWidth < 48, "tree header and tools do not impose a minimum graph width");

            commit.LeftMargin = originalMargin + 200;
            list.InvalidateMeasure();
            PumpUntil(() => tree.ActualWidth >= commit.LeftMargin + 4);
            commit.LeftMargin = originalMargin;
            list.InvalidateMeasure();
            PumpUntil(() => Math.Abs(tree.ActualWidth - expectedWidth) < 0.01);
            Check(tree.ActualWidth < 48, "tree automatically shrinks after the visible lanes narrow");

            tree.DisplayIndex = list.Columns.Count - 1;
            var offset = list.Columns.Where(x => x.IsVisible && x.DisplayIndex < tree.DisplayIndex).Sum(x => x.ActualWidth);
            PumpUntil(() => Math.Abs(graph.Margin.Left - offset) < 0.01);
            Check(graph.Layout.ClipWidth == Math.Max(0, tree.ActualWidth - 4), "graph follows the reordered tree column and clips to its width");
            repo.UIStates.Save();
            Check(RepositoryUIStates.Load(Path.Combine(fixture, ".git")).IsTreeColumnVisibleInHistory,
                "tree visibility persists per repository");
        }
        finally
        {
            commit.LeftMargin = originalMargin;
            vm.IsTreeColumnVisible = false;
            for (var i = 0; i < originalOrder.Length; i++)
                originalOrder[i].DisplayIndex = i;
            PumpUntil(() => !tree.IsVisible);
        }
        var restored = list.GetVisualDescendants().OfType<Border>()
            .Where(x => x.Classes.Contains("history_message") || x.Classes.Contains("history_working_directory"));
        Check(restored.All(x => x.Padding.Left == (x.DataContext is Commit c ? c.LeftMargin : vm.WorkingDirectory.LeftMargin)),
            "hiding tree restores graph indentation for both row types");
    }

    private static void CheckHeadPreference(string fixture)
    {
        var pref = Preferences.Instance;
        Check(pref.WorkingDirectoryHeadMode == 2 && new Preferences().WorkingDirectoryHeadMode == 2, "existing preferences default to Always");
        pref.SetCanModify();
        foreach (var mode in new[] { 0, 1, 2 })
        {
            pref.WorkingDirectoryHeadMode = mode;
            pref.Save();
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "preference.json")));
            Check(json.RootElement.GetProperty("WorkingDirectoryHeadMode").GetInt32() == mode, "all head modes persist through the production serializer");
        }
        pref.WorkingDirectoryHeadMode = -1;
        Check(pref.WorkingDirectoryHeadMode == 2, "unselected dropdown value is ignored");
        pref.WorkingDirectoryHeadMode = 3;
        Check(pref.WorkingDirectoryHeadMode == 2, "invalid stored mode is ignored");
        File.Delete(Path.Combine(fixture, "preference.json"));
    }

    private static void CheckHeadModes(Repository repo, Window window)
    {
        // These checks supply synthetic status snapshots; keep the live watcher
        // from replacing them with the fixture's actual files during UI pumping.
        using var watcherLock = repo.LockWatcher();
        // Let refreshes already running when the watcher was locked finish first.
        PumpUntil(() => !repo.IsRefreshing);
        var vm = repo.Histories;
        var pref = Preferences.Instance;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        using var second = new Histories(repo);
        second.Commits = new List<Commit>(vm.Commits);
        second.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        pref.WorkingDirectoryHeadMode = 0;
        PumpUntil(() => !vm.Entries.Contains(vm.WorkingDirectory));
        Check(vm.Entries.SequenceEqual(vm.Commits.Cast<object>()) && vm.Graph.Dots.Count == vm.Commits.Count, "Hide contains only real commits and graph nodes");
        Check(!vm.IsWorkingDirectorySelected && vm.DetailContext is CommitDetail && list.SelectedItem is Commit, $"hiding selected working directory restores real commit details (working={vm.IsWorkingDirectorySelected}, details={vm.DetailContext?.GetType().Name}, row={list.SelectedItem?.GetType().Name}, commits={vm.SelectedCommits.Count})");
        Check(!second.IsWorkingDirectoryVisible && !second.Entries.Contains(second.WorkingDirectory), "preference changes update every open history");
        vm.UpdateWorkingDirectory([new Change { Path = "new.txt", WorkTree = ChangeState.Untracked }]);
        Check(!vm.Entries.Contains(vm.WorkingDirectory), "status refresh cannot reintroduce hidden head");
        vm.Commits = new List<Commit>(vm.Commits);
        Check(vm.Graph.Dots.Count == vm.Commits.Count && !vm.Entries.Contains(vm.WorkingDirectory), "history refresh cannot reintroduce hidden head");
        vm.IsWorkingDirectorySelected = true;
        Check(!vm.IsWorkingDirectorySelected, "hidden head cannot be selected programmatically");
        pref.WorkingDirectoryHeadMode = 1;
        PumpUntil(() => vm.Entries.Contains(vm.WorkingDirectory));
        Check(vm.WorkingDirectory.Added == 1 && second.Entries.Contains(second.WorkingDirectory), $"conditional mode shows latest changes across histories (added={vm.WorkingDirectory.Added}, secondVisible={second.IsWorkingDirectoryVisible}, secondEntry={second.Entries.Contains(second.WorkingDirectory)})");
        vm.UpdateWorkingDirectory([]);
        Check(!vm.IsWorkingDirectoryVisible && vm.Graph.Dots.Count == vm.Commits.Count, "conditional mode removes clean head");
        pref.WorkingDirectoryHeadMode = 2;
        PumpUntil(() => vm.Entries.Contains(vm.WorkingDirectory));
        Check(vm.IsWorkingDirectoryVisible && vm.WorkingDirectory.Modified + vm.WorkingDirectory.Added + vm.WorkingDirectory.Deleted == 0, "Always shows a clean working directory");
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        var graph = vm.Graph;
        vm.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        vm.UpdateWorkingDirectory([]);
        Check(vm.IsWorkingDirectorySelected && ReferenceEquals(vm.DetailContext, repo.WorkingCopy) && ReferenceEquals(graph, vm.Graph), "Always preserves editor selection and graph across dirty/clean transitions");
        vm.Commits = new List<Commit>(vm.Commits);
        Check(vm.Entries.Count == vm.Commits.Count + 1 && vm.Graph.Dots.Count == vm.Entries.Count, "Always survives history refresh");
        var commits = vm.Commits;
        vm.Commits = [];
        Check(vm.Entries.Count == 1 && vm.Graph.Dots.Count == 1, "Always shows a clean head in empty or filtered history");
        pref.WorkingDirectoryHeadMode = 0;
        Check(vm.Entries.Count == 0 && vm.Graph.Dots.Count == 0 && vm.DetailContext is SourceGit.Models.Null, "Hide restores empty history without stale editor details");
        vm.Commits = commits;
        using (var bare = new Histories(new Repository(true, repo.FullPath, repo.GitDir)))
        {
            pref.WorkingDirectoryHeadMode = 2;
            bare.Commits = [];
            bare.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
            Check(!bare.IsWorkingDirectoryVisible && bare.Entries.Count == 0 && bare.Graph.Dots.Count == 0, "bare repositories never get a working directory head");
        }
        second.Dispose();
        var closedGraph = second.Graph;
        pref.WorkingDirectoryHeadMode = 1;
        Check(ReferenceEquals(second.Graph, closedGraph), "closed histories unsubscribe from preferences");
        vm.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
    }

    private static void CheckHeadDropdown(bool render)
    {
        var pref = Preferences.Instance;
        var mode = pref.WorkingDirectoryHeadMode;
        var dialog = new Views.Preferences();
        dialog.Show();
        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 1;
        PumpUntil(() => dialog.GetVisualDescendants().OfType<ComboBox>().Any(x => Avalonia.Automation.AutomationProperties.GetName(x) == "Working Directory Head"));
        var dropdown = dialog.GetVisualDescendants().OfType<ComboBox>().Single(x => Avalonia.Automation.AutomationProperties.GetName(x) == "Working Directory Head");
        Check(dropdown.Items.OfType<ComboBoxItem>().Select(x => x.Content as string).SequenceEqual(new[] { "Hide", "Show when there are local changes", "Show always" }), "Appearance dropdown has all three choices in order");
        foreach (var selected in new[] { 0, 1, 2 })
        {
            dropdown.SelectedIndex = selected;
            Check(pref.WorkingDirectoryHeadMode == selected, "dropdown selection updates the preference");
        }
        pref.WorkingDirectoryHeadMode = 0;
        Check(dropdown.SelectedIndex == 0, "preference updates the dropdown selection");
        var box = dialog.GetVisualDescendants().OfType<CheckBox>().First(x => x.Content as string == App.Text("Preferences.Appearance.UseFixedTabWidth"));
        var dropdownPosition = dropdown.TranslatePoint(new Point(), dialog);
        var checkboxPosition = box.TranslatePoint(new Point(), dialog);
        Check(dropdownPosition.HasValue && checkboxPosition.HasValue && dropdownPosition.Value.Y + dropdown.Bounds.Height <= checkboxPosition.Value.Y, "head dropdown appears above Appearance checkboxes");
        if (render)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
            bitmap.Render(dialog);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "appearance-working-directory-head.png"));
        }
        // Hide rather than close: closing Preferences writes global Git configuration.
        dialog.Hide();
        pref.WorkingDirectoryHeadMode = mode;
    }

    private static void CheckClassification()
    {
        var entry = new WorkingDirectoryEntry();
        var cases = new (ChangeState Index, ChangeState WorkTree, int Modified, int Added, int Deleted)[]
        {
            (ChangeState.None, ChangeState.Modified, 1, 0, 0),
            (ChangeState.Modified, ChangeState.Modified, 1, 0, 0),
            (ChangeState.Added, ChangeState.Modified, 0, 1, 0),
            (ChangeState.Added, ChangeState.Deleted, 0, 1, 0),
            (ChangeState.None, ChangeState.Untracked, 0, 1, 0),
            (ChangeState.Deleted, ChangeState.None, 0, 0, 1),
            (ChangeState.Modified, ChangeState.Deleted, 0, 0, 1),
            (ChangeState.Renamed, ChangeState.Deleted, 1, 0, 0),
            (ChangeState.Added, ChangeState.TypeChanged, 1, 0, 0),
            (ChangeState.None, ChangeState.Conflicted, 1, 0, 0),
        };
        foreach (var c in cases)
        {
            entry.SetChanges([new Change { Path = "file", Index = c.Index, WorkTree = c.WorkTree }]);
            Check((entry.Modified, entry.Added, entry.Deleted) == (c.Modified, c.Added, c.Deleted), $"classification {c.Index}/{c.WorkTree}");
        }
        entry.SetChanges([new Change { Path = "file", Index = ChangeState.Modified, WorkTree = ChangeState.Modified },
            new Change { Path = "file", Index = ChangeState.Modified }]);
        Check(entry.Modified == 1, "deduplicate paths");
        Check(entry.Summary == "* Working Directory *", "fixed working-directory label");
        entry.SetChanges([]);
        Check(!entry.HasChanges, "clean entry has no changes");
    }

    private static void CheckGraph()
    {
        CommitGraph.SetDefaultPens();
        foreach (var headType in new[] { DecoratorType.CurrentBranchHead, DecoratorType.CurrentCommitHead })
        {
            var head = new Commit { SHA = new string('a', 40), Decorators = [new SourceGit.Models.Decorator { Type = headType }] };
            var tip = new Commit { SHA = new string('b', 40), Parents = [head.SHA] };
            var baseline = CommitGraph.Generate([tip, head], false, CommitGraphHighlighting.All, []);
            var margins = new[] { tip.LeftMargin, head.LeftMargin };
            var graph = CommitGraph.Generate([tip, head], false, CommitGraphHighlighting.All, [], true);
            Check(graph.Dots[0].Center.X == baseline.Dots[1].Center.X, "working node shares pinned HEAD lane");
            Check(graph.Dots.Skip(1).Select(x => x.Center.X).SequenceEqual(baseline.Dots.Select(x => x.Center.X)), "working row preserves every real node X coordinate");
            Check(tip.LeftMargin == margins[0] && head.LeftMargin == margins[1], "working row preserves commit text margins");
            Check(graph.Paths[0].Points.All(x => x.X == 10), "working edge stays straight in the leftmost lane");
            Check(graph.Dots[1].Center.X > graph.Dots[2].Center.X, "newer branch tip stays right of HEAD");
            Check(graph.Dots.Count == 3 && graph.Dots[0].Type == CommitGraph.DotType.WorkingDirectory, "distinct graph node");
            Check(graph.Dots[2].Type == CommitGraph.DotType.Head, "preserve real HEAD");
            Check(graph.Paths[0].Points[^1] == graph.Dots[2].Center, "connect HEAD below tip");
            Check(graph.Dots[1].Center.Y == 1.5 && graph.Dots[2].Center.Y == 2.5, "row alignment");
            var filtered = CommitGraph.Generate([tip], false, CommitGraphHighlighting.All, [], true);
            Check(filtered.Paths.All(x => x.Points[0] != filtered.Dots[0].Center), "filtered HEAD has no working edge");
        }
        var sideHead = new Commit { SHA = new string('c', 40), Decorators = [new SourceGit.Models.Decorator { Type = DecoratorType.CurrentBranchHead }] };
        var otherTip = new Commit { SHA = new string('d', 40), Parents = [new string('e', 40)] };
        var sideBaseline = CommitGraph.Generate([otherTip, sideHead], false, CommitGraphHighlighting.All, []);
        var sideGraph = CommitGraph.Generate([otherTip, sideHead], false, CommitGraphHighlighting.All, [], true);
        Check(sideGraph.Dots[0].Center.X == sideBaseline.Dots[1].Center.X && sideGraph.Dots[0].Center.X < sideGraph.Dots[1].Center.X, "HEAD is leftmost even below another branch tip");
        var newestHead = CommitGraph.Generate([sideHead], false, CommitGraphHighlighting.All, [], true);
        Check(newestHead.Paths[0].Points.All(x => x.X == newestHead.Dots[0].Center.X), "newest HEAD uses a vertical edge");
        var mergeHead = new Commit { SHA = "merge-head", Decorators = [new SourceGit.Models.Decorator { Type = DecoratorType.CurrentBranchHead }] };
        var mergeTip = new Commit { SHA = "merge-tip", Parents = ["other-parent", mergeHead.SHA] };
        var lowerMergeTip = new Commit { SHA = "lower-merge-tip", Parents = ["another-parent", mergeHead.SHA] };
        var connected = CommitGraph.Generate([mergeTip, lowerMergeTip, mergeHead], false, CommitGraphHighlighting.All, [], true);
        var junctions = connected.Links.Where(x => x.End.X == connected.Dots[0].Center.X).ToList();
        Check(junctions.Count == 2, "multiple branches connect above HEAD");
        var junction = junctions.MaxBy(x => x.End.Y).End;
        Check(connected.Paths[0].IsDotted && connected.Paths[0].Points[^1] == junction, "working connector is dotted above the last branch junction");
        Check(!connected.Paths[1].IsDotted && connected.Paths[1].Points[0] == junction && connected.Paths[1].Points[^1] == connected.Dots[^1].Center,
            "working connector stays continuous from the branch junction to HEAD");
        Check(connected.Paths.Skip(1).All(x => !x.IsDotted), "real tree connections stay continuous");
        var legacyHead = new Commit { SHA = "legacy-head", Decorators = [new SourceGit.Models.Decorator { Type = DecoratorType.CurrentBranchHead }] };
        var legacyTip = new Commit { SHA = "legacy-tip", Parents = [legacyHead.SHA] };
        var legacy = CommitGraph.Generate([legacyTip, legacyHead], false, CommitGraphHighlighting.All, [], false, false);
        Check(legacy.Dots.Count == 2 && legacy.Dots.All(x => x.Center.X == 10), "Hide restores fork-point lanes without reserving HEAD lane");
        Check(legacy.Dots[0].Center.Y == 0.5 && legacy.Dots[1].Center.Y == 1.5, "Hide restores original row positions");
        CheckPinnedAncestry();
        var empty = CommitGraph.Generate([], false, CommitGraphHighlighting.All, [], true);
        Check(empty.Dots.Count == 1 && empty.Paths.Count == 0, "unborn repository graph");
    }

    private static void CheckScrolledGraphRendering()
    {
        foreach (var headType in new[] { DecoratorType.CurrentBranchHead, DecoratorType.CurrentCommitHead })
        {
            var head = new Commit { SHA = "old-head", Decorators = [new SourceGit.Models.Decorator { Type = headType }] };
            List<Commit> commits = [];
            for (var i = 0; i < 40; i++)
                commits.Add(new Commit { SHA = $"commit-{i}", Parents = [i == 39 ? head.SHA : $"commit-{i + 1}"] });
            commits[30].Parents.Add(head.SHA);
            commits.Add(head);
            var graph = CommitGraph.Generate(commits, false, CommitGraphHighlighting.All, [], true);
            var continuation = graph.Paths[1];
            Check(!continuation.IsDotted && continuation.Points[0].Y > 30, "old HEAD continuation starts below the newer visible commits");

            foreach (var startY in new[] { 0.0, 130.0 })
            {
                var actual = Render(graph, startY);
                graph.Paths.Remove(continuation);
                var expected = Render(graph, startY);
                Check(actual.SequenceEqual(expected), $"offscreen HEAD continuation preserves visible tree lines at scroll offset {startY}: {headType}");
                var paths = graph.Paths.ToList();
                graph.Paths.Clear();
                Check(!expected.SequenceEqual(Render(graph, startY)), "visible newer commit paths are rendered");
                graph.Paths.AddRange(paths);
                graph.Paths.Insert(1, continuation);
            }
        }

        static byte[] Render(CommitGraph graph, double startY)
        {
            var view = new Views.CommitGraph { Graph = graph, Layout = new(startY, 120, 26), Width = 120, Height = 130 };
            view.Measure(new Size(120, 130));
            view.Arrange(new Rect(0, 0, 120, 130));
            using var bitmap = new RenderTargetBitmap(new PixelSize(120, 130));
            bitmap.Render(view);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
    }

    private static void CheckPinnedAncestry()
    {
        var root = new Commit { SHA = "root" };
        var parent = new Commit { SHA = "parent", Parents = [root.SHA] };
        var side = new Commit { SHA = "side", Parents = [root.SHA] };
        var merge = new Commit { SHA = "merge", Parents = [parent.SHA, side.SHA] };
        var head = new Commit { SHA = "head", Parents = [merge.SHA], Decorators = [new SourceGit.Models.Decorator { Type = DecoratorType.CurrentBranchHead }] };
        var other = new Commit { SHA = "other", Parents = [root.SHA] };
        var tip = new Commit { SHA = "tip", Parents = [other.SHA] };
        List<Commit> commits = [tip, head, other, merge, parent, side, root];
        foreach (var dirty in new[] { false, true })
        foreach (var firstParentOnly in new[] { false, true })
        foreach (var highlighting in Enum.GetValues<CommitGraphHighlighting>())
        {
            var graph = CommitGraph.Generate(commits, firstParentOnly, highlighting, [head.SHA], dirty);
            var offset = dirty ? 1 : 0;
            Check(new[] { 1, 3, 4, 6 }.All(i => graph.Dots[i + offset].Center.X == 10), "HEAD first-parent ancestry stays leftmost and straight");
            Check(new[] { 0, 2, 5 }.All(i => graph.Dots[i + offset].Center.X > 10), "other branches remain to the right of HEAD ancestry");
            if (!dirty)
                Check(graph.Paths.Where(x => x.Color == graph.Dots[1].Color).All(x => x.Points[0].Y >= 1.5), "clean graph has no invented edge above HEAD");
        }
    }

    private static void CheckSelection(Repository repo, Window window)
    {
        var vm = repo.Histories;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        Check(vm.Commits.Count == 1 && vm.Entries.Count == 2, "real commit count excludes working row");
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        Check(ReferenceEquals(vm.DetailContext, repo.WorkingCopy) && vm.SelectedCommits.Count == 0, "working selection uses shared model");
        PumpUntil(() => window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
        var editor = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single();
        list.Focus();
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(!vm.IsWorkingDirectorySelected, "keyboard Down selects a real commit");
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        Check(vm.SelectedCommits.Count == 0, "keyboard Up selects working directory exclusively");
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
        PumpUntil(() => !vm.IsWorkingDirectorySelected);
        Check(list.SelectedItems.Count == vm.Commits.Count && vm.SelectedCommits.Count == vm.Commits.Count, "Select All contains only real commits");
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        var graph = vm.Graph;
        repo.WorkingCopy.CommitMessage = "Live subject\nBody";
        Check(vm.WorkingDirectory.Summary == "* Working Directory *" && ReferenceEquals(vm.Graph, graph), "draft preserves fixed label and graph");
        repo.SelectedViewIndex = 1;
        PumpUntil(() => window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
        Check(ReferenceEquals(editor, window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single()), "same editor on sidebar");
        repo.SelectedViewIndex = 0;
        PumpUntil(() => window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
        Check(ReferenceEquals(editor, window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single()), "same editor in history");
        vm.IsCollapseDetails = true;
        PumpUntil(() => !window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
        Check(vm.IsWorkingDirectorySelected, "collapse detaches editor while preserving selection");
        vm.IsCollapseDetails = false;
        PumpUntil(() => window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any());
        Check(ReferenceEquals(editor, window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single()), "expand restores shared editor");
        list.SelectedItem = vm.Commits[0];
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(!vm.IsWorkingDirectorySelected, "real commit restores details");
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        vm.NavigateTo(vm.Commits[0].SHA);
        PumpUntil(() => list.SelectedItem is Commit);
        Check(!vm.IsWorkingDirectorySelected, "programmatic navigation restores real selection");
        list.SelectedItems.Add(vm.WorkingDirectory);
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        Check(list.SelectedItems.Count == 2 && vm.SelectedCommits.Count == 1 && vm.DetailContext is RevisionCompare { StartPoint: Commit, EndPoint: SourceGit.Models.Null }, "commit plus working directory compares against the worktree");
        vm.Commits = new List<Commit>(vm.Commits);
        PumpUntil(() => list.SelectedItems.Count == 2);
        Check(vm.IsWorkingDirectorySelected && vm.DetailContext is RevisionCompare, "history refresh preserves worktree comparison");
        list.SelectedItems.Remove(vm.Commits[0]);
        PumpUntil(() => vm.DetailContext is WorkingCopy);
        Check(vm.SelectedCommits.Count == 0 && list.SelectedItems.Count == 1, "removing comparison commit returns to the shared editor");
        list.SelectedItems.Add(vm.Commits[0]);
        PumpUntil(() => vm.DetailContext is RevisionCompare);
        list.SelectedItems.Remove(vm.WorkingDirectory);
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(!vm.IsWorkingDirectorySelected && vm.SelectedCommits.Count == 1, "removing working row returns to commit details");
        list.SelectedItems.Add(vm.WorkingDirectory);
        PumpUntil(() => vm.DetailContext is RevisionCompare);
        Preferences.Instance.WorkingDirectoryHeadMode = 0;
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(vm.SelectedCommits.Count == 1, "hiding the working row preserves the compared commit");
        Preferences.Instance.WorkingDirectoryHeadMode = 1;
        var fullHistory = vm.Commits;
        var filteredCommit = new Commit { SHA = fullHistory[0].SHA, Subject = fullHistory[0].Subject };
        vm.Commits = [filteredCommit];
        list.SelectedItem = vm.WorkingDirectory;
        list.SelectedItems.Add(filteredCommit);
        PumpUntil(() => vm.DetailContext is RevisionCompare);
        Preferences.Instance.WorkingDirectoryHeadMode = 0;
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(vm.SelectedCommits.Single() == filteredCommit, "hiding the working row preserves comparison target when HEAD is filtered out");
        vm.Commits = fullHistory;
        Preferences.Instance.WorkingDirectoryHeadMode = 1;
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.DetailContext is WorkingCopy);
        graph = vm.Graph;
        vm.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified },
            new Change { Path = "new.txt", WorkTree = ChangeState.Untracked }]);
        Check(vm.IsWorkingDirectorySelected && ReferenceEquals(graph, vm.Graph), "count update preserves selection and graph");
        vm.Commits = new List<Commit>(vm.Commits);
        PumpUntil(() => list.SelectedItems.Contains(vm.WorkingDirectory));
        Check(vm.IsWorkingDirectorySelected, "history refresh preserves working selection");
        vm.UpdateWorkingDirectory([]);
        PumpUntil(() => vm.DetailContext is CommitDetail);
        Check(vm.SelectedCommits.Count == 1 && vm.SelectedCommits[0].IsCurrentHead, "clean transition selects HEAD");
        vm.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
        var commits = vm.Commits;
        vm.Commits = [];
        Check(vm.Entries.Count == 1 && vm.Graph.Dots.Count == 1, "working entry survives empty or filtered history");
        vm.UpdateWorkingDirectory([]);
        PumpUntil(() => vm.DetailContext is SourceGit.Models.Null);
        Check(list.SelectedItems.Count == 0, "clean transition without displayed HEAD clears details");
        vm.Commits = commits;
        vm.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
    }

    private static void CheckWorkingDirectoryActions(Repository repo, Window window)
    {
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        var row = list.GetVisualDescendants().OfType<DataGridRow>().Single(x => ReferenceEquals(x.DataContext, repo.Histories.WorkingDirectory));
        var label = row.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == App.Text("Histories.WorkingDirectory"));
        ContextMenu opened = null;
        using var subscription = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => opened = menu);
        label.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        PumpUntil(() => opened?.IsOpen == true);
        var actions = opened.Items.OfType<MenuItem>().ToList();
        Check(actions.Select(x => x.Header as string).SequenceEqual(new[] { App.Text("Stash.Title"), App.Text("Repository.DiscardAll"), App.Text("Histories.WorkingDirectory.OpenPage") }), "working row has stash, discard and navigation actions");
        Check(actions.All(x => x.IsEnabled), "dirty working row enables its actions");
        opened.Close();
        actions[^1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Check(repo.SelectedViewIndex == 1, "working row menu opens Local Changes");
        repo.SelectedViewIndex = 0;
        PumpUntil(() => window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Any());
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var pressed = new PointerPressedEventArgs(label, pointer, window, new Point(), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        label.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, pressed));
        Check(repo.SelectedViewIndex == 1, "double-click working row opens Local Changes without checkout");
        repo.SelectedViewIndex = 0;
        Preferences.Instance.WorkingDirectoryHeadMode = 2;
        repo.Histories.UpdateWorkingDirectory([]);
        opened = null;
        label.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        PumpUntil(() => opened?.IsOpen == true);
        actions = opened.Items.OfType<MenuItem>().ToList();
        Check(!actions[0].IsEnabled && !actions[1].IsEnabled && actions[^1].IsEnabled, "clean working row disables stash and discard but allows navigation");
        opened.Close();
        repo.Histories.UpdateWorkingDirectory([new Change { Path = "tracked.txt", WorkTree = ChangeState.Modified }]);
        Preferences.Instance.WorkingDirectoryHeadMode = 1;
    }

    private static void CheckLiveTranslations(Window window)
    {
        var original = Preferences.Instance.Locale;
        var row = window.GetVisualDescendants().OfType<DataGridRow>().Single(x => x.DataContext is WorkingDirectoryEntry);
        var label = row.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == App.Text("Histories.WorkingDirectory"));
        foreach (var locale in Locale.Supported)
        {
            Preferences.Instance.Locale = locale.Key;
            PumpUntil(() => label.Text == App.Text("Histories.WorkingDirectory"));
            Check(label.Text.StartsWith("* ") && label.Text.EndsWith(" *"), $"working-directory label updates live: {locale.Key}");
            Check(!string.IsNullOrWhiteSpace(App.Text("Histories.WorkingDirectory.OpenPage")), $"working-directory action is translated: {locale.Key}");
        }
        Preferences.Instance.Locale = original;
        PumpUntil(() => label.Text == App.Text("Histories.WorkingDirectory"));
    }

    private static void CheckMultipleCommitSelection(Repository repo, Window window)
    {
        var vm = repo.Histories;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        list.SelectedItem = vm.WorkingDirectory;
        list.SelectedItems.Add(vm.Commits[0]);
        PumpUntil(() => vm.DetailContext is RevisionCompare);
        list.SelectedItems.Add(vm.Commits[1]);
        PumpUntil(() => !vm.IsWorkingDirectorySelected);
        Check(vm.SelectedCommits.Count == 2 && list.SelectedItems.Count == 2 && vm.DetailContext is RevisionCompare { EndPoint: Commit }, "selecting more than one commit removes the working row from comparison");
        vm.Commits = new List<Commit>(vm.Commits);
        PumpUntil(() => list.SelectedItems.Count == 2);
        Check(vm.SelectedCommits.Count == 2 && !vm.IsWorkingDirectorySelected, "history refresh preserves a normal two-commit comparison");
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.DetailContext is WorkingCopy);
    }

    private static void CheckRowHitTesting(Repository repo, Window window)
    {
        var vm = repo.Histories;
        var list = window.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
        var theme = Preferences.Instance.Theme;
        foreach (var variant in new[] { "Light", "Dark" })
        {
            Preferences.Instance.Theme = variant;
            list.SelectedItem = vm.Commits[0];
            PumpUntil(() => !vm.IsWorkingDirectorySelected);
            var row = list.GetVisualDescendants().OfType<DataGridRow>().Single(x => ReferenceEquals(x.DataContext, vm.WorkingDirectory));
            foreach (var cell in row.GetVisualDescendants().OfType<DataGridCell>().Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 0))
            foreach (var x in new[] { 2.0, cell.Bounds.Width / 2, cell.Bounds.Width - 2 })
            {
                var point = cell.TranslatePoint(new Point(x, cell.Bounds.Height / 2), window).Value;
                var hit = window.InputHitTest(point) as Control;
                Check(hit != null && hit.FindAncestorOfType<DataGridCell>() == cell, $"working row hit target spans every cell: {variant}");
                var pointer = new Pointer(1, PointerType.Mouse, true);
                hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                Check(vm.IsWorkingDirectorySelected && ReferenceEquals(vm.DetailContext, repo.WorkingCopy), $"clicking working row opens Local Changes: {variant}");
                hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                list.SelectedItem = vm.Commits[0];
                PumpUntil(() => !vm.IsWorkingDirectorySelected);
            }
        }
        Preferences.Instance.Theme = theme;
        list.SelectedItem = vm.WorkingDirectory;
        PumpUntil(() => vm.IsWorkingDirectorySelected);
    }

    private static void CheckStatusPipeline(Repository repo, string fixture)
    {
        File.WriteAllText(Path.Combine(fixture, "new.txt"), "added\n");
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.LocalChangesCount == 2);
        Check(repo.Histories.WorkingDirectory.Added == 1 && repo.Histories.WorkingDirectory.Modified == 1, "status snapshot counts untracked once");
        repo.IncludeUntracked = false;
        PumpUntil(() => repo.LocalChangesCount == 1);
        Check(repo.Histories.WorkingDirectory.Added == 0, "untracked preference shared with badge");
        repo.IncludeUntracked = true;
        PumpUntil(() => repo.LocalChangesCount == 2);
        Git(fixture, "add new.txt");
        File.AppendAllText(Path.Combine(fixture, "new.txt"), "edited after staging\n");
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.WorkingCopy.Staged.Any(x => x.Path == "new.txt") && repo.WorkingCopy.Unstaged.Any(x => x.Path == "new.txt"));
        Check(repo.Histories.WorkingDirectory.Added == 1 && repo.Histories.WorkingDirectory.Modified == 1, "AM counts as one addition");
        Git(fixture, "mv tracked.txt renamed.txt");
        File.Delete(Path.Combine(fixture, "renamed.txt"));
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.WorkingCopy.Staged.Any(x => x.Index == ChangeState.Renamed));
        Check(repo.Histories.WorkingDirectory.Modified == 1 && repo.Histories.WorkingDirectory.Deleted == 0, "rename plus deletion counts as modified");
        // Restore the isolated fixture to check a real watcher-driven dirty-to-clean transition.
        Git(fixture, "reset --hard HEAD");
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.LocalChangesCount == 0);
        Check(!repo.Histories.WorkingDirectory.HasChanges && !repo.Histories.IsWorkingDirectorySelected, "refresh pipeline removes clean row");
        Check(repo.WorkingCopy.CommitMessage == "Live subject\nBody", "status refresh preserves draft");
        File.AppendAllText(Path.Combine(fixture, "tracked.txt"), "dirty again\n");
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.LocalChangesCount == 1);
        Check(repo.Histories.Entries.Count == repo.Histories.Commits.Count + 1, "clean-to-dirty restores row");
        repo.Histories.IsWorkingDirectorySelected = true;
    }

    private static void CheckCommitRefreshRace(Repository repo, string fixture)
    {
        using (repo.LockWatcher())
        {
            Git(fixture, "add tracked.txt");
            Git(fixture, "commit -m second");
            // Deliberately deliver the clean status snapshot before the new commit query.
            repo.Histories.UpdateWorkingDirectory([]);
            repo.RefreshCommits();
            PumpUntil(() => repo.Histories.Commits.Count == 2);
            Check(repo.Histories.SelectedCommits is { Count: 1 } selected && selected[0].IsCurrentHead && selected[0].Subject == "second", "commit refresh selects new HEAD after earlier clean status");
            repo.RefreshWorkingCopyChanges();
            PumpUntil(() => repo.LocalChangesCount == 0);
        }
        File.AppendAllText(Path.Combine(fixture, "tracked.txt"), "preview edit\n");
        repo.RefreshWorkingCopyChanges();
        PumpUntil(() => repo.LocalChangesCount == 1);
        repo.Histories.IsWorkingDirectorySelected = true;
    }

    private static void CheckLayouts(Repository repo, Window window, bool render)
    {
        foreach (var horizontal in new[] { false, true })
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var width in new[] { 1100, 800 })
        {
            Preferences.Instance.UseTwoColumnsLayoutInHistories = horizontal;
            Preferences.Instance.Theme = theme;
            window.Width = width;
            // Give native layout/render jobs an opportunity to run.
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            window.Measure(new Size(width, 800));
            window.Arrange(new Rect(0, 0, width, 800));
            var editor = window.GetVisualDescendants().OfType<Views.CommitMessageToolBox>().Single();
            Check(editor.Bounds.Width >= 180 && editor.Bounds.Height > 50, $"editor usable: {theme}, horizontal={horizontal}, width={width}");
            Check(window.GetVisualDescendants().OfType<Views.WorkingCopy>().Count() == 1, "only one attached working copy");
            if (render)
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, 800));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"preview-{theme}-{horizontal}-{width}.png"));
            }
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
            if (timer.Elapsed.TotalSeconds > 15)
                throw new Exception("Timed out waiting for UI state");
        } while (!condition());
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        _checks++;
    }

    private static string Git(string path, string args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = path,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Exception(process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd();
    }
}
