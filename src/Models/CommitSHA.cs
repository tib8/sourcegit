using System;

namespace SourceGit.Models
{
    public static class CommitSHA
    {
        public const int MinShortLength = 4;
        public const int MaxShortLength = 64;

        public static int ShortLength
        {
            get => s_shortLength;
            set => s_shortLength = Math.Clamp(value, MinShortLength, MaxShortLength);
        }

        public static string Shorten(string sha)
        {
            return Shorten(sha, ShortLength);
        }

        public static string Shorten(string sha, int length)
        {
            if (string.IsNullOrEmpty(sha))
                return string.Empty;

            return sha.Substring(0, Math.Min(sha.Length, Math.Clamp(length, MinShortLength, MaxShortLength)));
        }

        private static int s_shortLength = 12;
    }
}
