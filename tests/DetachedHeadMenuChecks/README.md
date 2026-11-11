# Detached HEAD branch menu checks

Run from the repository root:

```powershell
dotnet run --project tests/DetachedHeadMenuChecks
```

The runner creates its own temporary Git repository, local and remote-tracking
branches, and a newly detached linked worktree. It loads branches through the
production Git query and invokes the actual sidebar context-menu builders using
Avalonia. Application settings and cache are isolated in the fixture directory;
all fixture files are removed on exit.

Coverage includes a newly detached worktree, attached checkout, checkout back to
detached HEAD, a detached main working directory, local and remote branch menus,
and menu construction before a current branch is available. It checks the current
commit and verifies that menu construction does not move HEAD or branch refs.

## Original failure

On the unmodified application sources at `2b1277ac`, running this harness with
`-- --reproduce` successfully reproduced:

```text
Loaded current branch: <null>
System.NullReferenceException
  at SourceGit.Views.BranchTree.CreateContextMenuForLocalBranch(...):line 1258
```

Git reports a newly detached worktree as `(no branch)`, whereas the parser only
recognized `(HEAD detached at ...)` and `(HEAD detached from ...)`. The local
branch menu then dereferenced the missing current branch. The fix recognizes the
additional Git representation and guards menu actions requiring a current HEAD.

The `--reproduce` mode is only for the original application sources; the default
mode verifies the corrected behavior.
