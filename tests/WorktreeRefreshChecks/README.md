# Worktree refresh responsiveness checks

Run with .NET 10 and Git installed:

```powershell
dotnet run --project tests/WorktreeRefreshChecks -p:UsedAvaloniaProducts=
```

The test creates an owned temporary bare remote with 20,000 commits, a clone,
and four linked worktrees. It opens the actual launcher tab bar and repository
pages with Avalonia Headless rendering, first with one tab and then with five.
Every tab is visited before a real local fetch updates their shared remote ref.

During fetch and refresh, a worker posts input-priority probes every 25 ms and
switches tabs every 250 ms. The test reports end-to-end duration, maximum and
95th-percentile input dispatch latency, and tab-switch count. It requires every
worktree to display the fetched commit, retain its previously selected commit,
and keep the maximum input delay below one second. The time limit is a
regression guard for long UI freezes, not a production performance guarantee.

The fixture paths are printed for provenance and removed after the run. No
existing repository, remote server, or user preference file is modified.
