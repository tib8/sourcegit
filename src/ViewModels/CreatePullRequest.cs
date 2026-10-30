using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Identity.Client;
using SourceGit.AzureDevOps;

namespace SourceGit.ViewModels
{
    public sealed class PullRequestReviewer : ObservableObject
    {
        public PullRequestReviewer(Action changed = null) { _changed = changed; }
        public string Id { get; init; }
        public string Name { get; init; }
        public string Detail { get; init; }
        public bool IsAvailable { get; init; } = true;
        public bool IsRequired { get => _required; set { if (SetProperty(ref _required, value)) _changed?.Invoke(); } }
        private readonly Action _changed;
        private bool _required;
    }

    public sealed class CreatePullRequest : ObservableObject, IDisposable
    {
        public ObservableCollection<GitRef> Branches { get; } = [];
        public ObservableCollection<Identity> SearchResults { get; } = [];
        public ObservableCollection<PullRequestReviewer> Reviewers { get; } = [];
        public string ReviewerSearchStatus { get => _reviewerSearchStatus; private set => SetProperty(ref _reviewerSearchStatus, value); }
        public bool IsSearchingReviewers { get => _searchingReviewers; private set => SetProperty(ref _searchingReviewers, value); }
        public bool HasReviewerSearchError { get => _reviewerSearchError; private set => SetProperty(ref _reviewerSearchError, value); }
        public string SourceBranch { get; }
        public string RepositoryLabel { get => _repositoryLabel; private set => SetProperty(ref _repositoryLabel, value); }
        public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }
        public string Tenant { get => _tenant; set => SetProperty(ref _tenant, value); }
        public string[] AuthenticationMethods { get; } = [App.Text("CreatePR.MicrosoftAccount"), App.Text("CreatePR.PersonalAccessToken")];
        public int AuthenticationMethod
        {
            get => _authenticationMethod;
            set
            {
                if (IsBusy || !SetProperty(ref _authenticationMethod, value))
                    return;
                _auth?.Dispose();
                _auth = null;
                PersonalAccessToken = string.Empty;
                ResetConnection();
                SessionOnly = false;
                Error = string.Empty;
                Status = App.Text("CreatePR.ConnectPrompt");
                OnPropertyChanged(nameof(UsePersonalAccessToken));
                OnPropertyChanged(nameof(ConnectLabel));
                ConnectionExpanded = true;
            }
        }
        public bool UsePersonalAccessToken => AuthenticationMethod == 1;
        public string ConnectLabel => App.Text(UsePersonalAccessToken ? "CreatePR.ConnectToken" : "CreatePR.SignIn");
        public string PersonalAccessToken { get => _personalAccessToken; set => SetProperty(ref _personalAccessToken, value); }
        public bool ConnectionExpanded { get => _connectionExpanded; set => SetProperty(ref _connectionExpanded, value); }
        public string AccountName { get => _accountName; private set => SetProperty(ref _accountName, value); }
        public bool SessionOnly { get => _sessionOnly; private set => SetProperty(ref _sessionOnly, value); }
        public bool IsConnected { get => _connected; private set { SetProperty(ref _connected, value); NotifyState(); } }
        public bool IsSignedIn { get => _signedIn; private set { SetProperty(ref _signedIn, value); NotifyState(); } }
        public bool HasAccount => _auth?.HasCredentials == true;
        public bool CanRetryLoading => IsSignedIn && (!IsConnected || _commitLoadFailed);
        public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyState(); } }
        public bool IsLoadingCommits { get => _loadingCommits; private set { SetProperty(ref _loadingCommits, value); NotifyState(); } }
        public bool HasActivity => IsBusy || IsLoadingCommits;
        public bool CanEdit => IsConnected && !IsBusy && !IsFinished && _store?.Pending == null;
        public bool CanEditRequest => CanEdit && !HasExistingPullRequest;
        public bool IsFinished { get => _finished; private set { SetProperty(ref _finished, value); NotifyState(); } }
        public string Status { get => _status; private set { SetProperty(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
        public string Error { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsErrorStatus)); } }
        public bool HasStatus => !string.IsNullOrEmpty(Status);
        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool IsSuccessful => IsFinished && _result?.State == CreationState.Created;
        public bool IsErrorStatus => HasError && !IsSuccessful;
        public bool HasExistingPullRequest => _existingPullRequest != null;
        public string ExistingPullRequestMessage => _existingPullRequest == null ? string.Empty :
            App.Text(_existingPullRequest.IsDraft ? "CreatePR.ExistingDraftPair" : "CreatePR.ExistingPair", _existingPullRequest.PullRequestId);
        public string ExistingPullRequestTitle => _existingPullRequest?.Title ?? string.Empty;
        public bool HasResultLink => _result?.PullRequest?.PullRequestId > 0;
        public bool HasPending => _store?.Pending != null;
        public bool HasUnknown => _store?.Pending is { PullRequestId: 0 };
        public bool RecoveryAcknowledged { get => _recoveryAcknowledged; set { SetProperty(ref _recoveryAcknowledged, value); NotifyState(); } }
        public bool CanClearRecovery => !IsBusy && HasPending && RecoveryAcknowledged;
        public string PrimaryLabel => HasPending ? App.Text(_store.Pending.PullRequestId > 0 ? "CreatePR.RetrySetup" : "CreatePR.CheckCreation") : App.Text(IsDraft ? "CreatePR.CreateDraft" : "CreatePR.Create");
        public bool CanCreate => IsConnected && !HasActivity && !IsFinished && (HasPending || (!HasExistingPullRequest && !_commitLoadFailed && Target != null && !string.IsNullOrWhiteSpace(Title) && Reviewers.All(x => x.IsAvailable)));
        public bool AutoCompleteAvailable => !IsDraft;
        public bool IsSquash => MergeStrategy == 0;
        public string[] MergeStrategies { get; } = [App.Text("CreatePR.Squash"), App.Text("CreatePR.Rebase"), App.Text("CreatePR.Merge")];
        public string MergeHelp => App.Text("CreatePR.Help." + (MergeStrategy == 0 ? "Squash" : MergeStrategy == 1 ? "Rebase" : "Merge"));
        public string AutoCompleteHelp => App.Text(IsDraft ? "CreatePR.Help.Draft" : AutoComplete ? "CreatePR.Help.Auto" : "CreatePR.Help.Manual");
        public bool CompletionExpanded { get => _completionExpanded; set => SetProperty(ref _completionExpanded, value); }
        public string CompletionSummary => string.Join(" · ",
            App.Text(IsDraft ? "CreatePR.Summary.Draft" : AutoComplete ? "CreatePR.Summary.Auto" : "CreatePR.Summary.Manual"),
            MergeStrategies[Math.Clamp(MergeStrategy, 0, MergeStrategies.Length - 1)],
            App.Text(DeleteSourceBranch ? "CreatePR.Summary.Delete" : "CreatePR.Summary.Keep"));
        public GitRef Target
        {
            get => _target;
            set
            {
                if (SetProperty(ref _target, value))
                {
                    _ = LoadCommitTextAsync();
                    if (CanEdit && value != null && Branches.Contains(value))
                        RememberTarget(value.Name);
                }
                NotifyState();
            }
        }
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value))
                {
                    if (!_updatingCommitText)
                        _titleEdited = true;
                    if (!_messageEdited)
                    {
                        _mergeMessage = value;
                        OnPropertyChanged(nameof(MergeMessage));
                    }
                }
                NotifyState();
            }
        }
        public string Description { get => _description; set { if (SetProperty(ref _description, value) && !_updatingCommitText) _descriptionEdited = true; } }
        public bool IsDraft { get => _draft; set { SetProperty(ref _draft, value); OnPropertyChanged(nameof(AutoCompleteAvailable)); OnPropertyChanged(nameof(AutoCompleteHelp)); NotifyState(); } }
        public bool AutoComplete { get => _auto; set { SetProperty(ref _auto, value); OnPropertyChanged(nameof(AutoCompleteHelp)); OnPropertyChanged(nameof(CompletionSummary)); } }
        public bool DeleteSourceBranch { get => _delete; set { SetProperty(ref _delete, value); OnPropertyChanged(nameof(CompletionSummary)); } }
        public int MergeStrategy { get => _mergeStrategy; set { SetProperty(ref _mergeStrategy, value); OnPropertyChanged(nameof(IsSquash)); OnPropertyChanged(nameof(MergeHelp)); OnPropertyChanged(nameof(CompletionSummary)); } }
        public string MergeMessage { get => _mergeMessage; set { _messageEdited = true; SetProperty(ref _mergeMessage, value); } }
        public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) _ = SearchAsync(value); } }

        public CreatePullRequest(Repository repository, Models.Remote remote, Models.Branch branch, Models.Branch localBranch = null,
            HttpClient http = null, Func<ConnectionSettings, string, Task<IAuthentication>> authenticationFactory = null,
            Func<string, string, Task<PersonalAccessTokenAuthentication>> personalAccessTokenFactory = null,
            Func<CancellationToken, Task<Commands.PullRequestBranchOrigin>> branchOriginReader = null)
        {
            _repo = repository;
            _remote = remote;
            _localBranch = localBranch;
            SourceBranch = branch.Name;
            _sourceRef = "refs/heads/" + branch.Name;
            _title = string.Empty;
            _mergeMessage = _title;
            RemoteAddress.TryParse(remote.URL, out _address);
            RepositoryLabel = _address == null ? remote.Name : $"{_address.Organization} / {_address.Project} / {_address.Repository}";
            var settings = Preferences.Instance.AzureDevOpsConnection ?? new ConnectionSettings();
            _clientId = settings.ClientId;
            _tenant = settings.Tenant;
            _authenticationMethod = settings.UsePersonalAccessToken ? 1 : 0;
            _connectionExpanded = UsePersonalAccessToken || string.IsNullOrWhiteSpace(_clientId);
            _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            _authenticationFactory = authenticationFactory ?? (async (settings, directory) => await Authentication.CreateAsync(settings, directory));
            _personalAccessTokenFactory = personalAccessTokenFactory ?? ((organization, token) => PersonalAccessTokenAuthentication.CreateAsync(organization, token, Native.OS.Keychain));
            _branchOriginReader = branchOriginReader ?? (token => _localBranch == null
                ? Task.FromResult<Commands.PullRequestBranchOrigin>(null)
                : new Commands.ReadPullRequestBranchOrigin(_repo.FullPath, _localBranch.FullName).GetOriginAsync(token));
            Status = App.Text("CreatePR.ConnectPrompt");
        }

        public Task InitializeAsync() => !UsePersonalAccessToken && string.IsNullOrWhiteSpace(ClientId) ? Task.CompletedTask : ConnectAsync(false);
        public Task SignInAsync() => ConnectAsync(true);

        private void ResetConnection()
        {
            IsConnected = false;
            IsSignedIn = false;
            AccountName = string.Empty;
            _result = null;
            _existingPullRequest = null;
            IsFinished = false;
            _store = null;
            _preferenceKey = null;
            _recentTargetRefs.Clear();
            Reviewers.Clear();
            Branches.Clear();
            SearchResults.Clear();
            ReviewerSearchStatus = string.Empty;
            HasReviewerSearchError = false;
            IsSearchingReviewers = false;
            _searchCancellation?.Cancel();
            _commitCancellation?.Cancel();
            Target = null;
            _client = null;
            _service = null;
            NotifyState();
        }

        private Task ConnectAsync(bool interactive) => RunAsync(async () =>
        {
            ResetConnection();
            Status = App.Text(UsePersonalAccessToken ? "CreatePR.ConnectingToken" : interactive ? "CreatePR.SigningIn" : "CreatePR.RestoringSignIn");
            if (_address == null)
                throw new ServiceException("UnsupportedRemote");
            if (!string.IsNullOrEmpty(_remote.PushURL) && (!RemoteAddress.TryParse(_remote.PushURL, out var push) || push.Key != _address.Key))
                throw new ServiceException("AmbiguousRemote");

            var settings = new ConnectionSettings { UsePersonalAccessToken = UsePersonalAccessToken, ClientId = ClientId?.Trim(), Tenant = Tenant?.Trim(), AccountId = Preferences.Instance.AzureDevOpsConnection?.AccountId };
            if (UsePersonalAccessToken)
            {
                if (_auth is not PersonalAccessTokenAuthentication || !string.IsNullOrWhiteSpace(PersonalAccessToken))
                {
                    _auth?.Dispose();
                    try
                    { _auth = await _personalAccessTokenFactory(_address.Organization, PersonalAccessToken); }
                    finally { PersonalAccessToken = string.Empty; }
                }
            }
            else
            {
                _auth?.Dispose();
                _auth = await _authenticationFactory(settings, Native.OS.BasicDirectories.ConfigDir);
                if (interactive)
                    await _auth.SignInAsync(_lifetime.Token);
            }
            await _auth.GetAuthorizationAsync(_lifetime.Token);
            _client = new Client(_http, _address, _auth.GetAuthorizationAsync);
            if (_auth is PersonalAccessTokenAuthentication tokenAuth)
                tokenAuth.SetIdentity(await _client.GetAuthenticatedUserAsync(_lifetime.Token));
            UpdateAccount();
            if (!UsePersonalAccessToken)
                settings.AccountId = _auth.AccountId;
            Preferences.Instance.AzureDevOpsConnection = settings;
            Preferences.Instance.Save();
            Status = App.Text("CreatePR.LoadingRepository");
            var info = await _client.ResolveAsync(_lifetime.Token);
            if (_auth is PersonalAccessTokenAuthentication verifiedToken)
            {
                await verifiedToken.RememberAsync();
                UpdateAccount();
            }
            RepositoryLabel = $"{_address.Organization} / {info.Project.Name} / {info.Name}";
            _preferenceKey = _client.PreferenceKey(_auth.AccountId);
            _store = new RepositoryCreationStore(_repo.Settings, _preferenceKey + "/" + _sourceRef);
            _service = new CreationService(_client, _store, ReadLocalTipAsync);
            Status = App.Text("CreatePR.LoadingBranches");
            foreach (var branch in (await _client.GetBranchesAsync(_lifetime.Token)).Where(x => x.Name != _sourceRef).OrderBy(x => x.BranchName, StringComparer.Ordinal))
                Branches.Add(branch);

            var defaults = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey) ?? new PullRequestDefaults();
            _recentTargetRefs = RecentTargets(defaults, null);
            SortBranches();
            var pending = _store.Pending;
            var inferredTarget = pending == null ? await FindOriginTargetAsync() : null;
            Target = Branches.FirstOrDefault(x => x.Name == (pending?.TargetRef ?? inferredTarget ?? defaults.TargetRef));
            AutoComplete = pending is { IsDraft: false } ? pending.AutoComplete : defaults.AutoComplete;
            DeleteSourceBranch = pending?.Completion.DeleteSourceBranch ?? defaults.DeleteSourceBranch;
            MergeStrategy = Array.IndexOf(StrategyValues, pending?.Completion.MergeStrategy ?? defaults.MergeStrategy);
            if (MergeStrategy < 0)
                MergeStrategy = 0;
            var selectedReviewers = pending?.Reviewers ?? defaults.Reviewers;
            Status = App.Text("CreatePR.LoadingReviewers");
            var restored = await _client.ResolveIdentitiesAsync(selectedReviewers.Select(x => x.Id), _lifetime.Token);
            foreach (var saved in selectedReviewers)
            {
                var identity = restored.FirstOrDefault(x => x.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));
                Reviewers.Add(new PullRequestReviewer(RememberReviewers) { Id = saved.Id, Name = identity?.Label ?? App.Text("CreatePR.UnavailableReviewer"), Detail = identity?.Detail, IsAvailable = identity != null, IsRequired = saved.IsRequired });
            }
            IsConnected = true;
            ConnectionExpanded = false;
            if (pending != null)
            {
                Target = Branches.FirstOrDefault(x => x.Name == pending.TargetRef);
                Title = pending.Title;
                IsDraft = pending.IsDraft;
                MergeMessage = pending.Completion.MergeCommitMessage;
                Status = App.Text("CreatePR.PendingNotice");
            }
            else
            {
                Status = string.Empty;
                await LoadCommitTextAsync();
                if (defaults.TargetRef.Length > 0 && Target == null)
                    Error = App.Text("CreatePR.Error.SavedTargetUnavailable");
            }
        });

        private void UpdateAccount()
        {
            IsSignedIn = true;
            AccountName = _auth.UserName;
            SessionOnly = _auth.SessionOnly;
            ConnectionExpanded = false;
        }

        private async Task<string> FindOriginTargetAsync()
        {
            var origin = await _branchOriginReader(_lifetime.Token);
            if (origin == null)
                return null;
            var start = origin.StartPoint;
            if (start != "HEAD" && !(start is { Length: 40 } && start.All(Uri.IsHexDigit)))
            {
                var names = new List<string> { start };
                foreach (var prefix in new[] { "refs/heads/", "refs/remotes/" + _remote.Name + "/", _remote.Name + "/" })
                {
                    if (start.StartsWith(prefix, StringComparison.Ordinal))
                        names.Add(start[prefix.Length..]);
                }
                var named = Branches.Where(x => names.Contains(x.BranchName, StringComparer.Ordinal)).ToList();
                return named.Count == 1 ? named[0].Name : null;
            }
            // Branches created from a commit/HEAD have no parent name. Infer only an exact, unique remote tip.
            var matching = Branches.Where(x => string.Equals(x.ObjectId, origin.CommitId, StringComparison.OrdinalIgnoreCase)).ToList();
            return matching.Count == 1 ? matching[0].Name : null;
        }

        private static List<string> RecentTargets(PullRequestDefaults defaults, string latest) =>
            new[] { latest }.Concat(defaults.RecentTargetRefs ?? []).Append(defaults.TargetRef)
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).Take(3).ToList();

        private void SortBranches()
        {
            var ordered = _recentTargetRefs.Select(name => Branches.FirstOrDefault(x => x.Name == name)).Where(x => x != null)
                .Concat(Branches.Where(x => !_recentTargetRefs.Contains(x.Name)).OrderBy(x => x.BranchName, StringComparer.Ordinal)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = Branches.IndexOf(ordered[i]);
                if (current != i)
                    Branches.Move(current, i);
            }
        }

        public void RefreshTargetOrder() => SortBranches();

        private PullRequestDefaults CopyDefaults()
        {
            var saved = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey) ?? new PullRequestDefaults();
            return new()
            {
                TargetRef = saved.TargetRef,
                RecentTargetRefs = [.. saved.RecentTargetRefs ?? []],
                Reviewers = saved.Reviewers.Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList(),
                AutoComplete = saved.AutoComplete,
                MergeStrategy = saved.MergeStrategy,
                DeleteSourceBranch = saved.DeleteSourceBranch,
            };
        }

        private void SaveDefaults(PullRequestDefaults defaults, string errorCode)
        {
            var previous = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey);
            _repo.Settings.AzureDevOpsPullRequests[_preferenceKey] = defaults;
            if (!_repo.Settings.TrySave())
            {
                if (previous == null)
                    _repo.Settings.AzureDevOpsPullRequests.Remove(_preferenceKey);
                else
                    _repo.Settings.AzureDevOpsPullRequests[_preferenceKey] = previous;
                Error = App.Text("CreatePR.Error." + errorCode);
            }
        }

        private void RememberTarget(string name)
        {
            var defaults = CopyDefaults();
            defaults.RecentTargetRefs = RecentTargets(defaults, name);
            defaults.TargetRef = name;
            SaveDefaults(defaults, "SelectionsNotSaved");
            _recentTargetRefs = defaults.RecentTargetRefs;
        }

        private void RememberReviewers()
        {
            if (!CanEditRequest || _preferenceKey == null)
                return;
            var defaults = CopyDefaults();
            defaults.Reviewers = Reviewers.Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList();
            SaveDefaults(defaults, "SelectionsNotSaved");
        }

        public Task RetryLoadingAsync() => IsConnected ? RunAsync(LoadCommitTextAsync) : ConnectAsync(false);

        private async Task LoadCommitTextAsync()
        {
            _commitCancellation?.Cancel();
            _commitCancellation = null;
            _commitLoadFailed = false;
            _existingPullRequest = null;
            NotifyState();
            if (_disposed || !IsConnected || HasPending)
            {
                IsLoadingCommits = false;
                return;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _commitCancellation = cancellation;
            var target = Target;
            IsLoadingCommits = true;
            ApplyCommitText(string.Empty, string.Empty);
            try
            {
                if (target == null)
                {
                    Status = string.Empty;
                    return;
                }
                Error = string.Empty;
                Status = App.Text("CreatePR.CheckingExisting");
                var existing = await _client.FindPullRequestsAsync(_sourceRef, target.Name, true, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (existing.Count > 0)
                {
                    ShowExistingPullRequest(existing[0]);
                    return;
                }
                Status = App.Text("CreatePR.LoadingCommits");
                var sourceTip = await _service.ValidateSourceAsync(_sourceRef, cancellation.Token);
                var targetRef = await _client.GetRefAsync(target.Name, cancellation.Token) ?? throw new ServiceException("InvalidTarget");
                var commits = await _client.GetCommitsAsync(sourceTip, targetRef.ObjectId, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var messages = commits.Select(x => (x.Comment ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim()).ToArray();
                var first = messages.FirstOrDefault() ?? string.Empty;
                var newline = first.IndexOf('\n');
                var title = newline < 0 ? first : first[..newline];
                var description = messages.Length > 1
                    ? string.Join("\n\n", messages.Select(x => "- " + x.Replace("\n", "\n  ")))
                    : newline < 0 ? string.Empty : first[(newline + 1)..].Trim();
                ApplyCommitText(title, description);
                Status = string.Empty;
                if (messages.Length == 0)
                {
                    _commitLoadFailed = true;
                    Error = App.Text("CreatePR.Error.NoCommits");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    _commitLoadFailed = true;
                    ShowError(ex);
                }
            }
            finally
            {
                if (ReferenceEquals(_commitCancellation, cancellation))
                {
                    _commitCancellation = null;
                    IsLoadingCommits = false;
                }
            }
        }

        private void ApplyCommitText(string title, string description)
        {
            _updatingCommitText = true;
            try
            {
                if (!_titleEdited)
                    Title = title;
                if (!_descriptionEdited)
                    Description = description;
            }
            finally { _updatingCommitText = false; }
        }

        private void ShowExistingPullRequest(PullRequest pullRequest)
        {
            if (pullRequest.PullRequestId <= 0)
                throw new ServiceException("InvalidResponse");
            _existingPullRequest = pullRequest;
            _searchCancellation?.Cancel();
            SearchResults.Clear();
            ReviewerSearchStatus = string.Empty;
            HasReviewerSearchError = false;
            Status = string.Empty;
            Error = string.Empty;
            NotifyState();
        }

        public void OpenExistingPullRequest()
        {
            if (HasExistingPullRequest)
                Native.OS.OpenBrowser(_client.PullRequestUrl(_existingPullRequest.PullRequestId));
        }

        public Task SignOutAsync() => RunAsync(async () =>
        {
            if (_auth != null)
                await _auth.SignOutAsync();
            _auth?.Dispose();
            _auth = null;
            PersonalAccessToken = string.Empty;
            SessionOnly = false;
            ResetConnection();
            if (!UsePersonalAccessToken)
                Preferences.Instance.AzureDevOpsConnection.AccountId = string.Empty;
            Preferences.Instance.Save();
            ConnectionExpanded = true;
            Status = App.Text("CreatePR.ConnectPrompt");
        });

        public void AddReviewer(Identity identity)
        {
            if (!CanEditRequest || identity == null || !identity.IsActive || !Guid.TryParse(identity.Id, out _) || Reviewers.Any(x => x.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase)))
                return;
            Reviewers.Add(new PullRequestReviewer(RememberReviewers) { Id = identity.Id, Name = identity.Label, Detail = identity.Detail });
            RememberReviewers();
            SearchText = string.Empty;
            NotifyState();
        }

        public void RemoveReviewer(PullRequestReviewer reviewer)
        {
            if (CanEditRequest)
            {
                Reviewers.Remove(reviewer);
                RememberReviewers();
            }
            NotifyState();
        }

        public Task SubmitAsync() => RunAsync(async () =>
        {
            if (!IsConnected || IsFinished || IsLoadingCommits || ((HasExistingPullRequest || _commitLoadFailed) && !HasPending))
                return;
            Status = App.Text(HasPending ? "CreatePR.Checking" : "CreatePR.Creating");
            if (HasPending)
                _result = await _service.ResumeAsync(_lifetime.Token);
            else
            {
                if (Target == null || string.IsNullOrWhiteSpace(Title) || Reviewers.Any(x => !x.IsAvailable))
                    throw new ServiceException("InvalidTarget");
                _result = await _service.CreateAsync(new CreateRequest
                {
                    SourceRefName = _sourceRef,
                    TargetRefName = Target.Name,
                    Title = Title.Trim(),
                    Description = Description,
                    IsDraft = IsDraft,
                    Reviewers = Reviewers.Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList(),
                }, new CompletionOptions
                {
                    MergeStrategy = StrategyValues[MergeStrategy],
                    DeleteSourceBranch = DeleteSourceBranch,
                    MergeCommitMessage = IsSquash ? MergeMessage : null,
                }, AutoComplete, _lifetime.Token);
            }

            if (_result is { State: CreationState.Existing, PullRequest.Status: "active" } && !HasPending)
            {
                ShowExistingPullRequest(_result.PullRequest);
                _result = null;
                return;
            }

            Status = App.Text("CreatePR.Result." + _result.State);
            if (_result.PullRequest?.PullRequestId > 0)
                Status += $"  #{_result.PullRequest.PullRequestId}: {_result.PullRequest.Title}";
            Error = _result.ErrorCode == null ? string.Empty : App.Text("CreatePR.Error." + _result.ErrorCode);
            IsFinished = _result.State is CreationState.Created or CreationState.Existing;
            if (_result.State == CreationState.Created)
                Remember(_result.PullRequest);
        });

        private void Remember(PullRequest pr)
        {
            var previous = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey);
            SaveDefaults(new PullRequestDefaults
            {
                TargetRef = pr.TargetRefName,
                RecentTargetRefs = RecentTargets(previous ?? new PullRequestDefaults(), pr.TargetRefName),
                Reviewers = pr.Reviewers.Where(x => Reviewers.Any(y => string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase))).Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList(),
                AutoComplete = pr.IsDraft ? previous?.AutoComplete ?? true : pr.AutoCompleteSetBy != null || (pr.Status == "completed" && AutoComplete),
                MergeStrategy = pr.CompletionOptions.MergeStrategy,
                DeleteSourceBranch = pr.CompletionOptions.DeleteSourceBranch,
            }, "PreferencesNotSaved");
        }

        public void OpenResult()
        {
            if (HasResultLink)
                Native.OS.OpenBrowser(_client.PullRequestUrl(_result.PullRequest.PullRequestId));
        }

        public void OpenRepository()
        {
            if (_address != null)
                Native.OS.OpenBrowser(_address.WebUrl + "/pullrequests");
        }

        public void ClearRecovery()
        {
            if (!CanClearRecovery)
                return;
            try
            {
                _store.Clear();
                RecoveryAcknowledged = false;
                _result = null;
                Status = string.Empty;
                Error = string.Empty;
                NotifyState();
                _ = LoadCommitTextAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task SearchAsync(string text)
        {
            _searchCancellation?.Cancel();
            _searchCancellation = null;
            IsSearchingReviewers = false;
            ReviewerSearchStatus = string.Empty;
            HasReviewerSearchError = false;
            SearchResults.Clear();
            if (_disposed || !CanEditRequest || string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
                return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _searchCancellation = cancellation;
            IsSearchingReviewers = true;
            ReviewerSearchStatus = App.Text("CreatePR.SearchingReviewers");
            try
            {
                await Task.Delay(250, cancellation.Token);
                var identities = await _client.SearchIdentitiesAsync(text.Trim(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!CanEditRequest)
                    return;
                foreach (var identity in identities.Where(x => !Reviewers.Any(y => string.Equals(y.Id, x.Id, StringComparison.OrdinalIgnoreCase))))
                    SearchResults.Add(identity);
                ReviewerSearchStatus = SearchResults.Count == 0 ? App.Text("CreatePR.NoReviewersFound") : string.Empty;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    HasReviewerSearchError = true;
                    ReviewerSearchStatus = App.Text("CreatePR.Error." + (ex is ServiceException service ? service.Code : "Connection"));
                }
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                {
                    _searchCancellation = null;
                    IsSearchingReviewers = false;
                }
            }
        }

        private async Task<string> ReadLocalTipAsync(CancellationToken cancellationToken)
        {
            if (_localBranch == null)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            var tip = await new Commands.ReadPullRequestBranch(_repo.FullPath, _localBranch.FullName).GetTipAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return tip;
        }

        private async Task RunAsync(Func<Task> action)
        {
            if (IsBusy || _disposed)
                return;
            IsBusy = true;
            Error = string.Empty;
            try
            { await action(); }
            catch (OperationCanceledException) { if (!_disposed) Status = App.Text("CreatePR.Cancelled"); }
            catch (Exception ex) { if (!_disposed) ShowError(ex); }
            finally { IsBusy = false; NotifyState(); }
        }

        private void ShowError(Exception ex)
        {
            var tokenRejected = UsePersonalAccessToken && ex is ServiceException { Code: "Unauthorized" or "Forbidden" };
            var mismatchedTenant = ex is ServiceException { StatusCode: 401, ExpectedTenantId: not null } denied &&
                Guid.TryParse(_auth?.TenantId, out var signedInTenant) && !denied.ExpectedTenantId.Equals(signedInTenant.ToString(), StringComparison.OrdinalIgnoreCase);
            Error = tokenRejected ? App.Text("CreatePR.Error.PersonalAccessTokenRejected") : mismatchedTenant ? App.Text("CreatePR.Error.TenantMismatch", ((ServiceException)ex).ExpectedTenantId, _auth.TenantId) :
                App.Text("CreatePR.Error." + (ex is ServiceException service ? service.Code : ex is MsalException ? "Authentication" : "Connection"));
            if (ex is ServiceException { StatusCode: > 0 } response)
            {
                Error += " " + App.Text("CreatePR.HttpStatus", response.StatusCode);
                if (!mismatchedTenant && !string.IsNullOrWhiteSpace(response.ServerMessage))
                    Error += Environment.NewLine + response.ServerMessage;
            }
            Status = App.Text(IsSignedIn && !IsConnected ? UsePersonalAccessToken ? "CreatePR.TokenRepositoryLoadFailed" : "CreatePR.RepositoryLoadFailed" : "CreatePR.RequestFailed");
            if (tokenRejected)
                ConnectionExpanded = true;
            if (ex is ServiceException { Code: "SignInRequired" or "ConnectionSetup" or "PersonalAccessTokenRequired" })
            {
                IsSignedIn = false;
                ConnectionExpanded = true;
                Status = App.Text("CreatePR.ConnectPrompt");
            }
            NotifyState();
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanEditRequest));
            OnPropertyChanged(nameof(HasExistingPullRequest));
            OnPropertyChanged(nameof(ExistingPullRequestMessage));
            OnPropertyChanged(nameof(ExistingPullRequestTitle));
            OnPropertyChanged(nameof(IsSuccessful));
            OnPropertyChanged(nameof(IsErrorStatus));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(HasUnknown));
            OnPropertyChanged(nameof(HasResultLink));
            OnPropertyChanged(nameof(PrimaryLabel));
            OnPropertyChanged(nameof(CanClearRecovery));
            OnPropertyChanged(nameof(HasAccount));
            OnPropertyChanged(nameof(CanRetryLoading));
            OnPropertyChanged(nameof(HasActivity));
            OnPropertyChanged(nameof(CompletionSummary));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            _searchCancellation?.Cancel();
            _commitCancellation?.Cancel();
            _http.Dispose();
            _lifetime.Dispose();
            _auth?.Dispose();
            PersonalAccessToken = string.Empty;
        }

        private static readonly string[] StrategyValues = ["squash", "rebase", "noFastForward"];
        private readonly Repository _repo;
        private readonly Models.Remote _remote;
        private readonly Models.Branch _localBranch;
        private readonly RemoteAddress _address;
        private readonly string _sourceRef;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource _searchCancellation;
        private CancellationTokenSource _commitCancellation;
        private readonly Func<ConnectionSettings, string, Task<IAuthentication>> _authenticationFactory;
        private readonly Func<string, string, Task<PersonalAccessTokenAuthentication>> _personalAccessTokenFactory;
        private readonly Func<CancellationToken, Task<Commands.PullRequestBranchOrigin>> _branchOriginReader;
        private List<string> _recentTargetRefs = [];
        private IAuthentication _auth;
        private Client _client;
        private RepositoryCreationStore _store;
        private CreationService _service;
        private CreationResult _result;
        private PullRequest _existingPullRequest;
        private string _preferenceKey;
        private string _repositoryLabel;
        private string _clientId;
        private string _tenant;
        private int _authenticationMethod;
        private string _personalAccessToken = string.Empty;
        private string _accountName;
        private string _status;
        private string _error;
        private string _title;
        private string _description = string.Empty;
        private string _mergeMessage;
        private string _searchText = string.Empty;
        private string _reviewerSearchStatus = string.Empty;
        private GitRef _target;
        private bool _searchingReviewers;
        private bool _reviewerSearchError;
        private bool _connectionExpanded;
        private bool _sessionOnly;
        private bool _connected;
        private bool _signedIn;
        private bool _busy;
        private bool _finished;
        private bool _loadingCommits;
        private bool _commitLoadFailed;
        private bool _completionExpanded;
        private bool _updatingCommitText;
        private bool _titleEdited;
        private bool _descriptionEdited;
        private bool _recoveryAcknowledged;
        private bool _messageEdited;
        private bool _draft;
        private bool _auto = true;
        private bool _delete = true;
        private bool _disposed;
        private int _mergeStrategy;
    }
}
