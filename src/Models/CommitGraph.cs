using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Media;

namespace SourceGit.Models
{
    public record CommitGraphLayout(double StartY, double ClipWidth, double RowHeight);

    public enum CommitGraphHighlighting
    {
        All = 0,
        CurrentBranchOnly,
        SelectedCommitsOnly,
        CurrentBranchAndSelectedCommits,
        SelectedCommitsOnlyFirstParent,
    }

    public class CommitGraph
    {
        public static List<Pen> Pens { get; } = [];

        public static void SetDefaultPens(double thickness = 2)
        {
            SetPens(s_defaultPenColors, thickness);
        }

        public static void SetPens(List<Color> colors, double thickness)
        {
            Pens.Clear();

            foreach (var c in colors)
                Pens.Add(new Pen(c.ToUInt32(), thickness));

            s_penCount = colors.Count;
            for (var i = 1; i < Bookmarks.Brushes.Length; i++)
                Pens.Add(new Pen(Bookmarks.Brushes[i], thickness));
        }

        public class Path(int color, bool isHighlighted)
        {
            public List<Point> Points { get; } = [];
            public int Color { get; } = color;
            public bool IsHighlighted { get; } = isHighlighted;
            public bool IsDotted { get; init; }
        }

        public class Link
        {
            public Point Start;
            public Point Control;
            public Point End;
            public int Color;
            public bool IsHighlighted;
        }

        public enum DotType
        {
            Default,
            Head,
            Merge,
            WorkingDirectory,
        }

        public class Dot
        {
            public DotType Type;
            public Point Center;
            public int Color;
            public bool IsHighlighted;
        }

        public List<Path> Paths { get; } = [];
        public List<Link> Links { get; } = [];
        public List<Dot> Dots { get; } = [];

        public static CommitGraph GenerateFiltered(List<Commit> allCommits, List<Commit> visibleCommits, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, bool hasWorkingDirectory = false, bool reserveHeadLane = true, int bookmark = 0)
        {
            var visible = new HashSet<string>();
            foreach (var commit in visibleCommits)
                visible.Add(commit.SHA);

            var visibleOrder = new Dictionary<string, int>();
            for (var i = 0; i < visibleCommits.Count; i++)
                visibleOrder[visibleCommits[i].SHA] = i;

            var nearest = new Dictionary<string, List<string>>();
            var parents = new Dictionary<string, List<string>>();
            // History is ordered children before parents. Resolve hidden ancestors from
            // oldest to newest, stopping each path at its first visible commit.
            for (var i = allCommits.Count - 1; i >= 0; i--)
            {
                var commit = allCommits[i];
                var parentCount = firstParentOnlyEnabled ? Math.Min(1, commit.Parents.Count) : commit.Parents.Count;
                List<string> resolved = [];
                if (parentCount == 1)
                {
                    if (nearest.TryGetValue(commit.Parents[0], out var ancestors))
                        resolved = ancestors;
                }
                else if (parentCount > 1)
                {
                    var seen = new HashSet<string>();
                    for (var j = 0; j < parentCount; j++)
                    {
                        if (!nearest.TryGetValue(commit.Parents[j], out var ancestors))
                            continue;

                        foreach (var ancestor in ancestors)
                        {
                            if (seen.Add(ancestor))
                                resolved.Add(ancestor);
                        }
                    }
                }

                if (parentCount > 1 && resolved.Count > 1)
                {
                    // Hidden merges can expose both a visible commit and its ancestors.
                    // Keep only the nearest targets: the older targets are already
                    // reachable through the nearer ones and would add redundant lanes.
                    var candidates = new HashSet<string>(resolved);
                    var redundant = new HashSet<string>();
                    var visited = new HashSet<string>();
                    var pending = new Stack<string>();
                    var oldest = 0;
                    foreach (var target in resolved)
                    {
                        oldest = Math.Max(oldest, visibleOrder[target]);
                        foreach (var parent in parents[target])
                            pending.Push(parent);
                    }
                    while (pending.TryPop(out var ancestor))
                    {
                        if (visibleOrder[ancestor] > oldest || !visited.Add(ancestor))
                            continue;

                        if (candidates.Contains(ancestor))
                            redundant.Add(ancestor);
                        foreach (var parent in parents[ancestor])
                            pending.Push(parent);
                    }
                    resolved.RemoveAll(redundant.Contains);
                }

                if (visible.Contains(commit.SHA))
                {
                    parents[commit.SHA] = resolved;
                    nearest[commit.SHA] = [commit.SHA];
                }
                else
                {
                    nearest[commit.SHA] = resolved;
                }
            }

            return Generate(visibleCommits, firstParentOnlyEnabled, highlighting, highlightExtraCommits, hasWorkingDirectory, reserveHeadLane, parents, bookmark);
        }

        public static CommitGraph Generate(List<Commit> commits, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, bool hasWorkingDirectory = false, bool reserveHeadLane = true, int bookmark = 0)
        {
            return Generate(commits, firstParentOnlyEnabled, highlighting, highlightExtraCommits, hasWorkingDirectory, reserveHeadLane, null, bookmark);
        }

        private static CommitGraph Generate(List<Commit> commits, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, bool hasWorkingDirectory, bool reserveHeadLane, Dictionary<string, List<string>> parentOverrides, int bookmark)
        {
            const double unitWidth = 12;
            const double halfWidth = 6;
            const double unitHeight = 1;
            const double halfHeight = 0.5;

            var temp = new CommitGraph();
            var unsolved = new List<PathHelper>();
            var ended = new List<PathHelper>();
            var offsetY = -halfHeight;
            var headColor = commits.Exists(x => x.IsCurrentHead) ? GetBookmarkColor(bookmark) : -1;
            var colorPicker = new ColorPicker(headColor);
            var defHighlighting = highlighting == CommitGraphHighlighting.All;

            // Keep HEAD's first-parent ancestry in the first lane, even when other
            // branch tips appear above it. The reservation is not a Git commit or
            // a visible edge; drawing starts when the actual HEAD is encountered.
            PathHelper headLane = null;
            var headCommit = commits.Find(x => x.IsCurrentHead);
            if (reserveHeadLane && headCommit != null)
            {
                headLane = new PathHelper(headCommit.SHA, defHighlighting, headColor >= 0 ? headColor : colorPicker.Next(), new Point(10, -halfHeight));
                headLane.Branches = headCommit.Decorators.FindAll(x => x.IsBranch);
                unsolved.Add(headLane);
            }

            foreach (var commit in commits)
            {
                var parents = parentOverrides != null && parentOverrides.TryGetValue(commit.SHA, out var projected) ? projected : commit.Parents;
                PathHelper major = null;
                var isBookmarkedHead = commit.IsCurrentHead && headColor >= 0;

                // Update current y offset
                offsetY += unitHeight;

                // Find first curves that links to this commit and marks others that links to this commit ended.
                var offsetX = 4 - halfWidth;
                var maxOffsetOld = unsolved.Count > 0 ? unsolved[^1].LastX : offsetX + unitWidth;
                var isHighlighted = defHighlighting;
                foreach (var l in unsolved)
                {
                    if (l.Next.Equals(commit.SHA, StringComparison.Ordinal))
                    {
                        if (major == null)
                        {
                            offsetX += unitWidth;
                            major = l;
                            isHighlighted = major.IsHighlighted;

                            if (parents.Count > 0)
                            {
                                major.Next = parents[0];
                                major.Goto(offsetX, offsetY, halfHeight);
                            }
                            else
                            {
                                major.End(offsetX, offsetY, halfHeight);
                                ended.Add(l);
                            }
                        }
                        else
                        {
                            l.End(major.LastX, offsetY, halfHeight);
                            ended.Add(l);

                            if (!isHighlighted && l.IsHighlighted)
                                isHighlighted = true;
                        }
                    }
                    else
                    {
                        offsetX += unitWidth;
                        l.Pass(offsetX, offsetY, halfHeight);
                    }
                }

                // Remove ended curves from unsolved
                foreach (var l in ended)
                {
                    colorPicker.Recycle(l.Path.Color);
                    unsolved.Remove(l);
                }
                ended.Clear();

                // Calculate highlighted state
                if (!isHighlighted)
                {
                    switch (highlighting)
                    {
                        case CommitGraphHighlighting.CurrentBranchOnly:
                            isHighlighted = commit.IsMerged;
                            break;
                        case CommitGraphHighlighting.SelectedCommitsOnly:
                        case CommitGraphHighlighting.SelectedCommitsOnlyFirstParent:
                            if (highlightExtraCommits.Remove(commit.SHA))
                            {
                                isHighlighted = true;
                                // Highlight first parent, other parents are dealt with later
                                if (parents.Count > 0)
                                    highlightExtraCommits.Add(parents[0]);
                            }
                            break;
                        default: // CommitGraphHighlighting.CurrentBranchAndSelectedCommits
                            if (commit.IsMerged)
                            {
                                isHighlighted = true;
                            }
                            else if (highlightExtraCommits.Remove(commit.SHA))
                            {
                                isHighlighted = true;
                                // Highlight first parent, other parents are dealt with later
                                if (parents.Count > 0)
                                    highlightExtraCommits.Add(parents[0]);
                            }
                            break;
                    }
                }
                commit.IsHighlightedInGraph = isHighlighted;

                if (headLane != null && commit.IsCurrentHead && major == headLane)
                {
                    headLane.Path.Points.Clear();
                    headLane.Path.Points.Add(new Point(10, offsetY));
                }

                // If no path found, create new curve for branch head
                // Otherwise, create new curve for new merged commit
                if (major == null)
                {
                    offsetX += unitWidth;

                    if (parents.Count > 0)
                    {
                        var color = isBookmarkedHead ? headColor : colorPicker.Next();
                        major = new PathHelper(parents[0], isHighlighted, color, new Point(offsetX, offsetY));
                        unsolved.Add(major);
                        temp.Paths.Add(major.Path);
                    }
                }
                else if (isHighlighted && !major.IsHighlighted && parents.Count > 0)
                {
                    major.Highlight();
                    temp.Paths.Add(major.Path);
                }

                if (headLane != null && commit.IsCurrentHead && major == headLane && parents.Count > 0 && !temp.Paths.Contains(major.Path))
                    temp.Paths.Add(major.Path);

                var branches = commit.Decorators.FindAll(x => x.IsBranch);
                if (major != null && branches.Count > 0)
                {
                    if (major.HasBranch && !isBookmarkedHead)
                    {
                        // A named branch can start on an existing line without a fork.
                        // Split its color at the branch tip, preserving the lane and highlighting.
                        var oldColor = major.Path.Color;
                        var color = colorPicker.Next(oldColor);
                        if (color != oldColor)
                        {
                            major.ChangeColor(color);
                            if (parents.Count > 0)
                                temp.Paths.Add(major.Path);
                        }
                        colorPicker.Recycle(parents.Count > 0 ? oldColor : color);
                    }
                    major.HasBranch = true;
                }

                // Carry branch identity along the lane, independently of recycled graph colors.
                commit.GraphBranches = branches.Count > 0 ? branches : major?.Branches ?? [];
                if (major != null && branches.Count > 0)
                    major.Branches = branches;

                if (isBookmarkedHead && major != null && major.Path.Color != headColor)
                {
                    colorPicker.Recycle(major.Path.Color);
                    major.ChangeColor(headColor);
                    if (parents.Count > 0)
                        temp.Paths.Add(major.Path);
                }

                // Calculate link position of this commit.
                var position = new Point(major?.LastX ?? offsetX, offsetY);
                var dotColor = major?.Path.Color ?? (isBookmarkedHead ? headColor : 0);
                var anchor = new Dot() { Center = position, Color = dotColor, IsHighlighted = isHighlighted };
                if (commit.IsCurrentHead)
                    anchor.Type = DotType.Head;
                else if (commit.Parents.Count > 1)
                    anchor.Type = DotType.Merge;
                else
                    anchor.Type = DotType.Default;
                temp.Dots.Add(anchor);

                // Deal with other parents (the first parent has been processed)
                if (!firstParentOnlyEnabled)
                {
                    if (highlighting == CommitGraphHighlighting.SelectedCommitsOnlyFirstParent)
                        isHighlighted = false;

                    for (int j = 1; j < parents.Count; j++)
                    {
                        var parentHash = parents[j];
                        var parent = unsolved.Find(x => x.Next.Equals(parentHash, StringComparison.Ordinal));
                        if (parent != null)
                        {
                            if (isHighlighted && !parent.IsHighlighted)
                            {
                                parent.Goto(parent.LastX, offsetY + halfHeight, halfHeight);
                                parent.Highlight();
                                temp.Paths.Add(parent.Path);
                            }

                            temp.Links.Add(new Link
                            {
                                Start = position,
                                End = new Point(parent.LastX, offsetY + halfHeight),
                                Control = new Point(parent.LastX, position.Y),
                                Color = parent.Path.Color,
                                IsHighlighted = isHighlighted,
                            });
                        }
                        else
                        {
                            offsetX += unitWidth;

                            // Create new curve for parent commit that not includes before
                            var l = new PathHelper(parentHash, isHighlighted, colorPicker.Next(), position, new Point(offsetX, position.Y + halfHeight));
                            l.Branches = commit.GraphBranches;
                            unsolved.Add(l);
                            temp.Paths.Add(l.Path);
                        }
                    }
                }

                // Margins & colors (used by Views.Histories).
                commit.Color = dotColor;
                commit.LeftMargin = Math.Max(offsetX, maxOffsetOld) + halfWidth + 2;
            }

            // Deal with curves haven't ended yet.
            for (var i = 0; i < unsolved.Count; i++)
            {
                var path = unsolved[i];
                var endY = (commits.Count - 0.5) * unitHeight;

                if (path.Path.Points.Count == 1 && Math.Abs(path.Path.Points[0].Y - endY) < 0.0001)
                    continue;

                path.End((i + 0.5) * unitWidth + 4, endY + halfHeight, halfHeight);
            }
            unsolved.Clear();

            if (hasWorkingDirectory)
            {
                // Add a presentation row without moving any of the existing graph lanes.
                var offset = new Vector(0, 1);
                foreach (var path in temp.Paths)
                    for (var i = 0; i < path.Points.Count; i++)
                        path.Points[i] += offset;
                foreach (var link in temp.Links)
                {
                    link.Start += offset;
                    link.Control += offset;
                    link.End += offset;
                }
                foreach (var dot in temp.Dots)
                    dot.Center += offset;

                var working = new Dot { Type = DotType.WorkingDirectory, Center = new Point(10, 0.5), IsHighlighted = true };
                var headIndex = commits.FindIndex(x => x.IsCurrentHead);
                if (headIndex >= 0)
                {
                    var head = temp.Dots[headIndex];
                    working.Center = new Point(head.Center.X, 0.5);
                    var lastJunction = working.Center;
                    foreach (var link in temp.Links)
                    {
                        if (link.End.X == head.Center.X && link.End.Y > lastJunction.Y && link.End.Y < head.Center.Y)
                            lastJunction = link.End;
                    }

                    var connection = lastJunction == working.Center ? head.Center : lastJunction;
                    var edge = new Path(head.Color, true) { IsDotted = true };
                    edge.Points.Add(working.Center);
                    edge.Points.Add(connection);
                    temp.Paths.Insert(0, edge);
                    if (connection != head.Center)
                    {
                        var continuation = new Path(head.Color, true);
                        continuation.Points.Add(connection);
                        continuation.Points.Add(head.Center);
                        temp.Paths.Insert(1, continuation);
                    }
                    working.Color = head.Color;
                }
                temp.Dots.Insert(0, working);
            }

            return temp;
        }

        private static int GetBookmarkColor(int bookmark)
        {
            if (Bookmarks.Get(bookmark) is not ISolidColorBrush brush)
                return -1;

            for (var i = 0; i < s_penCount; i++)
            {
                if (Pens[i].Brush is ISolidColorBrush graphBrush && graphBrush.Color == brush.Color)
                    return i;
            }

            return s_penCount + bookmark - 1;
        }

        private class ColorPicker(int reservedColor)
        {
            public int Next(int exclude = -1)
            {
                var reservedCount = s_penCount > 1 && reservedColor >= 0 && reservedColor < s_penCount ? 1 : 0;
                int color;
                do
                {
                    if (_colorsQueue.Count == 0)
                    {
                        for (var i = 0; i < s_penCount; i++)
                            _colorsQueue.Enqueue(i);
                    }

                    color = _colorsQueue.Dequeue();
                }
                while ((reservedCount > 0 && color == reservedColor) || (s_penCount - reservedCount > 1 && color == exclude));

                return color;
            }

            public void Recycle(int idx)
            {
                if (idx < s_penCount && idx != reservedColor && !_colorsQueue.Contains(idx))
                    _colorsQueue.Enqueue(idx);
            }

            private Queue<int> _colorsQueue = new Queue<int>();
        }

        private class PathHelper
        {
            public List<Decorator> Branches { get; set; } = [];
            public bool HasBranch { get; set; }
            public Path Path { get; private set; }
            public string Next { get; set; }
            public double LastX { get; private set; }
            public bool IsHighlighted { get => Path.IsHighlighted; }

            public PathHelper(string next, bool IsHighlighted, int color, Point start)
            {
                Next = next;
                LastX = start.X;
                _lastY = start.Y;

                Path = new Path(color, IsHighlighted);
                Path.Points.Add(start);
            }

            public PathHelper(string next, bool IsHighlighted, int color, Point start, Point to)
            {
                Next = next;
                LastX = to.X;
                _lastY = to.Y;

                Path = new Path(color, IsHighlighted);
                Path.Points.Add(start);
                Path.Points.Add(to);
            }

            /// <summary>
            ///     A path that just passed this row.
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void Pass(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    Add(LastX, y - halfHeight);
                    y += halfHeight;
                    Add(x, y);
                }

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     A path that has commit in this row but not ended
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void Goto(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    var minY = y - halfHeight;
                    if (minY > _lastY)
                        minY -= halfHeight;

                    Add(LastX, minY);
                    Add(x, y);
                }

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     A path that has commit in this row and end.
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void End(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    Add(LastX, y - halfHeight);
                }

                Add(x, y);

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     End the current path and create a new highlighted from the end.
            /// </summary>
            public void Highlight()
            {
                Restart(Path.Color, true);
            }

            public void ChangeColor(int color)
            {
                Restart(color, Path.IsHighlighted);
            }

            private void Restart(int color, bool isHighlighted)
            {
                Add(LastX, _lastY);

                Path = new Path(color, isHighlighted);
                Path.Points.Add(new Point(LastX, _lastY));
                _endY = 0;
            }

            private void Add(double x, double y)
            {
                if (_endY < y)
                {
                    Path.Points.Add(new Point(x, y));
                    _endY = y;
                }
            }

            private double _lastY = 0;
            private double _endY = 0;
        }

        private static int s_penCount = 0;
        private static readonly List<Color> s_defaultPenColors = [
            Colors.Orange,
            Colors.ForestGreen,
            Colors.Turquoise,
            Colors.Olive,
            Colors.Magenta,
            Colors.Red,
            Colors.Khaki,
            Colors.Lime,
            Colors.RoyalBlue,
            Colors.Teal,
        ];
    }
}
