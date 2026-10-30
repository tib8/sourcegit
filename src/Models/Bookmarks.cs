using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SourceGit.Models
{
    public static class Bookmarks
    {
        // Paul Tol's Bright palette; grey is reserved for dimmed history.
        public static readonly List<Color> Colors = [
            Color.Parse("#4477AA"), Color.Parse("#66CCEE"), Color.Parse("#228833"),
            Color.Parse("#CCBB44"), Color.Parse("#EE6677"), Color.Parse("#AA3377"),
        ];
        public static readonly IBrush[] Brushes = [null, .. Colors.ConvertAll(c => (IBrush)new ImmutableSolidColorBrush(c))];

        public static IBrush Get(int i) => i >= 0 && i < Brushes.Length ? Brushes[i] : null;

        // IDs 101-106 distinguish new choices from the legacy indices 1-14.
        public static int FromSerialized(int value)
        {
            if (value >= 101 && value <= 100 + Colors.Count)
                return value - 100;
            if (value <= 0 || value >= s_legacyColors.Length)
                return 0;
            var best = 0;
            var distance = double.MaxValue;
            for (var i = 0; i < Colors.Count; i++)
            {
                var candidate = GraphColorContrast.NormalDistance(s_legacyColors[value], Colors[i]);
                if (candidate < distance)
                {
                    best = i + 1;
                    distance = candidate;
                }
            }
            return best;
        }

        public static int ToSerialized(int value) => value > 0 && value <= Colors.Count ? 100 + value : 0;

        private static readonly Color[] s_legacyColors = [
            default, Avalonia.Media.Colors.Red, Avalonia.Media.Colors.Orange, Avalonia.Media.Colors.Gold,
            Avalonia.Media.Colors.ForestGreen, Avalonia.Media.Colors.DarkCyan, Avalonia.Media.Colors.DeepSkyBlue,
            Avalonia.Media.Colors.Purple, Avalonia.Media.Colors.Turquoise, Avalonia.Media.Colors.Olive,
            Avalonia.Media.Colors.Magenta, Avalonia.Media.Colors.Khaki, Avalonia.Media.Colors.Lime,
            Avalonia.Media.Colors.RoyalBlue, Avalonia.Media.Colors.Teal,
        ];
    }
}
