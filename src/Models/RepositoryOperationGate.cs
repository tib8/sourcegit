using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SourceGit.Models
{
    // Worktrees have separate indexes, but fetch and other operations modify shared refs.
    internal static class RepositoryOperationGate
    {
        internal static string Key(string commonDir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDir));

        internal static bool IsLocked(string key)
        {
            lock (s_counts)
                return s_counts.ContainsKey(key);
        }

        internal static IDisposable Lock(string key)
        {
            lock (s_counts)
            {
                s_counts.TryGetValue(key, out var count);
                s_counts[key] = count + 1;
            }
            return new Lease(key);
        }

        private sealed class Lease(string key) : IDisposable
        {
            public void Dispose()
            {
                var released = Interlocked.Exchange(ref _key, null);
                if (released == null)
                    return;
                lock (s_counts)
                {
                    if (--s_counts[released] == 0)
                        s_counts.Remove(released);
                }
            }

            private string _key = key;
        }

        private static readonly Dictionary<string, int> s_counts = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}
