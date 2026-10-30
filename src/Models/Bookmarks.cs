namespace SourceGit.Models
{
    public static class Bookmarks
    {
        public static readonly Avalonia.Media.IBrush[] Brushes = [
            null,
            Avalonia.Media.Brushes.Red,
            Avalonia.Media.Brushes.Orange,
            Avalonia.Media.Brushes.Gold,
            Avalonia.Media.Brushes.ForestGreen,
            Avalonia.Media.Brushes.DarkCyan,
            Avalonia.Media.Brushes.DeepSkyBlue,
            Avalonia.Media.Brushes.Purple,
            Avalonia.Media.Brushes.Turquoise,
            Avalonia.Media.Brushes.Olive,
            Avalonia.Media.Brushes.Magenta,
            Avalonia.Media.Brushes.Khaki,
            Avalonia.Media.Brushes.Lime,
            Avalonia.Media.Brushes.RoyalBlue,
            Avalonia.Media.Brushes.Teal,
        ];

        public static Avalonia.Media.IBrush Get(int i)
        {
            return (i >= 0 && i < Brushes.Length) ? Brushes[i] : null;
        }
    }
}
