# Repository bookmark branch color checks

Run on Windows with .NET 10:

```sh
dotnet run --project tests/BookmarkBranchColorChecks -p:UsedAvaloniaProducts=
```

These standalone checks cover every bookmark color and graph highlighting mode,
an older or detached HEAD, root commits, merged branches, uncolored bookmarks,
custom graph palettes and line thickness, multiple repository colors, and live
bookmark changes for managed and unmanaged tabs. They use isolated preferences
and leave the user's repositories and settings untouched.

The default graph and bookmarks share Paul Tol's six chromatic Bright colours.
Legacy numeric bookmark indices (including the expanded palette) migrate to the
nearest colour by normal-vision OKLab distance. New selections persist as IDs
101–106, preventing a second conversion on reload. Tests exercise generated JSON
serialization for nested preferences and unmanaged repository metadata, metadata
file round trips, and theme-aware neutral icons and coloured outlines.
