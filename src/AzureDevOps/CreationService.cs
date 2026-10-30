using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AzureDevOps
{
    public interface ICreationStore
    {
        PendingCreation Pending { get; }
        void Save(PendingCreation pending);
        void Clear();
    }

    public sealed class RepositoryCreationStore(Models.RepositorySettings settings, string key) : ICreationStore
    {
        public PendingCreation Pending => settings.AzureDevOpsPendingRequests.GetValueOrDefault(key);

        public void Save(PendingCreation pending)
        {
            var previous = Pending;
            settings.AzureDevOpsPendingRequests[key] = pending;
            if (!settings.TrySave())
            {
                if (previous == null)
                    settings.AzureDevOpsPendingRequests.Remove(key);
                else
                    settings.AzureDevOpsPendingRequests[key] = previous;
                throw new ServiceException("StorageFailed");
            }
        }

        public void Clear()
        {
            var previous = Pending;
            settings.AzureDevOpsPendingRequests.Remove(key);
            if (!settings.TrySave())
            {
                if (previous != null)
                    settings.AzureDevOpsPendingRequests[key] = previous;
                throw new ServiceException("StorageFailed");
            }
        }
    }

    public enum CreationState { Created, Existing, Incomplete, Unknown }
    public sealed record CreationResult(CreationState State, PullRequest PullRequest, string ErrorCode = null);

    public sealed class CreationService(Client client, ICreationStore store, Func<CancellationToken, Task<string>> readLocalTip)
    {
        public async Task<string> ValidateSourceAsync(string sourceRef, CancellationToken cancellationToken)
        {
            var source = await client.GetRefAsync(sourceRef, cancellationToken).ConfigureAwait(false);
            if (source == null)
                throw new ServiceException("Unpublished");
            var localTip = await readLocalTip(cancellationToken).ConfigureAwait(false);
            if (localTip != null && !string.Equals(localTip, source.ObjectId, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException("SourceChanged");
            return source.ObjectId;
        }

        public async Task<CreationResult> CreateAsync(CreateRequest request, CompletionOptions completion, bool autoComplete, CancellationToken cancellationToken)
        {
            if (store.Pending != null)
                return await ResumeAsync(cancellationToken).ConfigureAwait(false);
            if (request.SourceRefName == request.TargetRefName || string.IsNullOrWhiteSpace(request.Title))
                throw new ServiceException("InvalidTarget");
            var tip = await ValidateSourceAsync(request.SourceRefName, cancellationToken).ConfigureAwait(false);
            if (await client.GetRefAsync(request.TargetRefName, cancellationToken).ConfigureAwait(false) == null)
                throw new ServiceException("InvalidTarget");
            var duplicates = await client.FindPullRequestsAsync(request.SourceRefName, request.TargetRefName, true, cancellationToken).ConfigureAwait(false);
            if (duplicates.Count > 0)
                return new(CreationState.Existing, duplicates[0]);
            var identities = await client.ResolveIdentitiesAsync(request.Reviewers.Select(x => x.Id), cancellationToken).ConfigureAwait(false);
            if (request.Reviewers.Any(x => !identities.Any(y => y.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase))))
                throw new ServiceException("ReviewerUnavailable");
            tip = await ValidateSourceAsync(request.SourceRefName, cancellationToken).ConfigureAwait(false);

            var pending = new PendingCreation
            {
                SourceRef = request.SourceRefName,
                TargetRef = request.TargetRefName,
                SourceCommit = tip,
                Title = request.Title,
                StartedAt = DateTimeOffset.UtcNow,
                Completion = completion,
                Reviewers = request.Reviewers,
                IsDraft = request.IsDraft,
                AutoComplete = autoComplete && !request.IsDraft,
            };
            store.Save(pending);
            try
            {
                var created = await client.CreateAsync(request, cancellationToken).ConfigureAwait(false);
                pending.PullRequestId = created.PullRequestId;
                store.Save(pending);
            }
            catch (ServiceException ex) when (ex.IsDefinitiveRejection)
            {
                store.Clear();
                throw;
            }
            catch
            {
                return new(CreationState.Unknown, null, "OutcomeUnknown");
            }
            return await ApplySettingsAsync(pending, cancellationToken).ConfigureAwait(false);
        }

        public async Task<CreationResult> ResumeAsync(CancellationToken cancellationToken)
        {
            var pending = store.Pending ?? throw new ServiceException("InvalidResponse");
            if (pending.PullRequestId <= 0)
            {
                var matches = await client.FindPullRequestsAsync(pending.SourceRef, pending.TargetRef, false, cancellationToken).ConfigureAwait(false);
                // A matching pair is not proof that an uncertain POST created this PR. Present
                // candidates for inspection instead of changing their reviewers or completion.
                var candidate = matches.FirstOrDefault(x => x.CreationDate >= pending.StartedAt.AddMinutes(-1) && x.Title == pending.Title);
                return new(CreationState.Unknown, candidate, "OutcomeUnknown");
            }
            return await ApplySettingsAsync(pending, cancellationToken).ConfigureAwait(false);
        }

        private async Task<CreationResult> ApplySettingsAsync(PendingCreation pending, CancellationToken cancellationToken)
        {
            PullRequest pr = null;
            try
            {
                pr = await client.GetPullRequestAsync(pending.PullRequestId, cancellationToken).ConfigureAwait(false);
                if (pr.SourceRefName != pending.SourceRef || pr.TargetRefName != pending.TargetRef)
                    throw new ServiceException("SourceChanged");
                if (pr.Status != "active")
                {
                    store.Clear();
                    return new(CreationState.Existing, pr);
                }

                foreach (var reviewer in pending.Reviewers)
                {
                    var actual = pr.Reviewers.FirstOrDefault(x => string.Equals(x.Id, reviewer.Id, StringComparison.OrdinalIgnoreCase));
                    if (actual == null || (reviewer.IsRequired && !actual.IsRequired))
                        await client.SetReviewerAsync(pr.PullRequestId, reviewer, cancellationToken).ConfigureAwait(false);
                }
                pr = await client.GetPullRequestAsync(pr.PullRequestId, cancellationToken).ConfigureAwait(false);
                if (pending.Reviewers.Any(x => !pr.Reviewers.Any(y => string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) && !y.Inactive && (!x.IsRequired || y.IsRequired))))
                    throw new ServiceException("ReviewerUnavailable");

                var sourceTip = await ValidateSourceAsync(pending.SourceRef, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(sourceTip, pending.SourceCommit, StringComparison.OrdinalIgnoreCase))
                    throw new ServiceException("SourceChanged");
                if (pending.AutoComplete && !Guid.TryParse(pr.CreatedBy?.Id, out _))
                    throw new ServiceException("InvalidResponse");

                await client.UpdateAsync(pr.PullRequestId, new UpdateRequest
                {
                    AutoCompleteSetBy = pending.AutoComplete ? new IdentityId { Id = pr.CreatedBy.Id } : null,
                    CompletionOptions = pending.Completion,
                }, cancellationToken).ConfigureAwait(false);
                pr = await client.GetPullRequestAsync(pr.PullRequestId, cancellationToken).ConfigureAwait(false);
                var actualOptions = pr.CompletionOptions;
                if (actualOptions == null || actualOptions.MergeStrategy != pending.Completion.MergeStrategy || actualOptions.DeleteSourceBranch != pending.Completion.DeleteSourceBranch ||
                    (pending.AutoComplete && pr.Status == "active" && string.IsNullOrEmpty(pr.AutoCompleteSetBy?.Id)))
                    throw new ServiceException("SettingsNotApplied");
                store.Clear();
                return new(CreationState.Created, pr);
            }
            catch (Exception ex)
            {
                return new(CreationState.Incomplete, pr ?? new PullRequest { PullRequestId = pending.PullRequestId, Title = pending.Title }, ex is ServiceException service ? service.Code : "SetupFailed");
            }
        }
    }
}
