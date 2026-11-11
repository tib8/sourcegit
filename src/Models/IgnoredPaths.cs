using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    internal static class IgnoredPaths
    {
        // Batch paths on stdin: no command-line limit, quoting ambiguity, or Git process per event.
        // Without --no-index, check-ignore deliberately keeps tracked files in ignored directories.
        internal static async Task<HashSet<string>> QueryAsync(string root, string git, string[] paths, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(git, "--no-optional-locks check-ignore --stdin -z")
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.Start();
            using var registration = cancellationToken.Register(() =>
            {
                try { process.Kill(true); }
                catch (InvalidOperationException) { }
            });
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(string.Join('\0', paths).AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\0".AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            if (process.ExitCode != 0 && process.ExitCode != 1)
                throw new InvalidOperationException("Git ignore query failed: " + await error.ConfigureAwait(false));
            var ignored = new HashSet<string>((await output.ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var possibleDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in ignored)
            {
                if (!File.Exists(Path.Combine(root, path)))
                    possibleDirectories.Add(path.TrimEnd('/'));
            }
            if (possibleDirectories.Count > 0)
            {
                // check-ignore can ignore a directory even when it contains force-added files.
                // Keep deletion/rename/directory events if any tracked descendant is affected.
                using var tracked = new Process { StartInfo = new ProcessStartInfo(git, "--no-optional-locks ls-files --cached -z")
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                } };
                tracked.Start();
                using var trackedRegistration = cancellationToken.Register(() =>
                {
                    try { tracked.Kill(true); }
                    catch (InvalidOperationException) { }
                });
                var trackedOutput = tracked.StandardOutput.ReadToEndAsync(cancellationToken);
                var trackedError = tracked.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(trackedOutput, trackedError, tracked.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
                if (tracked.ExitCode != 0)
                    throw new InvalidOperationException("Cannot check tracked descendants.");
                foreach (var path in (await trackedOutput.ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parent = path;
                    while (true)
                    {
                        if (possibleDirectories.Contains(parent))
                            ignored.Remove(parent);
                        var slash = parent.LastIndexOf('/');
                        if (slash < 0)
                            break;
                        parent = parent.Substring(0, slash);
                    }
                }
            }
            return ignored;
        }
    }
}
