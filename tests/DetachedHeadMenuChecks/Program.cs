using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SourceGit;
using SourceGit.ViewModels;

internal static class Program
{


    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-detached-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = root;
        SourceGit.Native.OS.BasicDirectories.CacheDir = root;
        Console.WriteLine("Fixture: " + root);
        try
        {
            App.BuildAvaloniaApp().SetupWithoutStarting();
            App.SetLocale("en_US");
            var main = Path.Combine(root, "main");
            var detached = Path.Combine(root, "detached");
            Directory.CreateDirectory(main);
            Git(main, "init", "-b", "main");
            Git(main, "config", "user.name", "Test");
            Git(main, "config", "user.email", "test@example.invalid");
            Git(main, "config", "commit.gpgsign", "false");
            Git(main, "config", "core.hooksPath", Path.Combine(root, "disabled-hooks"));
            Git(main, "commit", "--allow-empty", "-m", "initial");
            Git(main, "branch", "topic");
            Git(main, "commit", "--allow-empty", "-m", "second");
            Git(main, "remote", "add", "origin", main);
            Git(main, "update-ref", "refs/remotes/origin/topic", "topic");
            Git(main, "worktree", "add", "--detach", detached, "HEAD");
            var headBefore = Git(detached, "rev-parse", "HEAD");
            var refsBefore = Git(main, "show-ref");
            var repo = Load(detached);
            Console.WriteLine("Loaded current branch: " + (repo.CurrentBranch?.Name ?? "<null>"));

            // Exercise the exact menu builder used by the sidebar right-click handler.
            if (args.Contains("--reproduce"))
            {
                try
                {
                    LocalMenu(repo, "topic");
                    throw new Exception("The original crash was not reproduced");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException)
                {
                    Console.WriteLine("REPRODUCED: " + ex.InnerException);
                    return 0;
                }
            }

            CheckDetached(repo, "new detached worktree");
            Git(detached, "checkout", "topic");
            var attached = Load(detached);
            Check(attached.CurrentBranch is { Name: "topic", IsDetachedHead: false }, "checkout restores an attached current branch");
            CheckMenu(attached, "main", true);
            CheckMenu(attached, "topic", false);

            Git(detached, "checkout", "--detach", "main");
            CheckDetached(Load(detached), "checkout --detach");
            Git(main, "checkout", "--detach", "HEAD");
            CheckDetached(Load(main), "detached main working directory");

            // A menu can also be requested before the first branch refresh finishes.
            var noCurrent = Load(detached);
            typeof(Repository).GetProperty(nameof(Repository.CurrentBranch))!.SetValue(noCurrent, null);
            CheckMenu(noCurrent, "topic", false);
            Check(Git(detached, "rev-parse", "HEAD") == headBefore, "menu construction preserves detached HEAD");
            Check(Git(main, "show-ref") == refsBefore, "menu construction preserves every branch ref");
            Console.WriteLine($"PASS: {_checks} detached HEAD menu checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture must stay inside the temporary directory");
            foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(absolute, true);
        }
    }

    private static Repository Load(string path)
    {
        var repo = new Repository(false, path, Git(path, "rev-parse", "--absolute-git-dir").Trim());
        repo.Branches.AddRange(Complete(new SourceGit.Commands.QueryBranches(path).GetResultAsync()));
        typeof(Repository).GetProperty(nameof(Repository.CurrentBranch))!.SetValue(repo, repo.Branches.Find(x => x.IsCurrent));
        return repo;
    }

    private static void CheckDetached(Repository repo, string label)
    {
        Check(repo.CurrentBranch is { IsCurrent: true, IsDetachedHead: true, Name: "HEAD" }, label + " recognizes HEAD");
        Check(repo.CurrentBranch.Head == Git(repo.FullPath, "rev-parse", "HEAD").Trim(), label + " preserves the actual HEAD commit");
        Check(repo.Branches.Count(x => x.IsCurrent) == 1, label + " has exactly one current entry");
        CheckMenu(repo, "topic", true);
        CheckMenu(repo, "main", true);
        var remote = repo.Branches.Single(x => x.FullName == "refs/remotes/origin/topic");
        var menu = new SourceGit.Views.BranchTree().CreateContextMenuForRemoteBranch(repo, remote);
        Check(menu.Items.Count > 0, label + " opens the remote branch menu");
    }

    private static ContextMenu LocalMenu(Repository repo, string name)
    {
        var tree = new SourceGit.Views.BranchTree();
        var branch = repo.Branches.Single(x => x.IsLocal && x.Name == name);
        return (ContextMenu)typeof(SourceGit.Views.BranchTree)
            .GetMethod("CreateContextMenuForLocalBranch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(tree, [repo, branch])!;
    }

    private static void CheckMenu(Repository repo, string name, bool hasMerge)
    {
        var menu = LocalMenu(repo, name);
        var headers = menu.Items.OfType<MenuItem>().Select(x => x.Header).OfType<string>().ToArray();
        Check(headers.Contains(App.Text("BranchCM.CopyName")), name + " menu includes copy name");
        Check(headers.Contains(App.Text("BranchCM.Merge", name, repo.CurrentBranch?.Name)) == hasMerge,
            name + " merge availability matches current HEAD");
        Check(headers.Contains(App.Text("BranchCM.CompareWithHead")) == (repo.CurrentBranch != null && repo.CurrentBranch.Name != name),
            name + " compare with HEAD requires a known current commit");
    }

    private static T Complete<T>(Task<T> task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (timer.Elapsed.TotalSeconds > 15)
                throw new TimeoutException("Timed out loading fixture branches");
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        _checks++;
    }

    private static string Git(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out running fixture Git command");
        }
        if (process.ExitCode != 0)
            throw new Exception(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}
