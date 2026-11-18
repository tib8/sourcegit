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
        internal static event Action PensChanged;

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

            PensChanged?.Invoke();
        }

        public class Path(int color, bool isHighlighted)
        {
            public List<Point> Points { get; } = [];
            public int Color { get; } = color;
            public bool IsHighlighted { get; } = isHighlighted;
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

        public static CommitGraph Generate(List<Commit> commits, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, int bookmark = 0, BranchGraphColors branchColors = null)
        {
            const double unitWidth = 12;
            const double halfWidth = 6;
            const double unitHeight = 1;
            const double halfHeight = 0.5;

            var temp = new CommitGraph();
            var unsolved = new List<PathHelper>();
            var ended = new List<PathHelper>();
            var offsetY = -halfHeight;
            var branches = new Dictionary<Commit, (string Ref, int Color)>();
            var assignedColors = new HashSet<int>();
            var head = commits.Find(x => x.IsCurrentHead);
            if (branchColors != null)
            {
                foreach (var commit in commits)
                {
                    var branch = FindBranch(commit, branchColors);
                    if (branch.Ref == null)
                        continue;

                    branches.Add(commit, branch);
                    if (branch.Color >= 0)
                        assignedColors.Add(branch.Color);
                }
            }

            var headColor = head != null && (!branches.TryGetValue(head, out var headBranch) || headBranch.Color < 0)
                ? GetBookmarkColor(bookmark)
                : -1;
            if (assignedColors.Contains(headColor))
                headColor = -1;

            var laneColors = new Dictionary<string, int>(StringComparer.Ordinal);
            if (assignedColors.Count > 0)
            {
                // A new merge lane can reach a cached branch further down its first-parent history.
                for (var i = commits.Count - 1; i >= 0; i--)
                {
                    var commit = commits[i];
                    if (branches.TryGetValue(commit, out var branch))
                    {
                        if (branch.Color >= 0)
                            laneColors[commit.SHA] = branch.Color;
                    }
                    else if (commit.Parents.Count > 0 && laneColors.TryGetValue(commit.Parents[0], out var parentColor))
                    {
                        laneColors[commit.SHA] = parentColor;
                    }
                }
            }

            var colorPicker = new ColorPicker(headColor, assignedColors);
            var defHighlighting = highlighting == CommitGraphHighlighting.All;

            foreach (var commit in commits)
            {
                PathHelper major = null;
                var hasNamedBranch = branches.TryGetValue(commit, out var branch);
                var preferredColor = commit.IsCurrentHead ? headColor : -1;
                if (hasNamedBranch && branch.Color >= 0)
                    preferredColor = branch.Color;

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

                            if (commit.Parents.Count > 0)
                            {
                                major.Next = commit.Parents[0];
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
                                if (commit.Parents.Count > 0)
                                    highlightExtraCommits.Add(commit.Parents[0]);
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
                                if (commit.Parents.Count > 0)
                                    highlightExtraCommits.Add(commit.Parents[0]);
                            }
                            break;
                    }
                }
                commit.IsHighlightedInGraph = isHighlighted;

                // If no path found, create new curve for branch head
                // Otherwise, create new curve for new merged commit
                if (major == null)
                {
                    offsetX += unitWidth;

                    if (commit.Parents.Count > 0)
                    {
                        var color = preferredColor >= 0 ? preferredColor : PickLaneColor(commit.SHA, laneColors, colorPicker, unsolved);
                        major = new PathHelper(commit.Parents[0], isHighlighted, color, new Point(offsetX, offsetY));
                        unsolved.Add(major);
                        temp.Paths.Add(major.Path);
                    }
                }
                else if (isHighlighted && !major.IsHighlighted && commit.Parents.Count > 0)
                {
                    major.Highlight();
                    temp.Paths.Add(major.Path);
                }

                var hasBranch = hasNamedBranch || (branchColors == null && commit.Decorators.Exists(x => x.Type is DecoratorType.CurrentBranchHead or DecoratorType.LocalBranchHead or DecoratorType.RemoteBranchHead));
                if (major != null)
                {
                    if (preferredColor >= 0 || (major.HasBranch && hasBranch))
                    {
                        // Split at the branch tip while preserving the lane and highlighting.
                        var oldColor = major.Path.Color;
                        var color = preferredColor >= 0 ? preferredColor : colorPicker.Next(unsolved, oldColor);
                        if (color != oldColor)
                        {
                            major.ChangeColor(color);
                            if (commit.Parents.Count > 0)
                                temp.Paths.Add(major.Path);
                        }
                        if (preferredColor < 0 && commit.Parents.Count == 0)
                            colorPicker.Recycle(color);
                        else if (color != oldColor)
                            colorPicker.Recycle(oldColor);
                    }
                    major.HasBranch |= hasBranch;
                }

                // Calculate link position of this commit.
                var position = new Point(major?.LastX ?? offsetX, offsetY);
                var dotColor = major?.Path.Color ?? (preferredColor >= 0 ? preferredColor : 0);
                if (hasNamedBranch && branch.Color < 0 && Pens[dotColor].Brush is ISolidColorBrush brush)
                    branchColors.Remember(branch.Ref, commit.SHA, brush.Color);
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

                    for (int j = 1; j < commit.Parents.Count; j++)
                    {
                        var parentHash = commit.Parents[j];
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
                            var color = PickLaneColor(parentHash, laneColors, colorPicker, unsolved);
                            var l = new PathHelper(parentHash, isHighlighted, color, position, new Point(offsetX, position.Y + halfHeight));
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

        private static (string Ref, int Color) FindBranch(Commit commit, BranchGraphColors colors)
        {
            string firstRef = null;
            string cachedRef = null;
            var cachedColor = -1;
            foreach (var decorator in commit.Decorators)
            {
                var reference = decorator.Type switch
                {
                    DecoratorType.CurrentBranchHead or DecoratorType.LocalBranchHead => $"refs/heads/{decorator.Name}",
                    DecoratorType.RemoteBranchHead => $"refs/remotes/{decorator.Name}",
                    _ => null,
                };
                if (reference == null)
                    continue;

                firstRef ??= reference;
                if ((cachedRef == null || string.CompareOrdinal(reference, cachedRef) < 0) && colors.TryGetColor(reference, out var color))
                {
                    var index = Pens.FindIndex(pen => pen.Brush is ISolidColorBrush brush && brush.Color == color);
                    if (index >= 0)
                    {
                        cachedRef = reference;
                        cachedColor = index;
                    }
                }
            }
            return (cachedRef ?? firstRef, cachedColor);
        }

        private static int PickLaneColor(string sha, Dictionary<string, int> laneColors, ColorPicker picker, List<PathHelper> active)
        {
            if (laneColors.TryGetValue(sha, out var color) && !active.Exists(lane => lane.Path.Color == color))
                return color;

            return picker.Next(active);
        }

        private class ColorPicker(int reservedColor, HashSet<int> assignedColors)
        {
            public int Next(List<PathHelper> active, int exclude = -1)
            {
                var reservedCount = s_penCount > 1 && reservedColor >= 0 && reservedColor < s_penCount ? 1 : 0;
                var avoidPrevious = s_penCount - reservedCount > 1;
                var bestScore = 0;
                if (assignedColors.Count > 0)
                {
                    for (var i = 0; i < s_penCount; i++)
                    {
                        if ((reservedCount > 0 && i == reservedColor) || (avoidPrevious && i == exclude))
                            continue;

                        bestScore = Math.Max(bestScore, Score(i, active));
                        if (bestScore == 3)
                            break;
                    }
                }
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
                while ((reservedCount > 0 && color == reservedColor) || (avoidPrevious && color == exclude) || (bestScore > 0 && Score(color, active) < bestScore));

                return color;
            }

            public void Recycle(int idx)
            {
                if (idx < s_penCount && idx != reservedColor && !_colorsQueue.Contains(idx))
                    _colorsQueue.Enqueue(idx);
            }

            private int Score(int color, List<PathHelper> active)
            {
                // Prefer a free lane color, then one without a visible cached branch.
                var score = active.Exists(lane => lane.Path.Color == color) ? 0 : 2;
                return assignedColors.Contains(color) ? score : score + 1;
            }

            private readonly Queue<int> _colorsQueue = new();
        }

        private class PathHelper
        {
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
