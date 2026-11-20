using System;
using System.IO;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class StashesPage : UserControl
    {
        public static readonly StyledProperty<bool> IsEmbeddedProperty =
            AvaloniaProperty.Register<StashesPage, bool>(nameof(IsEmbedded));

        public bool IsEmbedded
        {
            get => GetValue(IsEmbeddedProperty);
            set => SetValue(IsEmbeddedProperty, value);
        }

        public StashesPage()
        {
            InitializeComponent();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (!IsEmbedded || Content is not Grid grid)
                return;

            grid.ColumnDefinitions[0].ClearValue(ColumnDefinition.WidthProperty);
            grid.ColumnDefinitions[0].MinWidth = 0;
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].MinWidth = 0;
            grid.ColumnDefinitions[2].Width = new GridLength(2, GridUnitType.Star);
            for (var i = 0; i < 3; i++)
                StashListAndChanges.RowDefinitions[i].Height = new GridLength(0);
        }

        private void OnMainLayoutSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsEmbedded || sender is not Grid grid)
                return;

            var layout = ViewModels.Preferences.Instance.Layout;
            var width = grid.Bounds.Width;
            var leftWidth = Math.Max(width - 304, 300);

            if (layout.StashesLeftWidth.Value - leftWidth > 1.0)
                layout.StashesLeftWidth = new GridLength(leftWidth, GridUnitType.Pixel);
        }

        private async void OnStashListKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is ViewModels.StashesPage { SelectedStash: { } stash } vm)
            {
                if (e.Key is Key.Delete or Key.Back)
                {
                    vm.Drop(stash);
                    e.Handled = true;
                }
                else if (e.Key is Key.C && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
                {
                    await this.CopyTextAsync(stash.Message);
                    e.Handled = true;
                }
            }
        }

        private void OnStashContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is ViewModels.StashesPage page && sender is Border { DataContext: Models.Stash stash } row)
                StashContextMenu.Create(row, page, stash, showShortcuts: true).Open(row);

            e.Handled = true;
        }

        private void OnStashDoubleTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.StashesPage vm &&
                sender is Border { DataContext: Models.Stash stash })
                vm.Apply(stash);

            e.Handled = true;
        }

        private void OnChangeContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is ViewModels.StashesPage { ChangeSelection: { Count: > 0 } selection } vm)
            {
                var selectedSingleFolder = selection.IsSingleFolder;
                var fullPathOfFolder = selectedSingleFolder ? vm.GetAbsPath(selection.SingleFolderPath) : null;
                var relativePathOfFolder = selectedSingleFolder ? selection.SingleFolderPath : null;

                var menu = new ContextMenu();
                if (selection.Count == 1)
                {
                    var change = selection.Changes[0];
                    var changeFullPath = vm.GetAbsPath(change.Path);

                    if (!selection.HasFolder)
                    {
                        var openWithMerger = new MenuItem();
                        openWithMerger.Header = App.Text("OpenInExternalMergeTool");
                        openWithMerger.Icon = this.CreateMenuIcon("Icons.OpenWith");
                        openWithMerger.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+D" : "Ctrl+Shift+D";
                        openWithMerger.Click += (_, ev) =>
                        {
                            vm.OpenChangeWithExternalDiffTool(change);
                            ev.Handled = true;
                        };
                        menu.Items.Add(openWithMerger);
                    }

                    if (!selection.HasFolder || selectedSingleFolder)
                    {
                        var explore = new MenuItem();
                        explore.Header = App.Text("RevealFile");
                        explore.Icon = this.CreateMenuIcon("Icons.Explore");
                        explore.IsEnabled = selectedSingleFolder ? Directory.Exists(fullPathOfFolder) : File.Exists(changeFullPath);
                        explore.Click += (_, ev) =>
                        {
                            Native.OS.OpenInFileManager(selectedSingleFolder ? fullPathOfFolder : changeFullPath);
                            ev.Handled = true;
                        };
                        menu.Items.Add(explore);
                    }

                    if (menu.Items.Count > 0)
                        menu.Items.Add(new MenuItem { Header = "-" });
                }

                var applyChanges = new MenuItem();
                applyChanges.Header = App.Text("StashCM.ApplyFileChanges");
                applyChanges.Icon = this.CreateMenuIcon("Icons.Diff");
                applyChanges.Click += async (_, ev) =>
                {
                    await vm.ApplySelectedChanges(selection.Changes);
                    ev.Handled = true;
                };

                var checkoutFiles = new MenuItem();
                checkoutFiles.Header = App.ContextMenuText("ChangeCM.CheckoutThisRevision");
                checkoutFiles.Icon = this.CreateMenuIcon("Icons.File.Checkout");
                checkoutFiles.Click += async (_, ev) =>
                {
                    await vm.CheckoutFilesAsync(selection.Changes);
                    ev.Handled = true;
                };

                var copyPath = new MenuItem();
                copyPath.Header = App.Text("CopyPath");
                copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
                copyPath.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
                copyPath.Click += async (_, ev) =>
                {
                    if (selectedSingleFolder)
                    {
                        await this.CopyPathAsync(relativePathOfFolder);
                    }
                    else if (selection.Changes.Count == 1)
                    {
                        await this.CopyPathAsync(selection.Changes[0].Path);
                    }
                    else
                    {
                        var builder = new StringBuilder();
                        foreach (var c in selection.Changes)
                            builder.AppendLine(c.Path);

                        await this.CopyPathAsync(builder.ToString());
                    }

                    ev.Handled = true;
                };

                var copyFullPath = new MenuItem();
                copyFullPath.Header = App.Text("CopyFullPath");
                copyFullPath.Icon = this.CreateMenuIcon("Icons.Copy");
                copyFullPath.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+C" : "Ctrl+Shift+C";
                copyFullPath.Click += async (_, ev) =>
                {
                    if (selectedSingleFolder)
                    {
                        await this.CopyPathAsync(fullPathOfFolder);
                    }
                    else if (selection.Changes.Count == 1)
                    {
                        await this.CopyPathAsync(vm.GetAbsPath(selection.Changes[0].Path));
                    }
                    else
                    {
                        var builder = new StringBuilder();
                        foreach (var c in selection.Changes)
                            builder.AppendLine(vm.GetAbsPath(c.Path));

                        await this.CopyPathAsync(builder.ToString());
                    }

                    ev.Handled = true;
                };

                menu.Items.Add(applyChanges);
                menu.Items.Add(checkoutFiles);
                menu.Items.Add(new MenuItem { Header = "-" });
                menu.Items.Add(copyPath);
                menu.Items.Add(copyFullPath);
                if (!selectedSingleFolder)
                    menu.Items.Add(this.CreateCopyFileNameMenuItem(selection.Changes));
                menu.Open(sender as Control);
            }

            e.Handled = true;
        }

        private async void OnChangeCollectionViewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.StashesPage { ChangeSelection: { Count: > 0 } selection } vm)
                return;

            if (e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && e.Key == Key.C)
            {
                var builder = new StringBuilder();
                var copyAbsPath = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (selection.IsSingleFolder)
                {
                    builder.Append(copyAbsPath ? vm.GetAbsPath(selection.SingleFolderPath) : selection.SingleFolderPath);
                }
                else if (selection.Changes.Count == 1)
                {
                    builder.Append(copyAbsPath ? vm.GetAbsPath(selection.Changes[0].Path) : selection.Changes[0].Path);
                }
                else
                {
                    foreach (var c in selection.Changes)
                        builder.AppendLine(copyAbsPath ? vm.GetAbsPath(c.Path) : c.Path);
                }

                await this.CopyPathAsync(builder.ToString());
                e.Handled = true;
            }
        }
    }
}
