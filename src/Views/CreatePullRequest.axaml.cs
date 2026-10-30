using System;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class CreatePullRequest : ChromelessWindow
    {
        public CreatePullRequest()
        {
            InitializeComponent();
            CloseOnESC = true;
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.CreatePullRequest model)
                await model.InitializeAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            (DataContext as ViewModels.CreatePullRequest)?.Dispose();
            base.OnClosed(e);
        }

        private async void OnSignIn(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SignInAsync(); }
        private async void OnSignOut(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SignOutAsync(); }
        private async void OnCreate(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SubmitAsync(); }
        private async void OnRetryLoading(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.RetryLoadingAsync(); }
        private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_orderingTargets && sender is ComboBox { SelectedItem: AzureDevOps.GitRef target } && DataContext is ViewModels.CreatePullRequest { CanEdit: true } model)
                model.Target = target;
        }

        private void OnTargetDropDownOpened(object sender, EventArgs e)
        {
            if (DataContext is not ViewModels.CreatePullRequest model)
                return;
            _orderingTargets = true;
            try
            {
                model.RefreshTargetOrder();
                TargetBranch.SetCurrentValue(ComboBox.SelectedItemProperty, model.Target);
            }
            finally { _orderingTargets = false; }
        }

        private void OnAddReviewer(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: AzureDevOps.Identity identity } && DataContext is ViewModels.CreatePullRequest model)
            {
                model.AddReviewer(identity);
                ReviewerSearchBox.Focus();
            }
        }
        private void OnRemoveReviewer(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ViewModels.PullRequestReviewer reviewer })
                (DataContext as ViewModels.CreatePullRequest)?.RemoveReviewer(reviewer);
        }
        private void OnOpenResult(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.OpenResult(); }
        private void OnOpenExisting(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.OpenExistingPullRequest(); }
        private void OnOpenRepository(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.OpenRepository(); }
        private void OnClearRecovery(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.ClearRecovery(); }
        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
        private bool _orderingTargets;
    }
}
