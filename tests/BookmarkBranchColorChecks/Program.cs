using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Threading;
using SourceGit;
using SourceGit.Models;
using SourceGit.ViewModels;
using Commit = SourceGit.Models.Commit;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        CommitGraph.SetDefaultPens();
        CheckColorsAndHighlighting();
        CheckOlderHead();
        CheckFallbacks();
        CheckCustomPalettes();
        CheckStackedBookmarkColors();
        CheckLiveBookmarks();
        Console.WriteLine($"PASS: {_checks} bookmark branch color checks");
    }

    private static void CheckColorsAndHighlighting()
    {
        for (var bookmark = 1; bookmark < Bookmarks.Brushes.Length; bookmark++)
        foreach (var highlighting in Enum.GetValues<CommitGraphHighlighting>())
        foreach (var firstParentOnly in new[] { false, true })
        {
            var root = new Commit { SHA = "root", IsMerged = true };
            var mainParent = Commit("main-parent", root);
            mainParent.IsMerged = true;
            var featureParent = Commit("feature-parent", root);
            var feature = Commit("feature", featureParent, "refs/heads/feature");
            var head = Commit("head", mainParent, "HEAD -> refs/heads/main");
            head.Parents.Add(feature.SHA);
            List<Commit> commits = [head, feature, mainParent, featureParent, root];
            var baseline = CommitGraph.Generate(commits, firstParentOnly, highlighting, [feature.SHA]);
            var margins = commits.Select(x => x.LeftMargin).ToArray();
            var graph = CommitGraph.Generate(commits, firstParentOnly, highlighting, [feature.SHA], bookmark);
            Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(bookmark)).Color,
                "the current branch uses the exact repository bookmark color");
            Check(mainParent.Color == head.Color && root.Color == head.Color && feature.Color != head.Color,
                "the first-parent branch keeps its bookmark color while the merged branch stays distinct");
            Check(baseline.Dots.Select(x => x.IsHighlighted).SequenceEqual(graph.Dots.Select(x => x.IsHighlighted)) &&
                baseline.Paths.Select(x => x.IsHighlighted).SequenceEqual(graph.Paths.Select(x => x.IsHighlighted)),
                "bookmark colors preserve highlighted and grayed-out sections in every mode");
            Check(baseline.Dots.Select(x => x.Center).SequenceEqual(graph.Dots.Select(x => x.Center)) &&
                commits.Select(x => x.LeftMargin).SequenceEqual(margins),
                "bookmark colors preserve lane positions and graph width");
        }
    }

    private static void CheckOlderHead()
    {
        for (var bookmark = 1; bookmark < Bookmarks.Brushes.Length; bookmark++)
        {
            var root = new Commit { SHA = "root" };
            var head = Commit("head", root, "HEAD -> refs/heads/main");
            var tip = Commit("tip", head, "refs/heads/feature");
            var graph = CommitGraph.Generate([tip, head, root], false, CommitGraphHighlighting.All, [], bookmark);
            Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(bookmark)).Color && tip.Color != head.Color,
                "an older HEAD switches to the bookmark color without recoloring the branch above it");
            Check(graph.Paths.Count == 2 && graph.Paths[0].Points[^1] == graph.Dots[1].Center &&
                graph.Paths[1].Points[0] == graph.Dots[1].Center && graph.Dots.All(x => x.Center.X == 10),
                "the color boundary meets the older HEAD dot without moving the lane");

            root.Decorators = [new Decorator { Type = DecoratorType.CurrentBranchHead, Name = "main" }];
            head.Decorators.Clear();
            graph = CommitGraph.Generate([tip, head, root], false, CommitGraphHighlighting.All, [], bookmark);
            Check(BrushColor(root.Color) == ((ISolidColorBrush)Bookmarks.Get(bookmark)).Color && graph.Paths.Count == 1,
                "a root HEAD gets the bookmark color without adding an empty path");

            var detached = new Commit { SHA = "detached" };
            detached.ParseDecorators("HEAD");
            graph = CommitGraph.Generate([detached], false, CommitGraphHighlighting.All, [], bookmark);
            Check(graph.Dots.Count == 1 && graph.Paths.Count == 0 && BrushColor(detached.Color) == ((ISolidColorBrush)Bookmarks.Get(bookmark)).Color,
                "a detached or single-commit HEAD uses the bookmark color");
        }
    }

    private static void CheckFallbacks()
    {
        var root = new Commit { SHA = "root" };
        var head = Commit("head", root, "HEAD -> refs/heads/main");
        var baseline = CommitGraph.Generate([head, root], false, CommitGraphHighlighting.All, []);
        foreach (var bookmark in new[] { 0, -1, Bookmarks.Brushes.Length, int.MaxValue })
        {
            var graph = CommitGraph.Generate([head, root], false, CommitGraphHighlighting.All, [], bookmark);
            Check(graph.Dots.Select(x => x.Color).SequenceEqual(baseline.Dots.Select(x => x.Color)),
                "an unset or invalid bookmark retains the original graph colors");
        }
        head.Decorators.Clear();
        var noHead = CommitGraph.Generate([head, root], false, CommitGraphHighlighting.All, [], 2);
        Check(noHead.Dots.Select(x => x.Color).SequenceEqual(baseline.Dots.Select(x => x.Color)),
            "a history without HEAD retains the original graph colors");
        Check(CommitGraph.Generate([], false, CommitGraphHighlighting.All, [], 2).Dots.Count == 0,
            "an empty bookmarked history stays empty");

        head.ParseDecorators("HEAD -> refs/heads/main");
        var first = CommitGraph.Generate([head, root], false, CommitGraphHighlighting.All, [], 7);
        var firstColor = BrushColor(first.Dots[0].Color);
        CommitGraph.Generate([head, root], false, CommitGraphHighlighting.All, [], 1);
        Check(BrushColor(first.Dots[0].Color) == firstColor,
            "generating another repository's graph does not recolor an existing graph");
    }

    private static void CheckCustomPalettes()
    {
        try
        {
            foreach (var palette in new List<Color>[] { [Colors.AliceBlue], [Colors.Red], [Colors.Gold, Colors.Purple] })
            {
                CommitGraph.SetPens(palette, 3.5);
                var root = new Commit { SHA = "root" };
                var head = Commit("head", root, "HEAD -> refs/heads/main");
                var tip = Commit("tip", head, "refs/heads/feature");
                for (var bookmark = 1; bookmark < Bookmarks.Brushes.Length; bookmark++)
                {
                    var graph = CommitGraph.Generate([tip, head, root], false, CommitGraphHighlighting.All, [], bookmark);
                    Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(bookmark)).Color &&
                        graph.Dots.All(x => x.Color >= 0 && x.Color < CommitGraph.Pens.Count),
                        "custom and single-color palettes support every bookmark without invalid colors");
                    Check(CommitGraph.Pens[head.Color].Thickness == 3.5, "bookmark lines retain the configured graph thickness");
                }
            }
        }
        finally
        {
            CommitGraph.SetDefaultPens();
        }
    }

    private static void CheckStackedBookmarkColors()
    {
        try
        {
            foreach (var colors in new List<Color>[] { [Colors.Red, Colors.Orange], [Colors.Red, Colors.Orange, Colors.Gold] })
            {
                CommitGraph.SetPens(colors, 2);
                var root = new Commit { SHA = "root" };
                var head = Commit("head", root, "HEAD -> refs/heads/main");
                var branchBase = Commit("base", root, "refs/heads/base");
                var feature = Commit("feature", branchBase, "refs/heads/feature");
                var graph = CommitGraph.Generate([feature, branchBase, head, root], false, CommitGraphHighlighting.All, [], 1);
                Check(BrushColor(head.Color) == Colors.Red && feature.Color != head.Color && branchBase.Color != head.Color,
                    "stacked side branches preserve the color reserved for the current branch");
                Check(colors.Count == 2 || feature.Color != branchBase.Color,
                    "stacked side branches remain distinct when the palette has another available color");
                Check(graph.Dots.All(x => x.Color >= 0 && x.Color < CommitGraph.Pens.Count),
                    "small palettes terminate with valid colors when a bookmark color is reserved");
            }
        }
        finally
        {
            CommitGraph.SetDefaultPens();
        }
    }

    private static void CheckLiveBookmarks()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "sourcegit-bookmark-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = fixture;
        SourceGit.Native.OS.BasicDirectories.CacheDir = fixture;
        try
        {
            App.BuildAvaloniaApp().SetupWithoutStarting();
            var launcher = new Launcher(null);
            typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Application.Current, launcher);
            var node = new RepositoryNode { Id = fixture, Name = "Fixture", IsRepository = true, Bookmark = 6 };
            Preferences.Instance.RepositoryNodes.Add(node);
            var repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
            Check(repo.Bookmark == node.Bookmark, "repositories read the saved bookmark before their tab is attached");
            var histories = new Histories(repo);
            typeof(Repository).GetField("_histories", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repo, histories);
            launcher.Pages.Add(new LauncherPage(node, repo));
            var root = new Commit { SHA = "root" };
            var head = Commit("head", root, "HEAD -> refs/heads/main");
            histories.Commits = [head, root];
            var label = new SourceGit.Views.CommitRefsPresenter { DataContext = head, UseGraphColor = true, FontSize = 12 };
            label.Bind(SourceGit.Views.CommitRefsPresenter.GraphColorProperty, new Binding(nameof(SourceGit.Models.Commit.Color)));
            label.Measure(new Size(200, 26));
            Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(6)).Color,
                "the history view uses its repository tab's bookmark color");
            var original = histories.Graph;
            node.Bookmark = 7;
            Check(!ReferenceEquals(original, histories.Graph) && BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(6)).Color,
                "editing a repository bookmark regenerates the graph and preserves a cached branch color");
            Check(label.GraphColor == head.Color, "the visible badge uses the cached branch color");
            var items = (List<SourceGit.Views.CommitRefsPresenter.RenderItem>)typeof(SourceGit.Views.CommitRefsPresenter)
                .GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(label);
            Check(((ISolidColorBrush)items[0].Brush).Color == ((ISolidColorBrush)Bookmarks.Get(6)).Color,
                "a bookmark edit leaves the visible branch badge color intact");
            node.Bookmark = 0;
            Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(6)).Color,
                "clearing the bookmark preserves the cached color");
            node.IsUnmanaged = true;
            Preferences.Instance.RepositoryNodes.Remove(node);
            node.Bookmark = 3;
            var detached = Commit("detached", root, "HEAD");
            histories.Commits = [detached, head, root];
            Check(repo.Bookmark == 3 && BrushColor(detached.Color) == ((ISolidColorBrush)Bookmarks.Get(3)).Color,
                "unmanaged tabs use their own bookmark for an unassigned HEAD");

            var reopened = new Histories(new Repository(false, fixture, Path.Combine(fixture, ".git")));
            reopened.Commits = [head, root];
            Check(BrushColor(head.Color) == ((ISolidColorBrush)Bookmarks.Get(6)).Color,
                "a new repository view reuses that repository's runtime branch assignments");

            var independent = new Histories(new Repository(false, Path.Combine(fixture, "other"), Path.Combine(fixture, "other", ".git")));
            var independentHead = Commit("independent", root, "HEAD -> refs/heads/main");
            independent.Commits = [independentHead, root];
            Check(independentHead.Color == 0, "the same branch name in a different repository receives its own color");

            CheckRefChanges(fixture, launcher);
        }
        finally
        {
            var root = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(fixture).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture must stay inside the temporary directory");
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(fixture, true);
        }
    }

    private static void CheckRefChanges(string fixture, Launcher launcher)
    {
        var path = Path.Combine(fixture, "refs-fixture");
        Directory.CreateDirectory(path);
        var git = SourceGit.Native.OS.FindGitExecutable();
        Check(!string.IsNullOrEmpty(git), "Git is available for the ref lifecycle checks");
        SourceGit.Native.OS.GitExecutable = git;
        RunGit(path, "init", "-b", "main");
        RunGit(path, "commit", "--allow-empty", "-m", "main");
        RunGit(path, "switch", "-c", "feature");
        RunGit(path, "commit", "--allow-empty", "-m", "feature");

        var repo = new Repository(false, path, Path.Combine(path, ".git"));
        var histories = new Histories(repo);
        typeof(Repository).GetField("_histories", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repo, histories);
        var node = new RepositoryNode { Id = repo.FullPath, Name = "Ref Lifecycle", IsRepository = true, Bookmark = 6 };
        launcher.Pages.Add(new LauncherPage(node, repo));
        RefreshBranches(repo, () => repo.Branches.Any(x => x.Name == "feature"));

        var root = new Commit { SHA = RunGit(path, "rev-parse", "main") };
        var initial = Commit(RunGit(path, "rev-parse", "HEAD"), root, "HEAD -> refs/heads/feature");
        histories.Commits = [initial, root];
        var initialColor = BrushColor(initial.Color);
        Check(initialColor == ((ISolidColorBrush)Bookmarks.Get(6)).Color, "a newly visible branch can inherit the bookmark");
        node.Bookmark = 0;

        var worktree = Path.Combine(fixture, "linked-worktree");
        RunGit(path, "worktree", "add", "--detach", worktree, "HEAD");
        var worktreeGitDir = Path.Combine(path, ".git", "worktrees", Path.GetFileName(worktree));
        var linked = new Histories(new Repository(false, worktree, worktreeGitDir));
        var linkedTip = Commit(initial.SHA, root, "refs/heads/feature");
        linked.Commits = [linkedTip, root];
        Check(BrushColor(linkedTip.Color) == initialColor, "linked worktrees share the common repository's runtime branch colors");

        RunGit(path, "branch", "-m", "feature", "intermediate");
        RunGit(path, "branch", "-m", "intermediate", "renamed");
        RunGit(path, "commit", "--allow-empty", "-m", "advance renamed branch");
        RefreshBranches(repo, () => repo.Branches.Any(x => x.Name == "renamed"));
        var renamed = Commit(RunGit(path, "rev-parse", "HEAD"), root, "HEAD -> refs/heads/renamed");
        histories.Commits = [renamed, root];
        Check(BrushColor(renamed.Color) == initialColor, "chained external renames preserve the cached color even after a commit");

        RunGit(path, "switch", "main");
        RunGit(path, "branch", "-D", "renamed");
        RefreshBranches(repo, () => repo.Branches.All(x => x.Name != "renamed"));
        histories.RefreshGraph();
        RunGit(path, "switch", "-c", "renamed");
        RunGit(path, "commit", "--allow-empty", "-m", "new branch with a reused name");
        RefreshBranches(repo, () => repo.Branches.Any(x => x.Name == "renamed"));
        node.Bookmark = 7;
        var recreated = Commit(RunGit(path, "rev-parse", "HEAD"), root, "HEAD -> refs/heads/renamed");
        histories.Commits = [recreated, root];
        Check(BrushColor(recreated.Color) == ((ISolidColorBrush)Bookmarks.Get(7)).Color,
            "a recreated ref gets a fresh color after deletion and stale history rendering");
    }

    private static void RefreshBranches(Repository repo, Func<bool> done)
    {
        repo.RefreshBranches();
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Check(done(), "the repository rereads its refs");
    }

    private static string RunGit(string path, params string[] args)
    {
        var start = new ProcessStartInfo(SourceGit.Native.OS.GitExecutable)
        {
            WorkingDirectory = path,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false" })
            start.ArgumentList.Add(arg);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error);
        return output.TrimEnd();
    }

    private static Color BrushColor(int color) => ((ISolidColorBrush)CommitGraph.Pens[color].Brush).Color;

    private static Commit Commit(string sha, Commit parent, string decorators = "")
    {
        var commit = new Commit { SHA = sha, Parents = [parent.SHA] };
        commit.ParseDecorators(decorators);
        return commit;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception("FAIL: " + message);
        _checks++;
    }
}
