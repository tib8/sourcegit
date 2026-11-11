using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using SourceGit;
using SourceGit.ViewModels;
using Views = SourceGit.Views;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-worktree-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Owned test fixture: " + root);
        SourceGit.Native.OS.GitExecutable = "git";
        SourceGit.Native.OS.BasicDirectories.ConfigDir = root;
        SourceGit.Native.OS.BasicDirectories.CacheDir = root;
        typeof(Preferences).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Preferences());
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .ConfigureFonts(manager => manager.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(
                new Uri("fonts:SourceGit"), new Uri("avares://SourceGit/Resources/Fonts"))))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        try
        {
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(remote);
            Git(remote, null, "init", "--bare");
            var history = new StringBuilder();
            for (var i = 1; i <= 20000; i++)
            {
                history.Append($"commit refs/heads/main\nmark :{i}\ncommitter Test <test@example.com> {1700000000 + i} +0000\ndata 12\nCommit {i:D5}\n");
                if (i > 1)
                    history.Append($"from :{i - 1}\n");
                history.Append('\n');
            }
            history.Append("done\n");
            Git(remote, history.ToString(), "fast-import", "--quiet");
            Git(remote, null, "symbolic-ref", "HEAD", "refs/heads/main");
            var client = Path.Combine(root, "client");
            Git(root, null, "clone", "--quiet", remote, client);
            var worktrees = new List<string> { client };
            for (var i = 1; i < 5; i++)
            {
                var worktree = Path.Combine(root, "worktree-" + i);
                Git(client, null, "worktree", "add", "--quiet", "-b", "worktree-" + i, worktree, "HEAD~" + i);
                worktrees.Add(worktree);
            }
            RunScenario(remote, worktrees.Take(1).ToList());
            RunScenario(remote, worktrees);
            Console.WriteLine("PASS: actual local fetch with one and five open worktrees, 20,000 commits per tab, input dispatch and tab switching.");
        }
        finally
        {
            typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Application.Current, null);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, true);
                    break;
                }
                catch (IOException) when (attempt < 30) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (attempt < 30) { Thread.Sleep(100); }
            }
        }
    }

    private static void RunScenario(string remote, List<string> worktrees)
    {
        Preferences.Instance.EnableAutoFetch = false;
        Preferences.Instance.MaxHistoryCommits = 20000;
        Preferences.Instance.Workspaces.Clear();
        Preferences.Instance.Workspaces.Add(new Workspace { Name = "Refresh test", IsActive = true, Repositories = new(worktrees) });
        var launcher = new Launcher(string.Empty);
        typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Application.Current, launcher);
        var pages = launcher.Pages.Where(p => p.Data is Repository).ToArray();
        Check(pages.Length == worktrees.Count, "Not all worktrees opened in the launcher.");
        var repositories = pages.Select(p => (Repository)p.Data).ToArray();
        foreach (var repository in repositories)
        {
            repository.UIStates.IsBranchesColumnVisibleInHistory = true;
            repository.UIStates.IsTreeColumnVisibleInHistory = true;
        }
        var body = new ContentControl();
        body.DataTemplates.Add(new FuncDataTemplate<LauncherPage>((page, _) => new Views.LauncherPage { DataContext = page }));
        body.Bind(ContentControl.ContentProperty, new Binding(nameof(Launcher.ActivePage)));
        var layout = new Grid { RowDefinitions = new("30,*"), DataContext = launcher };
        layout.Children.Add(new Views.LauncherTabBar());
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        var window = new Window { Width = 1600, Height = 1000, Content = layout };
        window.Show();
        using var pulses = new CancellationTokenSource();
        Task pulseTask = Task.CompletedTask;
        var measuring = false;
        try
        {
            PumpUntil(window, () => repositories.All(r => !r.IsRefreshing && r.Histories.Graph != null && r.Histories.Commits.Count == 20000));
            var selected = repositories.Select(r => r.Histories.Commits[5000].SHA).ToArray();
            for (var i = 0; i < repositories.Length; i++)
                repositories[i].Histories.SelectedCommits = [repositories[i].Histories.Commits[5000]];
            // Visit every tab before fetching, as in a session with previously opened worktrees.
            foreach (var page in pages)
            {
                launcher.ActivePage = page;
                PumpFor(window, 250);
            }
            launcher.ActivePage = pages[0];
            PumpFor(window, 250);
            var previousHead = Git(remote, null, "rev-parse", "main").Trim();
            var message = "fetch " + Guid.NewGuid().ToString("N");
            Git(remote, $"commit refs/heads/main\ncommitter Test <test@example.com> 1800000000 +0000\ndata {message.Length}\n{message}\nfrom {previousHead}\n\ndone\n", "fast-import", "--quiet");
            var newHead = Git(remote, null, "rev-parse", "main").Trim();
            var delays = new List<double>();
            var switched = 0;
            measuring = true;
            pulseTask = Task.Run(async () =>
            {
                var ticks = 0;
                while (!pulses.IsCancellationRequested)
                {
                    await Task.Delay(25).ConfigureAwait(false);
                    var posted = Stopwatch.GetTimestamp();
                    var switchTab = ++ticks % 10 == 0;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!measuring)
                            return;
                        delays.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                        if (switchTab)
                        {
                            launcher.ActivePage = pages[++switched % pages.Length];
                            Check(ReferenceEquals(body.Content, launcher.ActivePage), "Tab switch did not reach the launcher view.");
                        }
                    }, DispatcherPriority.Input);
                }
            });
            var timer = Stopwatch.StartNew();
            pages[0].Popup = new Fetch(repositories[0]);
            var fetch = pages[0].ProcessPopupAsync();
            PumpUntil(window, () => fetch.IsCompleted && repositories.All(r => !r.IsRefreshing && r.Histories.Graph != null && r.Histories.Commits.Any(c => c.SHA == newHead)));
            fetch.GetAwaiter().GetResult();
            PumpFor(window, 300);
            measuring = false;
            var maximum = delays.DefaultIfEmpty(double.PositiveInfinity).Max();
            var ordered = delays.Order().ToArray();
            var p95 = ordered.Length == 0 ? double.PositiveInfinity : ordered[(int)((ordered.Length - 1) * .95)];
            Console.WriteLine($"{pages.Length} worktree(s): fetch + refresh {timer.ElapsedMilliseconds} ms; input max {maximum:F1} ms, p95 {p95:F1} ms; tab switches {switched}; histories {string.Join(",", repositories.Select(r => r.Histories.Commits.Count))}");
            Check(switched > 0, "No tab interaction ran during the measured operation.");
            Check(maximum < 1000, "UI input was blocked for at least one second during fetch/refresh.");
            Check(repositories.All(r => r.Histories.Commits.Any(c => c.SHA == newHead)), "A worktree did not receive the fetched commit.");
            for (var i = 0; i < repositories.Length; i++)
                Check(repositories[i].Histories.SelectedCommits is { Count: 1 } current && current[0].SHA == selected[i],
                    $"Fetch or tab switching changed the selected commit in worktree {i}.");
        }
        finally
        {
            measuring = false;
            pulses.Cancel();
            pulseTask.GetAwaiter().GetResult();
            foreach (var repository in repositories)
                repository.Close();
            window.Close();
            PumpFor(window, 200);
        }
    }

    private static void PumpUntil(Window window, Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Pump(window);
            if (timer.Elapsed > TimeSpan.FromSeconds(60))
                throw new Exception("Timed out waiting for worktree refresh.");
        }
        Pump(window);
    }

    private static void PumpFor(Window window, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
            Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(5);
    }

    private static string Git(string root, string input, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = input == null ? null : new UTF8Encoding(false),
        } };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        process.WaitForExit();
        Check(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
