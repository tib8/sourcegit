using SourceGit.Models;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        CommitGraph.SetDefaultPens();
        CheckStackedBranches();
        CheckSharedBase();
        CheckMergeLane();
        CheckHighlighting();
        CheckSmallPalettes();
        CheckMasterGraphFeatures();
        CheckDetachedSnapshot();
        Console.WriteLine($"PASS: {_checks} commit graph checks");
    }

    private static void CheckDetachedSnapshot()
    {
        var root = new Commit { SHA = "snapshot-root" };
        var head = Commit("snapshot-head", root, "HEAD -> refs/heads/main");
        var tip = Commit("snapshot-tip", head, "refs/heads/topic");
        List<Commit> all = [tip, head, root];
        foreach (var filtered in new[] { false, true })
        {
            var visible = filtered ? new List<Commit> { tip, root } : all;
            var expected = filtered ? CommitGraph.GenerateFiltered(all, visible, false, CommitGraphHighlighting.All, [], true) :
                CommitGraph.Generate(visible, false, CommitGraphHighlighting.All, [], true);
            var colors = visible.Select(c => c.Color).ToArray();
            var margins = visible.Select(c => c.LeftMargin).ToArray();
            var snapshot = new CommitGraph.Snapshot(all, visible, filtered, false, CommitGraphHighlighting.All, [], true, true, 0);
            foreach (var c in visible) { c.Color = 99; c.LeftMargin = 999; }
            // A concurrent theme change must not affect the captured graph calculation.
            CommitGraph.SetPens([Avalonia.Media.Colors.Red], 2);
            var actual = Task.Run(() => snapshot.Generate(CancellationToken.None)).GetAwaiter().GetResult();
            Check(visible.All(c => c.Color == 99 && c.LeftMargin == 999), "background graph never mutates bound commits");
            Check(actual.Dots.Select(d => (d.Center, d.Color)).SequenceEqual(expected.Dots.Select(d => (d.Center, d.Color))), "snapshot preserves graph layout and captured colors");
            snapshot.Apply();
            Check(visible.Select(c => c.Color).SequenceEqual(colors) && visible.Select(c => c.LeftMargin).SequenceEqual(margins), "snapshot publishes the matching commit presentation");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { snapshot.Generate(cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "superseded graph computations can be cancelled");
            CommitGraph.SetDefaultPens();
        }
    }

    private static void CheckStackedBranches()
    {
        var root = new Commit { SHA = "root" };
        var main = Commit("main", root, "refs/heads/main, refs/remotes/origin/main");
        var first = Commit("first", main, "tag: refs/tags/v1");
        var feature = Commit("feature", first, "refs/heads/feature");
        var second = Commit("second", feature);
        var tip = Commit("tip", second, "HEAD -> refs/heads/topic, refs/remotes/origin/topic, refs/heads/alias");
        List<Commit> commits = [tip, second, feature, first, main, root];
        var graph = CommitGraph.Generate(commits, false, CommitGraphHighlighting.All, []);
        Check(tip.Color == second.Color && feature.Color == first.Color && main.Color == root.Color,
            "commits and tags on each branch segment keep the same color");
        Check(tip.Color != feature.Color && feature.Color != main.Color,
            "each stacked branch changes color at its base branch tip");
        Check(graph.Dots.All(x => x.Center.X == 10) && commits.All(x => x.LeftMargin == 18),
            "stacked branches keep one lane and the original graph width");
        Check(graph.Paths.Count == 3 && graph.Dots.Count == commits.Count,
            "multiple branch names at one tip do not add paths or dots");
        for (var i = 0; i < 4; i += 2)
        {
            var boundary = graph.Dots[i + 2].Center;
            Check(graph.Paths.Any(x => x.Color == commits[i].Color && x.Points[^1] == boundary) &&
                graph.Paths.Any(x => x.Color == commits[i + 2].Color && x.Points[0] == boundary),
                "color segments meet exactly at the named branch dot");
        }

        root.ParseDecorators("refs/heads/root");
        graph = CommitGraph.Generate(commits, false, CommitGraphHighlighting.All, []);
        Check(root.Color != main.Color && graph.Paths.Count == 3 && graph.Paths[^1].Points[^1] == graph.Dots[^1].Center,
            "a branch at the root gets a distinct dot color without an extra edge");

        var aliasRoot = new Commit { SHA = "alias-root" };
        var aliases = Commit("aliases", aliasRoot, "HEAD -> refs/heads/main, refs/heads/other, refs/remotes/origin/main");
        graph = CommitGraph.Generate([aliases, aliasRoot], false, CommitGraphHighlighting.All, []);
        Check(graph.Paths.Count == 1 && graph.Dots.Count == 2 && aliases.Color == aliasRoot.Color,
            "branches pointing at the same commit share the existing dot and color");
    }

    private static void CheckSharedBase()
    {
        var root = new Commit { SHA = "root" };
        var shared = Commit("shared", root, "refs/heads/main");
        var left = Commit("left", shared, "refs/heads/left");
        var right = Commit("right", shared, "refs/heads/right");
        var third = Commit("third", shared, "refs/heads/third");
        var graph = CommitGraph.Generate([left, right, third, shared, root], false, CommitGraphHighlighting.All, []);
        Check(new[] { left.Color, right.Color, third.Color, shared.Color }.Distinct().Count() == 4,
            "several branches from one named base retain distinct colors");
        Check(graph.Dots.Take(3).Select(x => x.Center.X).Distinct().Count() == 3 && graph.Dots.Count == 5,
            "diverging branches keep separate lanes and one shared base commit");
        Check(shared.Color == root.Color, "the shared ancestry uses the base branch color");
    }

    private static void CheckMergeLane()
    {
        foreach (var firstParentOnly in new[] { false, true })
        {
            var root = new Commit { SHA = "root" };
            var mainParent = Commit("main-parent", root);
            var featureParent = Commit("feature-parent", root);
            var feature = Commit("feature", featureParent, "refs/heads/feature");
            var merge = Commit("merge", mainParent, "HEAD -> refs/heads/main");
            merge.Parents.Add(feature.SHA);
            var graph = CommitGraph.Generate([merge, feature, mainParent, featureParent, root], firstParentOnly, CommitGraphHighlighting.All, []);
            Check(merge.Color == mainParent.Color && feature.Color == featureParent.Color && merge.Color != feature.Color,
                "a merged branch keeps the color already assigned to its separate lane");
            Check(graph.Paths.Count == 2, "a branch on an existing merge lane does not add a redundant color split");
        }

        var detachedRoot = new Commit { SHA = "detached-root" };
        var detached = Commit("detached", detachedRoot, "HEAD, tag: refs/tags/v1");
        var detachedGraph = CommitGraph.Generate([detached, detachedRoot], false, CommitGraphHighlighting.All, []);
        Check(detachedGraph.Paths.Count == 1 && detached.Color == detachedRoot.Color,
            "detached HEAD and tags preserve the existing lane color");
    }

    private static void CheckHighlighting()
    {
        foreach (var highlighting in Enum.GetValues<CommitGraphHighlighting>())
        foreach (var firstParentOnly in new[] { false, true })
        foreach (var selectBase in new[] { false, true })
        {
            var root = new Commit { SHA = "root", IsMerged = true };
            var branchBase = Commit("base", root);
            var feature = Commit("feature", branchBase, "refs/heads/feature");
            var main = Commit("main", root, "HEAD -> refs/heads/main");
            List<Commit> commits = [feature, branchBase, main, root];
            var selected = selectBase ? branchBase.SHA : main.SHA;
            var original = CommitGraph.Generate(commits, firstParentOnly, highlighting, [selected]);
            var highlights = commits.Select(x => x.IsHighlightedInGraph).ToArray();
            branchBase.ParseDecorators("refs/heads/base");
            var colored = CommitGraph.Generate(commits, firstParentOnly, highlighting, [selected]);
            Check(original.Dots.Select(x => x.IsHighlighted).SequenceEqual(colored.Dots.Select(x => x.IsHighlighted)) &&
                commits.Select(x => x.IsHighlightedInGraph).SequenceEqual(highlights),
                "branch colors preserve highlighted and grayed-out commits in every mode");
            Check(colored.Paths.Any(x => x.Color == branchBase.Color && x.Points[0] == colored.Dots[1].Center &&
                x.IsHighlighted == branchBase.IsHighlightedInGraph),
                "the new branch segment preserves its highlighted or grayed-out state");
            Check(original.Dots.Select(x => x.Center).SequenceEqual(colored.Dots.Select(x => x.Center)),
                "recoloring preserves the layout in every highlighting mode");
            if (highlighting == CommitGraphHighlighting.CurrentBranchOnly)
                Check(!colored.Dots[0].IsHighlighted && !colored.Dots[1].IsHighlighted && colored.Dots[2].IsHighlighted,
                    "inactive stacked branches stay gray while the current branch stays highlighted");
        }
    }

    private static void CheckSmallPalettes()
    {
        var colors = CommitGraph.Pens.Take(2).Select(x => ((Avalonia.Media.ISolidColorBrush)x.Brush).Color).ToList();
        try
        {
            foreach (var colorCount in new[] { 1, 2 })
            {
                CommitGraph.SetPens(colors.Take(colorCount).ToList(), 2);
                var stack = Enumerable.Range(0, 12).Select(i => new Commit { SHA = $"stack-{i}", Parents = [$"stack-{i + 1}"] }).ToList();
                for (var i = 0; i < stack.Count; i++)
                    stack[i].ParseDecorators($"refs/heads/branch-{i}");
                var graph = CommitGraph.Generate(stack, false, CommitGraphHighlighting.All, []);
                Check(stack.All(x => x.Color >= 0 && x.Color < colorCount), "small palettes always produce valid colors");
                Check(colorCount == 1 || stack.Zip(stack.Skip(1)).All(x => x.First.Color != x.Second.Color),
                    "adjacent stacked branches stay distinct when the palette is recycled");
                Check(graph.Paths.Count == (colorCount == 1 ? 1 : stack.Count),
                    "a single-color palette does not create redundant path segments");
            }
        }
        finally
        {
            CommitGraph.SetDefaultPens();
        }
    }

    private static void CheckMasterGraphFeatures()
    {
        foreach (var reserveHeadLane in new[] { false, true })
        foreach (var highlighting in Enum.GetValues<CommitGraphHighlighting>())
        {
            var root = new Commit { SHA = "root" };
            var main = Commit("main", root, "refs/heads/main");
            var hidden = Commit("hidden", main);
            var tip = Commit("tip", hidden, "HEAD -> refs/heads/feature");
            List<Commit> all = [tip, hidden, main, root];
            var full = CommitGraph.Generate(all, false, highlighting, [main.SHA], true, reserveHeadLane);
            var highlights = new[] { tip, main, root }.Select(x => x.IsHighlightedInGraph).ToArray();
            Check(hidden.GraphBranches.Single().Name == "feature" && root.GraphBranches.Single().Name == "main",
                "branch hover context follows the named branch color boundaries");
            var filtered = CommitGraph.GenerateFiltered(all, [tip, main, root], false, highlighting, [main.SHA], true, reserveHeadLane);
            Check(tip.Color != main.Color && main.Color == root.Color && filtered.Dots.All(x => x.Center.X == 10),
                "filtered stacked branches change color without adding lanes");
            Check(new[] { tip, main, root }.Select(x => x.IsHighlightedInGraph).SequenceEqual(highlights),
                "filtering preserves the existing highlighting through the color boundary");
            Check(full.Dots[0].Color == tip.Color && filtered.Dots[0].Color == tip.Color &&
                filtered.Paths.Single(x => x.IsDotted).Color == tip.Color,
                "the working-directory row follows the HEAD branch color in filtered history");
        }

        foreach (var reserveHeadLane in new[] { false, true })
        {
            var root = new Commit { SHA = "root" };
            var head = Commit("head", root, "HEAD -> refs/heads/main");
            var other = Commit("other", head, "refs/heads/feature");
            var graph = CommitGraph.Generate([other, head, root], false, CommitGraphHighlighting.All, [], true, reserveHeadLane);
            Check(other.Color != head.Color && head.Color == root.Color && graph.Dots[0].Color == head.Color,
                "an older HEAD retains its branch color and working-directory connection");
            Check(graph.Dots[2].Center.X == 10 && graph.Dots[1].Center.X == (reserveHeadLane ? 22 : 10),
                "branch coloring preserves both reserved and compact HEAD lane layouts");
        }
    }

    private static Commit Commit(string sha, Commit parent, string decorators = "")
    {
        var commit = new Commit { SHA = sha, Parents = [parent.SHA] };
        commit.ParseDecorators(decorators);
        return commit;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        _checks++;
    }
}
