using System;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    internal static class StashContextMenu
    {
        public static ContextMenu Create(Control host, ViewModels.StashesPage page, Models.Stash stash, bool showShortcuts = false)
        {
            void RunOnCurrentStash(Action<Models.Stash> action)
            {
                var current = page.Stashes.Find(entry => entry.Name == stash.Name && entry.SHA == stash.SHA);
                if (current != null)
                    action(current);
            }

            var apply = new MenuItem();
            apply.Header = App.ContextMenuText("StashCM.Apply");
            apply.Icon = host.CreateMenuIcon("Icons.CheckCircled");
            apply.Click += (_, ev) =>
            {
                RunOnCurrentStash(page.Apply);
                ev.Handled = true;
            };

            var branch = new MenuItem();
            branch.Header = App.Text("StashCM.Branch");
            branch.Icon = host.CreateMenuIcon("Icons.Branch.Add");
            branch.Click += (_, ev) =>
            {
                RunOnCurrentStash(page.CheckoutBranch);
                ev.Handled = true;
            };

            var drop = new MenuItem();
            drop.Header = App.ContextMenuText("StashCM.Drop");
            drop.Icon = host.CreateMenuIcon("Icons.Clear");
            if (showShortcuts)
                drop.Tag = "Back/Delete";
            drop.Click += (_, ev) =>
            {
                RunOnCurrentStash(page.Drop);
                ev.Handled = true;
            };

            var patch = new MenuItem();
            patch.Header = App.Text("StashCM.SaveAsPatch");
            patch.Icon = host.CreateMenuIcon("Icons.Save");
            patch.Click += async (_, ev) =>
            {
                var storageProvider = TopLevel.GetTopLevel(host)?.StorageProvider;
                if (storageProvider == null)
                    return;

                var options = new FilePickerSaveOptions();
                options.Title = App.Text("StashCM.SaveAsPatch");
                options.DefaultExtension = ".patch";
                options.FileTypeChoices = [new FilePickerFileType("Patch File") { Patterns = ["*.patch"] }];

                try
                {
                    var storageFile = await storageProvider.SaveFilePickerAsync(options);
                    if (storageFile != null)
                        await page.SaveStashAsPatchAsync(stash, storageFile.Path.LocalPath);
                }
                catch (Exception exception)
                {
                    Models.Notification.Send(null, $"Failed to save as patch: {exception.Message}", true);
                }

                ev.Handled = true;
            };

            var copy = new MenuItem();
            copy.Header = App.Text("StashCM.CopyMessage");
            copy.Icon = host.CreateMenuIcon("Icons.Copy");
            if (showShortcuts)
                copy.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
            copy.Click += async (_, ev) =>
            {
                await host.CopyTextAsync(stash.Message);
                ev.Handled = true;
            };

            var menu = new ContextMenu();
            menu.Items.Add(apply);
            menu.Items.Add(branch);
            menu.Items.Add(drop);
            menu.Items.Add(new MenuItem { Header = "-" });
            menu.Items.Add(patch);
            menu.Items.Add(new MenuItem { Header = "-" });
            menu.Items.Add(copy);
            return menu;
        }
    }
}
