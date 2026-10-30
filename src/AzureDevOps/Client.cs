using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AzureDevOps
{
    public sealed class Client(HttpClient http, RemoteAddress address, Func<CancellationToken, Task<AuthenticationHeaderValue>> acquireAuthorization)
    {
        public RemoteAddress Address { get; private set; } = address;
        public RepositoryInfo Repository { get; private set; }

        public async Task<Identity> GetAuthenticatedUserAsync(CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Get, Address.OrganizationUrl + "/_apis/connectionData", null, cancellationToken, null).ConfigureAwait(false);
            var data = await ReadAsync(response, ApiJsonContext.Default.ConnectionData, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(data.AuthenticatedUser?.Id, out var id) || id == Guid.Empty || data.AuthenticatedUser.IsContainer)
                throw new ServiceException("InvalidResponse");
            return data.AuthenticatedUser;
        }

        public async Task<RepositoryInfo> ResolveAsync(CancellationToken cancellationToken)
        {
            Repository = await GetAsync(Address.RepositoryApi, ApiJsonContext.Default.RepositoryInfo, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(Repository?.Id, out _) || string.IsNullOrEmpty(Repository.Project?.Id))
                throw new ServiceException("InvalidResponse");
            Address = new(Address.Organization, Repository.Project.Id, Repository.Id);
            return Repository;
        }

        public string PreferenceKey(string accountId) => $"{Address.Organization.ToLowerInvariant()}/{Repository.Project.Id}/{Repository.Id}/{accountId}";
        public string PullRequestUrl(int id) => $"{Address.OrganizationUrl}/{Uri.EscapeDataString(Repository.Project.Name)}/_git/{Uri.EscapeDataString(Repository.Name)}/pullrequest/{id}";

        public async Task<List<GitRef>> GetBranchesAsync(CancellationToken cancellationToken)
        {
            var result = new List<GitRef>();
            string continuation = null;
            do
            {
                var path = Address.RepositoryApi + "/refs?filter=heads/&$top=1000";
                if (!string.IsNullOrEmpty(continuation))
                    path += "&continuationToken=" + Uri.EscapeDataString(continuation);
                using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                var page = await ReadAsync(response, ApiJsonContext.Default.ListResultGitRef, cancellationToken).ConfigureAwait(false);
                result.AddRange(page.Value.Where(x => x.Name?.StartsWith("refs/heads/", StringComparison.Ordinal) == true));
                continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
            } while (!string.IsNullOrEmpty(continuation));
            return result;
        }

        public async Task<GitRef> GetRefAsync(string name, CancellationToken cancellationToken)
        {
            if (!name.StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new ServiceException("InvalidSource");
            string continuation = null;
            do
            {
                var path = Address.RepositoryApi + "/refs?filter=" + Uri.EscapeDataString(name[5..]);
                if (!string.IsNullOrEmpty(continuation))
                    path += "&continuationToken=" + Uri.EscapeDataString(continuation);
                using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                var refs = await ReadAsync(response, ApiJsonContext.Default.ListResultGitRef, cancellationToken).ConfigureAwait(false);
                var match = refs.Value.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                if (match != null)
                    return match;
                continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
            } while (!string.IsNullOrEmpty(continuation));
            return null;
        }

        public async Task<List<GitCommit>> GetCommitsAsync(string sourceCommit, string targetCommit, CancellationToken cancellationToken)
        {
            if (!IsCommitId(sourceCommit) || !IsCommitId(targetCommit))
                throw new ServiceException("InvalidResponse");
            var result = new List<GitCommit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var skip = 0; ; skip += 100)
            {
                // compareVersion starts the walk; itemVersion is the excluded baseline.
                // Pin both ends while paging. showOldestCommitsFirst would ignore these filters.
                var path = Address.RepositoryApi + "/commits?searchCriteria.itemVersion.versionType=commit&searchCriteria.itemVersion.version=" + targetCommit +
                    "&searchCriteria.compareVersion.versionType=commit&searchCriteria.compareVersion.version=" + sourceCommit + $"&$top=100&$skip={skip}";
                var page = await GetAsync(path, ApiJsonContext.Default.ListResultGitCommit, cancellationToken).ConfigureAwait(false);
                foreach (var entry in page.Value)
                {
                    if (!IsCommitId(entry.CommitId) || !seen.Add(entry.CommitId))
                        throw new ServiceException("InvalidResponse");
                    var commit = entry.CommentTruncated
                        ? await GetAsync(Address.RepositoryApi + "/commits/" + entry.CommitId + "?changeCount=0", ApiJsonContext.Default.GitCommit, cancellationToken).ConfigureAwait(false)
                        : entry;
                    if (commit.CommitId != entry.CommitId || commit.CommentTruncated)
                        throw new ServiceException("InvalidResponse");
                    result.Add(commit);
                }
                if (page.Value.Count < 100)
                    break;
            }
            result.Reverse();
            return result;
        }

        private static bool IsCommitId(string value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

        public async Task<List<Identity>> SearchIdentitiesAsync(string term, CancellationToken cancellationToken)
        {
            try
            {
                var root = $"https://vssps.dev.azure.com/{Uri.EscapeDataString(Address.Organization)}/_apis/";
                using var response = await SendAsync(HttpMethod.Post, root + "graph/subjectquery",
                    JsonSerializer.Serialize(new SubjectQuery { Query = term }, ApiJsonContext.Default.SubjectQuery), cancellationToken, "7.1-preview.1").ConfigureAwait(false);
                var matches = await ReadAsync(response, ApiJsonContext.Default.ListResultGraphSubject, cancellationToken).ConfigureAwait(false);
                var descriptors = matches.Value.Select(x => x.Descriptor).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(50);
                var result = new List<Identity>();
                foreach (var batch in descriptors.Chunk(25))
                {
                    // Graph origin IDs are not PR reviewer IDs. Resolve the stable Azure DevOps identities.
                    var identities = await GetAsync(root + "identities?queryMembership=None&subjectDescriptors=" + Uri.EscapeDataString(string.Join(',', batch)),
                        ApiJsonContext.Default.ListResultIdentity, cancellationToken).ConfigureAwait(false);
                    result.AddRange(identities.Value.Where(x => x.IsActive && Guid.TryParse(x.Id, out _)));
                }
                return result.DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (ServiceException ex) when (ex.StatusCode == 403)
            {
                throw new ServiceException("ReviewerSearchPermission", ex.StatusCode);
            }
        }

        public async Task<List<Identity>> ResolveIdentitiesAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
        {
            var requested = ids.Distinct().ToArray();
            if (requested.Length == 0)
                return [];
            if (requested.Any(x => !Guid.TryParse(x, out _)))
                throw new ServiceException("ReviewerUnavailable");
            var path = $"https://vssps.dev.azure.com/{Uri.EscapeDataString(Address.Organization)}/_apis/identities?queryMembership=None&identityIds={Uri.EscapeDataString(string.Join(',', requested))}";
            var result = await GetAsync(path, ApiJsonContext.Default.ListResultIdentity, cancellationToken).ConfigureAwait(false);
            return result.Value.Where(x => x.IsActive && requested.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public async Task<List<PullRequest>> FindPullRequestsAsync(string source, string target, bool activeOnly, CancellationToken cancellationToken)
        {
            var result = new List<PullRequest>();
            for (var skip = 0; ; skip += 100)
            {
                var path = Address.RepositoryApi + "/pullrequests?searchCriteria.sourceRefName=" + Uri.EscapeDataString(source) + "&searchCriteria.targetRefName=" + Uri.EscapeDataString(target) + "&searchCriteria.status=" + (activeOnly ? "active" : "all") + $"&$top=100&$skip={skip}";
                var page = await GetAsync(path, ApiJsonContext.Default.ListResultPullRequest, cancellationToken).ConfigureAwait(false);
                result.AddRange(page.Value.Where(x => x.SourceRefName == source && x.TargetRefName == target && (!activeOnly || x.Status == "active")));
                if (page.Value.Count < 100)
                    return result;
            }
        }

        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken cancellationToken) => GetAsync(Address.RepositoryApi + $"/pullrequests/{id}", ApiJsonContext.Default.PullRequest, cancellationToken);

        public async Task<PullRequest> CreateAsync(CreateRequest request, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Post, Address.RepositoryApi + "/pullrequests", JsonSerializer.Serialize(request, ApiJsonContext.Default.CreateRequest), cancellationToken).ConfigureAwait(false);
            var result = await ReadAsync(response, ApiJsonContext.Default.PullRequest, cancellationToken).ConfigureAwait(false);
            if (result.PullRequestId <= 0)
                throw new ServiceException("InvalidResponse");
            return result;
        }

        public async Task SetReviewerAsync(int id, ReviewerSelection reviewer, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Put, Address.RepositoryApi + $"/pullrequests/{id}/reviewers/{Uri.EscapeDataString(reviewer.Id)}", JsonSerializer.Serialize(reviewer, ApiJsonContext.Default.ReviewerSelection), cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateAsync(int id, UpdateRequest request, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Patch, Address.RepositoryApi + $"/pullrequests/{id}", JsonSerializer.Serialize(request, ApiJsonContext.Default.UpdateRequest), cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        {
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation && response.Content.Headers.ContentType?.MediaType == "text/html")
                throw new ServiceException("Unauthorized", (int)response.StatusCode);
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false) ?? throw new ServiceException("InvalidResponse", (int)response.StatusCode);
            }
            catch (JsonException)
            {
                throw new ServiceException("InvalidResponse", (int)response.StatusCode);
            }
        }

        private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return null;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var error = await JsonSerializer.DeserializeAsync(stream, ApiJsonContext.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);
                var message = error?.Message?.Trim();
                return message is { Length: > 1024 } ? message[..1024] + "…" : message;
            }
            catch (JsonException) { return null; }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string json, CancellationToken cancellationToken, string apiVersion = "7.1")
        {
            for (var attempt = 0; ; attempt++)
            {
                var url = apiVersion == null ? path : path + (path.Contains('?') ? "&" : "?") + "api-version=" + apiVersion;
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = await acquireAuthorization(cancellationToken).ConfigureAwait(false);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                // Ask for an API error instead of an HTML federated sign-in redirect.
                // Keep redirects disabled so credentials cannot leave the selected services.
                request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
                request.Headers.TryAddWithoutValidation("X-VSS-ReauthenticationAction", "Suppress");
                if (json != null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;
                var status = (int)response.StatusCode;
                if (method == HttpMethod.Get && attempt < 2 && (status == 429 || status >= 500))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(attempt + 1);
                    response.Dispose();
                    if (delay > TimeSpan.FromSeconds(15))
                        throw new ServiceException("Throttled", status);
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                string serverMessage;
                string expectedTenant = null;
                if (response.Headers.TryGetValues("X-VSS-ResourceTenant", out var tenants))
                {
                    var tenant = tenants.FirstOrDefault();
                    if (Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty)
                        expectedTenant = tenantId.ToString();
                }
                try
                { serverMessage = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false); }
                finally { response.Dispose(); }
                throw new ServiceException(status switch
                {
                    401 => "Unauthorized",
                    403 => "Forbidden",
                    404 => "NotFound",
                    409 => "Conflict",
                    429 => "Throttled",
                    >= 300 and < 400 => "UnexpectedRedirect",
                    _ => "ServerRejected",
                }, status, serverMessage, expectedTenant);
            }
        }
    }
}
