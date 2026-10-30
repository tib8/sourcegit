using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SourceGit.AzureDevOps
{
    public sealed class ConnectionSettings
    {
        public bool UsePersonalAccessToken { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string Tenant { get; set; } = "organizations";
        public string AccountId { get; set; } = string.Empty;
    }

    public sealed class ConnectionData
    {
        public Identity AuthenticatedUser { get; set; }
    }

    public sealed class PullRequestDefaults
    {
        public string TargetRef { get; set; } = string.Empty;
        public List<string> RecentTargetRefs { get; set; } = [];
        public List<ReviewerSelection> Reviewers { get; set; } = [];
        public bool AutoComplete { get; set; } = true;
        public string MergeStrategy { get; set; } = "squash";
        public bool DeleteSourceBranch { get; set; } = true;
    }

    public sealed class PendingCreation
    {
        public string SourceRef { get; set; }
        public string TargetRef { get; set; }
        public string SourceCommit { get; set; }
        public string Title { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public int PullRequestId { get; set; }
        public CompletionOptions Completion { get; set; }
        public List<ReviewerSelection> Reviewers { get; set; } = [];
        public bool AutoComplete { get; set; }
        public bool IsDraft { get; set; }
    }

    public sealed class ReviewerSelection
    {
        public string Id { get; set; }
        public bool IsRequired { get; set; }
    }

    public sealed class ProjectInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class RepositoryInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string DefaultBranch { get; set; }
        public ProjectInfo Project { get; set; }
    }

    public sealed class GitRef
    {
        public string Name { get; set; }
        public string ObjectId { get; set; }
        [JsonIgnore] public string BranchName => Name?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? Name[11..] : Name;
    }

    public sealed class Identity
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ProviderDisplayName { get; set; }
        public string UniqueName { get; set; }
        public bool IsActive { get; set; }
        public bool IsContainer { get; set; }
        public bool IsRequired { get; set; }
        public bool Inactive { get; set; }
        public Dictionary<string, IdentityProperty> Properties { get; set; } = [];
        [JsonIgnore] public string Label => DisplayName ?? ProviderDisplayName ?? Id;
        [JsonIgnore] public string Detail => UniqueName ?? (Properties.TryGetValue("Mail", out var mail) ? mail.Value : string.Empty);
    }

    public sealed class IdentityProperty
    {
        [JsonPropertyName("$value")] public string Value { get; set; }
    }

    public sealed class SubjectQuery
    {
        public string Query { get; set; }
        public string[] SubjectKind { get; set; } = ["User", "Group"];
    }

    public sealed class GraphSubject
    {
        public string Descriptor { get; set; }
    }

    public sealed class ListResult<T>
    {
        public List<T> Value { get; set; } = [];
    }

    public sealed class CommitRef
    {
        public string CommitId { get; set; }
    }

    public sealed class GitCommit
    {
        public string CommitId { get; set; }
        public string Comment { get; set; }
        public bool CommentTruncated { get; set; }
    }

    public sealed class PullRequest
    {
        public int PullRequestId { get; set; }
        public string Title { get; set; }
        public string SourceRefName { get; set; }
        public string TargetRefName { get; set; }
        public string Status { get; set; }
        public bool IsDraft { get; set; }
        public DateTimeOffset CreationDate { get; set; }
        public Identity CreatedBy { get; set; }
        public Identity AutoCompleteSetBy { get; set; }
        public List<Identity> Reviewers { get; set; } = [];
        public CompletionOptions CompletionOptions { get; set; }
        public CommitRef LastMergeSourceCommit { get; set; }
    }

    public sealed class CreateRequest
    {
        public string SourceRefName { get; set; }
        public string TargetRefName { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public bool IsDraft { get; set; }
        public List<ReviewerSelection> Reviewers { get; set; } = [];
    }

    public sealed class CompletionOptions
    {
        public string MergeStrategy { get; set; } = "squash";
        public bool DeleteSourceBranch { get; set; }
        public string MergeCommitMessage { get; set; }
        public bool TransitionWorkItems { get; set; }
        public bool BypassPolicy { get; set; }
    }

    public sealed class IdentityId
    {
        public string Id { get; set; }
    }

    public sealed class UpdateRequest
    {
        public IdentityId AutoCompleteSetBy { get; set; }
        public CompletionOptions CompletionOptions { get; set; }
    }

    public sealed class ErrorResponse
    {
        public string Message { get; set; }
    }

    public sealed class ServiceException(string code, int statusCode = 0, string serverMessage = null, string expectedTenantId = null) : Exception(code)
    {
        public string Code { get; } = code;
        public int StatusCode { get; } = statusCode;
        public string ServerMessage { get; } = serverMessage;
        public string ExpectedTenantId { get; } = expectedTenantId;
        public bool IsDefinitiveRejection => (StatusCode is >= 400 and < 500 && StatusCode is not (408 or 429)) || (StatusCode == 0 && Code == "SignInRequired");
    }
}
