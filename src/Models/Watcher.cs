using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class Watcher : IDisposable
    {
        public Watcher(IRepository repo, string fullpath, string gitDir,
            Func<string[], CancellationToken, Task<HashSet<string>>> queryIgnored = null, string worktreeGitDir = null)
        {
            _repo = repo;
            _root = new DirectoryInfo(fullpath).FullName;
            _commonDir = RepositoryOperationGate.Key(gitDir);
            _worktreeGitDir = RepositoryOperationGate.Key(worktreeGitDir ?? gitDir);
            _queryIgnored = queryIgnored;
            _watchers = new List<FileSystemWatcher>();

            // Build output must never overflow the buffer used for shared Git metadata.
            AddWatcher(fullpath, OnWorkingCopyChanged, OnWorkingCopyWatcherError);
            AddWatcher(gitDir, OnGitDirChanged, OnWatcherError);

            _timer = new Timer(Tick, null, 100, 100);

            // Starts filesystem watchers in another thread to avoid UI blocking
            Task.Run(() =>
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                        return;

                    try
                    {
                        foreach (var watcher in _watchers)
                            watcher.EnableRaisingEvents = true;
                    }
                    catch
                    {
                        OnWatcherError(this, new ErrorEventArgs(new IOException("Failed to start filesystem watcher.")));
                    }
                }
            });
        }

        private void AddWatcher(string path, FileSystemEventHandler changed, ErrorEventHandler error)
        {
            var watcher = new FileSystemWatcher(path)
            {
                Filter = "*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (sender, e) =>
            {
                // A tracked file renamed into an ignored path must refresh its old name.
                changed(sender, new FileSystemEventArgs(WatcherChangeTypes.Deleted, path, e.OldName));
                changed(sender, e);
            };
            watcher.Error += error;
            _watchers.Add(watcher);
        }

        public IDisposable Lock() => RepositoryOperationGate.Lock(_commonDir);

        public void MarkBranchUpdated()
        {
            Interlocked.Exchange(ref _updateBranch, 0);
            Interlocked.Exchange(ref _updateWC, 0);
            Interlocked.Exchange(ref _branchChangesWorkingCopy, 0);
        }

        public void MarkTagUpdated()
        {
            Interlocked.Exchange(ref _updateTags, 0);
        }

        public void MarkWorkingCopyUpdated()
        {
            Interlocked.Exchange(ref _updateWC, 0);
        }

        public void MarkStashUpdated()
        {
            Interlocked.Exchange(ref _updateStashes, 0);
        }

        public void MarkSubmodulesUpdated()
        {
            Interlocked.Exchange(ref _updateSubmodules, 0);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _ = _cancellation.CancelAsync();
                _timer.Dispose();
                foreach (var watcher in _watchers)
                    watcher.Dispose();
                _watchers.Clear();
            }
        }

        private void Tick(object sender)
        {
            lock (_syncRoot)
            {
                if (_disposed || RepositoryOperationGate.IsLocked(_commonDir))
                    return;

                if (_workingCopyRecovery.TryTake(Environment.TickCount64))
                {
                    _repo.RefreshWorkingCopyChanges();
                    if (_repo.MayHaveSubmodules())
                        _repo.RefreshSubmodules();
                }
                StartWorkingCopyCheck();

                if (_recovery.IsPending)
                {
                    if (_recovery.TryTake(Environment.TickCount64))
                    {
                        // Clear old deadlines before dispatching. Events arriving during
                        // these refreshes remain pending for the next pass.
                        Interlocked.Exchange(ref _updateBranch, -1);
                        Interlocked.Exchange(ref _updateTags, -1);
                        Interlocked.Exchange(ref _updateStashes, -1);
                        Interlocked.Exchange(ref _updateWC, -1);
                        Interlocked.Exchange(ref _updateSubmodules, -1);
                        Interlocked.Exchange(ref _branchChangesWorkingCopy, 0);
                        _repo.RefreshBranches();
                        _repo.RefreshWorktrees();
                        _repo.RefreshTags();
                        _repo.RefreshStashes();
                        _repo.RefreshWorkingCopyChanges();
                        // Also clear removed submodules after an overflow.
                        _repo.RefreshSubmodules();
                        _repo.RefreshCommits();
                    }

                    return;
                }

                var now = DateTime.Now.ToFileTime();
                var refreshCommits = false;
                var refreshSubmodules = false;
                var refreshWC = false;

                var oldUpdateBranch = Interlocked.Exchange(ref _updateBranch, -1);
                if (oldUpdateBranch > 0)
                {
                    if (now > oldUpdateBranch)
                    {
                        refreshCommits = true;
                        refreshWC = Interlocked.Exchange(ref _branchChangesWorkingCopy, 0) != 0;
                        refreshSubmodules = refreshWC && _repo.MayHaveSubmodules();

                        _repo.RefreshBranches();
                        _repo.RefreshWorktrees();
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _updateBranch, oldUpdateBranch, -1);
                    }
                }

                if (refreshWC)
                {
                    Interlocked.Exchange(ref _updateWC, -1);
                    _repo.RefreshWorkingCopyChanges();
                }
                else
                {
                    var oldUpdateWC = Interlocked.Exchange(ref _updateWC, -1);
                    if (oldUpdateWC > 0)
                    {
                        if (now > oldUpdateWC)
                            _repo.RefreshWorkingCopyChanges();
                        else
                            Interlocked.CompareExchange(ref _updateWC, oldUpdateWC, -1);
                    }
                }

                if (refreshSubmodules)
                {
                    Interlocked.Exchange(ref _updateSubmodules, -1);
                    _repo.RefreshSubmodules();
                }
                else
                {
                    var oldUpdateSubmodule = Interlocked.Exchange(ref _updateSubmodules, -1);
                    if (oldUpdateSubmodule > 0)
                    {
                        if (now > oldUpdateSubmodule)
                            _repo.RefreshSubmodules();
                        else
                            Interlocked.CompareExchange(ref _updateSubmodules, oldUpdateSubmodule, -1);
                    }
                }

                var oldUpdateStashes = Interlocked.Exchange(ref _updateStashes, -1);
                if (oldUpdateStashes > 0)
                {
                    if (now > oldUpdateStashes)
                        _repo.RefreshStashes();
                    else
                        Interlocked.CompareExchange(ref _updateStashes, oldUpdateStashes, -1);
                }

                var oldUpdateTags = Interlocked.Exchange(ref _updateTags, -1);
                if (oldUpdateTags > 0)
                {
                    if (now > oldUpdateTags)
                    {
                        refreshCommits = true;
                        _repo.RefreshTags();
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _updateTags, oldUpdateTags, -1);
                    }
                }

                if (refreshCommits)
                    _repo.RefreshCommits();
            }
        }

        /// <summary>
        ///     The internal buffer overflowed (a large fetch or checkout writes thousands of files), so
        ///     some events were dropped and we cannot know which. Coalesce recovery requests
        ///     until things settle, with a bounded delay during continuous activity.
        /// </summary>
        private void OnWatcherError(object o, ErrorEventArgs e)
        {
            if (!_disposed)
                _recovery.Schedule(Environment.TickCount64);
        }

        private void OnGitDirChanged(object o, FileSystemEventArgs e)
        {
            if (_disposed)
                return;

            _recovery.RecordActivity(Environment.TickCount64);
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            HandleGitDirFileChanged(name);
        }

        private void OnWorkingCopyWatcherError(object sender, ErrorEventArgs e)
        {
            if (!_disposed)
                _workingCopyRecovery.Schedule(Environment.TickCount64);
        }

        private void OnWorkingCopyChanged(object sender, FileSystemEventArgs e)
        {
            if (_disposed || string.IsNullOrEmpty(e.Name))
                return;
            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.Equals(".git", StringComparison.Ordinal) || name.StartsWith(".git/", StringComparison.Ordinal))
                return;
            _workingCopyRecovery.RecordActivity(Environment.TickCount64);
            lock (_pendingPaths)
            {
                if (_pendingPaths.Count == 0)
                    _firstPathActivity = Environment.TickCount64;
                _lastPathActivity = Environment.TickCount64;
                if (_pendingPaths.Count < 8192)
                {
                    _pendingPaths.Add(name);
                    if (e.ChangeType != WatcherChangeTypes.Changed)
                        _structuralPaths.Add(name);
                }
                else
                    _workingCopyRecovery.Schedule(Environment.TickCount64);
            }
        }

        // Called under _syncRoot. Native callbacks only enqueue paths; slow inspection runs
        // on a worker, with at most one ignore query in flight per worktree.
        private void StartWorkingCopyCheck()
        {
            if (_checkingPaths)
                return;
            string[] paths;
            HashSet<string> structuralPaths;
            lock (_pendingPaths)
            {
                var now = Environment.TickCount64;
                if (_pendingPaths.Count == 0 || (now - _lastPathActivity < 1000 && now - _firstPathActivity < 5000))
                    return;
                paths = new string[_pendingPaths.Count];
                _pendingPaths.CopyTo(paths);
                _pendingPaths.Clear();
                structuralPaths = new(_structuralPaths, StringComparer.Ordinal);
                _structuralPaths.Clear();
            }
            _checkingPaths = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var ignored = _queryIgnored == null ? new HashSet<string>() : await _queryIgnored(paths, _cancellation.Token).ConfigureAwait(false);
                    foreach (var name in paths)
                    {
                        var fullpath = Path.Combine(_root, name);
                        // A directory's timestamp changes when ignored children are written.
                        // Creation/deletion/rename must still inspect its tracked descendants.
                        if (!structuralPaths.Contains(name) && Directory.Exists(fullpath))
                            continue;
                        if (ignored.Contains(name) && !name.Equals(".gitignore", StringComparison.Ordinal) &&
                            !name.EndsWith("/.gitignore", StringComparison.Ordinal))
                            continue;
                        HandleWorkingCopyFileChanged(name, fullpath);
                    }
                }
                catch (OperationCanceledException) when (_disposed) { }
                catch
                {
                    // Failure must never hide real edits.
                    Interlocked.Exchange(ref _updateWC, DateTime.Now.ToFileTime());
                    if (_repo.MayHaveSubmodules())
                        Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.ToFileTime());
                }
                finally
                {
                    lock (_syncRoot)
                        _checkingPaths = false;
                }
            });
        }

        private void HandleGitDirFileChanged(string name)
        {
            if (name.StartsWith("worktrees/", StringComparison.Ordinal))
            {
                var localPrefix = Path.GetRelativePath(_commonDir, _worktreeGitDir).Replace('\\', '/') + "/";
                if (name.StartsWith(localPrefix, StringComparison.Ordinal))
                    name = name.Substring(localPrefix.Length);
            }

            if (name.Contains("fsmonitor--daemon/", StringComparison.Ordinal) ||
                name.EndsWith(".lock", StringComparison.Ordinal) ||
                name.StartsWith("lfs/", StringComparison.Ordinal))
                return;

            if (name.StartsWith("modules", StringComparison.Ordinal))
            {
                if (name.EndsWith("/HEAD", StringComparison.Ordinal) ||
                    name.EndsWith("/ORIG_HEAD", StringComparison.Ordinal))
                {
                    var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                    Interlocked.Exchange(ref _updateSubmodules, desired);
                    Interlocked.Exchange(ref _updateWC, desired);
                }
            }
            else if (name.Equals("MERGE_HEAD", StringComparison.Ordinal) ||
                name.Equals("AUTO_MERGE", StringComparison.Ordinal))
            {
                if (_repo.MayHaveSubmodules())
                    Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
            }
            else if (name.StartsWith("refs/tags", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateTags, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.StartsWith("refs/stash", StringComparison.Ordinal) || name.Equals("logs/refs/stash", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateStashes, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.Equals("HEAD", StringComparison.Ordinal) ||
                name.Equals("config", StringComparison.Ordinal) ||
                name.Equals("config.worktree", StringComparison.Ordinal) ||
                name.Equals("BISECT_START", StringComparison.Ordinal) ||
                name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                name.StartsWith("refs/remotes/", StringComparison.Ordinal) ||
                (name.StartsWith("worktrees/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                // Remote refs and other worktrees' HEADs cannot change this worktree's status.
                if (!name.StartsWith("refs/remotes/", StringComparison.Ordinal) &&
                    !name.StartsWith("worktrees/", StringComparison.Ordinal))
                    Interlocked.Exchange(ref _branchChangesWorkingCopy, 1);
                Interlocked.Exchange(ref _updateBranch, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.Equals("packed-refs", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _branchChangesWorkingCopy, 1);
                var desired = DateTime.Now.AddSeconds(.5).ToFileTime();
                Interlocked.Exchange(ref _updateBranch, desired);
                Interlocked.Exchange(ref _updateTags, desired);
            }
            else if (name.StartsWith("reftable/", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _branchChangesWorkingCopy, 1);
                var desired = DateTime.Now.AddSeconds(.5).ToFileTime();
                Interlocked.Exchange(ref _updateBranch, desired);
                Interlocked.Exchange(ref _updateTags, desired);
                Interlocked.Exchange(ref _updateStashes, desired);
            }
            else if (name.Equals("index", StringComparison.Ordinal) || name.Equals("info/exclude", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
            }
        }

        private void HandleWorkingCopyFileChanged(string name, string fullpath)
        {
            if (name.Equals(".gitmodules", StringComparison.Ordinal))
            {
                var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                Interlocked.Exchange(ref _updateSubmodules, desired);
                Interlocked.Exchange(ref _updateWC, desired);
                return;
            }

            var dir = Directory.Exists(fullpath) ? fullpath : Path.GetDirectoryName(fullpath);
            if (IsInSubmodule(dir))
            {
                Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
                return;
            }

            Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
        }

        private bool IsInSubmodule(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Equals(_root, StringComparison.Ordinal))
                return false;

            if (File.Exists($"{folder}/.git"))
                return true;

            return IsInSubmodule(Path.GetDirectoryName(folder));
        }

        private readonly object _syncRoot = new();
        private readonly WatcherRecovery _recovery = new();
        private volatile bool _disposed;
        private readonly IRepository _repo;
        private readonly string _root;
        private List<FileSystemWatcher> _watchers;
        private Timer _timer;

        private readonly string _commonDir;
        private readonly string _worktreeGitDir;
        private readonly Func<string[], CancellationToken, Task<HashSet<string>>> _queryIgnored;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly WatcherRecovery _workingCopyRecovery = new();
        private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> _structuralPaths = new(StringComparer.Ordinal);
        private bool _checkingPaths;
        private long _firstPathActivity;
        private long _lastPathActivity;
        private long _updateWC;
        private long _updateBranch;
        private int _branchChangesWorkingCopy;
        private long _updateSubmodules;
        private long _updateStashes;
        private long _updateTags;
    }
}
