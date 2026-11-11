using SourceGit.Models;

await CoalescesRequests();
await CategoriesRunIndependently();
await BusyStateCoversAllCategories();
await RecoversAfterFailure();
await CloseDiscardsPendingAndCancelsActive();
await CloseDoesNotWaitForProcessTermination();
RecoveryWaitsForQuietAndHasABound();
await WatcherRecoversOnceAndHonorsOperationLock();
await SharedWorktreeOperationLock();
await IgnoredBuildOutputAndRealEdits();
await WorkingCopyOverflowDoesNotRefreshHistory();
Console.WriteLine("Refresh coordinator and watcher recovery checks passed.");

static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static async Task CoalescesRequests()
{
    var started = Signal();
    var release = Signal();
    var runs = 0;
    var active = 0;
    var applied = 0;
    var errors = new List<Exception>();
    using var coordinator = new RefreshCoordinator(errors.Add);
    async Task Refresh(RefreshCoordinator.Context context)
    {
        Check(Interlocked.Increment(ref active) == 1, "Same category ran concurrently.");
        if (Interlocked.Increment(ref runs) == 1)
        {
            started.SetResult();
            await release.Task;
            Check(!context.CancellationToken.IsCancellationRequested, "A dirty request cancelled the active query.");
        }

        if (context.IsCurrent)
            Interlocked.Increment(ref applied);
        Interlocked.Decrement(ref active);
    }

    coordinator.Request(RefreshKind.Commits, Refresh);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Parallel.For(0, 1000, _ => coordinator.Request(RefreshKind.Commits, Refresh));
    release.SetResult();
    await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(runs == 2 && applied == 1, $"Expected one follow-up and one current publication, got {runs}/{applied}.");
    Check(errors.Count == 0, "Coordinator swallowed a check failure.");

    coordinator.Request(RefreshKind.Commits, Refresh);
    await coordinator.WhenIdleAsync();
    Check(runs == 3 && applied == 2, "A request after completion was lost.");
}

static async Task CategoriesRunIndependently()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    var blocked = Signal();
    var release = Signal();
    var independent = Signal();
    coordinator.Request(RefreshKind.Commits, async _ =>
    {
        blocked.SetResult();
        await release.Task;
    });
    await blocked.Task;
    coordinator.Request(RefreshKind.WorkingCopy, _ =>
    {
        independent.SetResult();
        return Task.CompletedTask;
    });
    await independent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    release.SetResult();
    await coordinator.WhenIdleAsync();
}

static async Task BusyStateCoversAllCategories()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    var historyStarted = Signal();
    var statusStarted = Signal();
    var historyReleased = Signal();
    var statusReleased = Signal();
    var idle = Signal();
    var observedBusy = false;
    coordinator.StateChanged += () =>
    {
        if (coordinator.IsRefreshing)
            observedBusy = true;
        else
            idle.TrySetResult();
    };
    coordinator.Request(RefreshKind.Commits, async _ =>
    {
        historyStarted.SetResult();
        await historyReleased.Task;
    });
    coordinator.Request(RefreshKind.WorkingCopy, async _ =>
    {
        statusStarted.SetResult();
        await statusReleased.Task;
    });
    await Task.WhenAll(historyStarted.Task, statusStarted.Task);
    Check(observedBusy && coordinator.IsRefreshing, "Active refresh was not signalled.");
    historyReleased.SetResult();
    // Wait until just the history category is free, with the status query still blocked.
    var historyCompleted = Signal();
    coordinator.Request(RefreshKind.Commits, _ => { historyCompleted.SetResult(); return Task.CompletedTask; });
    await historyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(coordinator.IsRefreshing && !idle.Task.IsCompleted, "Busy state ended while another category was active.");
    statusReleased.SetResult();
    await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!coordinator.IsRefreshing, "Idle repository kept its busy state.");
}

static async Task RecoversAfterFailure()
{
    var errors = 0;
    using var coordinator = new RefreshCoordinator(_ => Interlocked.Increment(ref errors));
    coordinator.Request(RefreshKind.Tags, _ => throw new IOException("Expected failure"));
    await coordinator.WhenIdleAsync();
    Check(errors == 1, "Failed refresh was not reported.");
    var ran = false;
    coordinator.Request(RefreshKind.Tags, _ => { ran = true; return Task.CompletedTask; });
    await coordinator.WhenIdleAsync();
    Check(ran, "Failure left the category stuck.");

    var started = Signal();
    var release = Signal();
    coordinator.Request(RefreshKind.Tags, async _ =>
    {
        started.SetResult();
        await release.Task;
        throw new IOException("Failure with a pending refresh");
    });
    await started.Task;
    ran = false;
    coordinator.Request(RefreshKind.Tags, _ => { ran = true; return Task.CompletedTask; });
    release.SetResult();
    await coordinator.WhenIdleAsync();
    Check(ran, "Failure dropped a pending refresh.");
}

static async Task CloseDoesNotWaitForProcessTermination()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    using var releaseTermination = new ManualResetEventSlim();
    var started = Signal();
    var terminating = Signal();
    coordinator.Request(RefreshKind.WorkingCopy, async context =>
    {
        using var registration = context.CancellationToken.Register(() =>
        {
            terminating.SetResult();
            releaseTermination.Wait();
        });
        started.SetResult();
        await Task.Delay(Timeout.Infinite, context.CancellationToken);
    });
    await started.Task;
    var closing = Task.Run(coordinator.Dispose);
    try
    {
        await terminating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closing.WaitAsync(TimeSpan.FromSeconds(1));
    }
    finally
    {
        releaseTermination.Set();
    }
    await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task CloseDiscardsPendingAndCancelsActive()
{
    using var coordinator = new RefreshCoordinator(ex => throw ex);
    var started = Signal();
    var cancelled = Signal();
    var runs = 0;
    coordinator.Request(RefreshKind.WorkingCopy, async context =>
    {
        Interlocked.Increment(ref runs);
        started.SetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Check(!context.IsCurrent, "Closed repository can still publish results.");
            cancelled.SetResult();
        }
    });
    await started.Task;
    coordinator.Request(RefreshKind.WorkingCopy, _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
    coordinator.Dispose();
    Check(!coordinator.IsRefreshing, "Closed repository kept its busy state.");
    coordinator.Dispose();
    coordinator.Request(RefreshKind.Tags, _ => throw new Exception("Request after close ran."));
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coordinator.WhenIdleAsync();
    Check(runs == 1, "Close started a pending refresh.");
}

static void RecoveryWaitsForQuietAndHasABound()
{
    var recovery = new WatcherRecovery();
    recovery.Schedule(100);
    recovery.Schedule(200);
    recovery.RecordActivity(900);
    Check(!recovery.TryTake(1899), "Recovery did not wait for quiet.");
    Check(recovery.TryTake(1900), "Recovery missed the end of the quiet period.");
    Check(!recovery.TryTake(10000), "One overflow burst caused multiple recoveries.");

    recovery.Schedule(11000);
    for (var now = 11100; now < 16000; now += 100)
    {
        recovery.Schedule(now);
        recovery.RecordActivity(now);
        Check(!recovery.TryTake(now), "Continuous events triggered recovery too early.");
    }
    recovery.RecordActivity(16000);
    Check(recovery.TryTake(16000), "Repeated errors postponed recovery past the maximum wait.");

    recovery.Schedule(16001);
    Check(!recovery.TryTake(16002), "Next burst inherited the previous deadline.");
    Check(recovery.TryTake(17001), "A new overflow after recovery was dropped.");
}

static async Task WatcherRecoversOnceAndHonorsOperationLock()
{
    var path = Path.Combine(Path.GetTempPath(), $"sourcegit-watcher-check-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(path, ".git"));
    try
    {
        var repository = new RecordingRepository();
        using var watcher = new Watcher(repository, path, Path.Combine(path, ".git"));
        using var operation = watcher.Lock();
        // Inject native buffer errors; the subsequent dispatch still uses the real timer.
        var error = typeof(Watcher).GetMethod("OnWatcherError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 20; i++)
            error.Invoke(watcher, [null, new ErrorEventArgs(new InternalBufferOverflowException())]);
        await Task.Delay(1200);
        Check(repository.Total == 0, "Watcher refreshed during an application operation.");
        operation.Dispose();
        operation.Dispose();
        await repository.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Check(repository.Total == 7, $"Expected one complete recovery, got {repository.Total} calls.");
        watcher.Dispose();
        error.Invoke(watcher, [null, new ErrorEventArgs(new InternalBufferOverflowException())]);
        await Task.Delay(1200);
        Check(repository.Total == 7, "Disposed watcher still dispatched refreshes.");
    }
    finally
    {
        Directory.Delete(path, true);
    }
}

static void Inject(Watcher watcher, string method, object args)
{
    typeof(Watcher).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(watcher, [null, args]);
}

static async Task Git(string path, params string[] args)
{
    using var process = new System.Diagnostics.Process { StartInfo = new("git")
    {
        WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true,
    } };
    foreach (var arg in args)
        process.StartInfo.ArgumentList.Add(arg);
    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(output, error, process.WaitForExitAsync());
    Check(process.ExitCode == 0, await error);
}

static async Task SharedWorktreeOperationLock()
{
    var root = Path.Combine(Path.GetTempPath(), $"sourcegit-shared-watch-{Guid.NewGuid():N}");
    var main = Path.Combine(root, "main");
    var other = Path.Combine(root, "other");
    var independent = Path.Combine(root, "independent");
    var common = Path.Combine(main, ".git");
    Directory.CreateDirectory(common);
    Directory.CreateDirectory(other);
    Directory.CreateDirectory(Path.Combine(independent, ".git"));
    try
    {
        var first = new RecordingRepository();
        var second = new RecordingRepository();
        var third = new RecordingRepository();
        using var a = new Watcher(first, main, common);
        using var b = new Watcher(second, other, common + Path.DirectorySeparatorChar,
            worktreeGitDir: Path.Combine(common, "worktrees", "other"));
        using var c = new Watcher(third, independent, Path.Combine(independent, ".git"));
        using var operation = a.Lock();
        using var nested = b.Lock();
        foreach (var watcher in new[] { a, b, c })
            Inject(watcher, "OnGitDirChanged", new FileSystemEventArgs(WatcherChangeTypes.Changed, common, "refs/remotes/origin/main"));
        await Task.Delay(1200);
        Check(first.Total == 0 && second.Total == 0, "Fetch refreshed a sibling worktree before completion.");
        Check(third.Total > 0, "Operation lock blocked an unrelated repository.");
        a.Dispose();
        operation.Dispose();
        operation.Dispose();
        await Task.Delay(300);
        Check(second.Total == 0, "Closing a tab or disposing a lease twice released another operation.");
        nested.Dispose();
        await Task.Delay(500);
        Check(second.Total == 3 && second.Commits == 1 && second.WorkingCopies == 0,
            "Sibling did not coalesce the fetch, or refreshed its unchanged working copy.");
        Inject(b, "OnGitDirChanged", new FileSystemEventArgs(WatcherChangeTypes.Changed, common, "worktrees/other/index"));
        await Task.Delay(1300);
        Check(second.WorkingCopies == 1, "Linked worktree's own index change was missed.");
    }
    finally
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

static async Task IgnoredBuildOutputAndRealEdits()
{
    var root = Path.Combine(Path.GetTempPath(), $"sourcegit-ignore-watch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        await Git(root, "init");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "build/\n*.tmp\n");
        Directory.CreateDirectory(Path.Combine(root, "build"));
        File.WriteAllText(Path.Combine(root, "build", "tracked.txt"), "original");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "original");
        await Git(root, "add", "-f", ".gitignore", "tracked.txt", "build/tracked.txt");
        File.WriteAllText(Path.Combine(root, "build", "árvíz file.tmp"), "ignored");
        var ignored = await IgnoredPaths.QueryAsync(root, "git",
            ["build/árvíz file.tmp", "build/removed.tmp", "build", "build/tracked.txt", "tracked.txt"], CancellationToken.None);
        Check(ignored.Contains("build/árvíz file.tmp") && ignored.Contains("build/removed.tmp"), "Ignored Unicode/deleted outputs were not filtered.");
        Check(!ignored.Contains("build") && !ignored.Contains("build/tracked.txt") && !ignored.Contains("tracked.txt"), "Tracked files or their parent directory were hidden.");
        var repository = new RecordingRepository();
        using var watcher = new Watcher(repository, root, Path.Combine(root, ".git"),
            (paths, token) => IgnoredPaths.QueryAsync(root, "git", paths, token));
        await Task.Delay(300);
        // Several bursts, separated by longer than the normal refresh debounce.
        for (var burst = 0; burst < 3; burst++)
        {
            for (var i = 0; i < 250; i++)
            {
                var file = Path.Combine(root, "build", $"output-{i}.tmp");
                File.WriteAllText(file, burst.ToString());
                File.Delete(file);
            }
            await Task.Delay(1500);
        }
        await Task.Delay(2200);
        Check(repository.Total == 0, $"Ignored build output caused {repository.Total} refreshes.");
        File.WriteAllText(Path.Combine(root, "build", "tracked.txt"), "edited");
        await Task.Delay(2700);
        Check(repository.WorkingCopies > 0 && repository.Commits == 0, "An ignored-but-tracked edit was lost or refreshed history.");
        var count = repository.WorkingCopies;
        File.Move(Path.Combine(root, "tracked.txt"), Path.Combine(root, "build", "moved.tmp"));
        await Task.Delay(2700);
        Check(repository.WorkingCopies > count, "Rename into an ignored directory lost the old tracked path.");
        count = repository.WorkingCopies;
        Inject(watcher, "OnGitDirChanged", new FileSystemEventArgs(WatcherChangeTypes.Created, Path.Combine(root, ".git"), "objects/pack/incoming.pack"));
        await Task.Delay(1300);
        Check(repository.WorkingCopies == count, "Fetching objects needlessly refreshed the working copy.");
        Directory.Delete(Path.Combine(root, "build"), true);
        await Task.Delay(2700);
        Check(repository.WorkingCopies > count, "Deleting an ignored directory hid its tracked child deletion.");
        Console.WriteLine("Build bursts: 750 ignored create/delete pairs, zero refreshes; tracked edit and rename detected.");
    }
    finally
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

static async Task WorkingCopyOverflowDoesNotRefreshHistory()
{
    var root = Path.Combine(Path.GetTempPath(), $"sourcegit-overflow-watch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(root, ".git"));
    try
    {
        var repository = new RecordingRepository();
        using var watcher = new Watcher(repository, root, Path.Combine(root, ".git"));
        Inject(watcher, "OnWorkingCopyWatcherError", new ErrorEventArgs(new InternalBufferOverflowException()));
        await Task.Delay(1400);
        Check(repository.WorkingCopies == 1 && repository.Total == 1, "Build buffer overflow rebuilt unrelated history/refs.");
    }
    finally
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

sealed class RecordingRepository : IRepository
{
    public int Total => Volatile.Read(ref _total);
    public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool MayHaveSubmodules() => false;
    public void RefreshBranches() => Record();
    public void RefreshWorktrees() => Record();
    public void RefreshTags() => Record();
    public void RefreshCommits() { Interlocked.Increment(ref _commits); Record(); }
    public void RefreshSubmodules() => Record();
    public void RefreshWorkingCopyChanges() { Interlocked.Increment(ref _workingCopies); Record(); }
    public void RefreshStashes() => Record();
    private void Record()
    {
        if (Interlocked.Increment(ref _total) == 7)
            Recovered.TrySetResult();
    }
    public int Commits => Volatile.Read(ref _commits);
    public int WorkingCopies => Volatile.Read(ref _workingCopies);
    private int _commits;
    private int _workingCopies;
    private int _total;
}
