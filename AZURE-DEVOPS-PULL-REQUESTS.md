# Azure DevOps pull requests

SourceGit can create pull requests directly in Azure DevOps Services. Right-click a published local or remote branch in the sidebar and choose **Create Pull Request**. In the history graph, the action is available inside the branch-name submenu. Other hosting providers retain their existing browser workflow.

The Create PR action is shown only when the selected branch's tip commit was authored using the effective Git `user.email` configured for this repository. Email matching ignores case and surrounding whitespace; display names and committer identities are not used. The repository's local Git identity takes precedence over global configuration. Unknown authorship or an empty Git email hides the action. This rule applies to both sidebar/history menus and the existing browser-based provider actions. It checks the selected tip, not every earlier commit on the branch. Authorship is loaded during the normal background branch refresh, so opening a context menu does not launch additional Git commands. Local repository configuration changes trigger a refresh; refresh the repository after changing global identity settings outside SourceGit.

A local branch does not need an upstream setting when exactly one supported remote branch has the same name and commit. When several remotes match, choose the intended branch under **Remotes** explicitly. SourceGit never changes tracking settings to create a PR.

Push the source branch through the ordinary Git workflow first. SourceGit checks the server ref before creation and verifies a corresponding local branch against it. The dialog does not push, commit, rebase, or configure tracking. Uncommitted changes are not included.

## Connect an account

Choose **Microsoft sign-in** or **Personal access token (PAT)** under **Azure DevOps connection**. No organization, tenant, application ID, token, or reviewer is built into SourceGit.

### Personal access token

An existing organization-scoped PAT can connect without a Microsoft Entra application registration or that application's consent. The token remains subject to its expiration, organization policies, and the owner's permissions.

1. Use a PAT for the repository's organization with **Code (Read & write)** for PR operations, **Graph (Read)** for live reviewer search, and **Identity (Read)** for resolving and restoring reviewers. Full access is not required.
2. Choose **Personal access token (PAT)**, paste it into the masked token field, and select **Connect with token**. No application ID or tenant ID is needed.
3. SourceGit checks the authenticated account and repository access using read-only requests. After successful validation it saves the PAT in the operating system credential store, separately for each organization. Later dialogs can reuse it; paste a new token to replace it.

The PAT is never written to ordinary preferences, Git configuration, repository settings, or the recovery journal. When secure storage fails, the current dialog can continue using the token in memory and displays a session-only notice. **Sign out** removes the saved PAT from this computer; it does not revoke the PAT in Azure DevOps. Git credentials are not imported or changed. Successful PR defaults are isolated by the authenticated Azure DevOps account, including when a token is rotated. Microsoft sign-in and PAT defaults are kept separately.

If the token is rejected, check its organization, expiration, scopes, and the account's repository access. Replace an expired token in the connection panel. See Microsoft's [PAT guidance](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate), [PR API permissions](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests/create?view=azure-devops-rest-7.1), and [identity lookup permissions](https://learn.microsoft.com/en-us/rest/api/azure/devops/ims/identities/read-identities?view=azure-devops-rest-7.1).

### Microsoft sign-in

Microsoft sign-in uses Entra work or school accounts and a public-client application registration. A maintainer or administrator needs to provide a registration that the account is allowed to use.

1. Register an application in Microsoft Entra ID with the supported organizational account types required for your deployment.
2. Add the **Mobile and desktop applications** platform with the `http://localhost` redirect URI used by the system browser.
3. Configure delegated Azure DevOps access (`user_impersonation`) and obtain consent as required by the tenant. SourceGit requests `https://app.vssps.visualstudio.com/.default` through MSAL.
4. Open **Azure DevOps connection** in the dialog. Enter the public application/client ID and the tenant ID. Use `organizations` only with a registration configured for multiple organizational tenants.
5. Choose **Sign in with Microsoft**. Use an account with access to the repository and permission to create pull requests. A client secret is neither required nor stored.

Connection settings are saved in the local SourceGit preferences. Tokens are stored separately using MSAL's protected desktop cache. When secure storage is unavailable, a visible message explains that sign-in is limited to the current session. Signing out removes the cached accounts for this connection. Existing Git credentials are not copied into the REST integration.

See Microsoft's [Entra integration guidance](https://learn.microsoft.com/en-us/azure/devops/integrate/get-started/authentication/entra-oauth) and [desktop token-cache documentation](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop).

### Browser sign-in succeeds but repository loading fails

Microsoft authentication and Azure DevOps authorization are separate steps. The dialog retains the signed-in account, shows the HTTP status and Azure DevOps error, and offers **Retry loading** without reopening the browser.

If Azure DevOps reports a different organization tenant, use the tenant ID shown in the error. A single-tenant app must be registered in that directory. To reuse an app registered in another directory, it must support multiple organizational tenants and have the necessary consent in the target directory; enter the target directory's tenant ID in SourceGit. Changing an app to multi-tenant without changing the selected tenant does not resolve the mismatch. You can check the organization's existing directory under **Organization settings → Microsoft Entra ID**.

If the tenants already match, check that the signed-in account has access to the organization and repository. SourceGit does not change organizational access policies or grant permissions.

## Create a pull request

The three most recently selected target branches appear first in the dropdown, newest first, followed by the other branches alphabetically, without duplicates. Deleted branches and the source branch are excluded.

When a local counterpart exists, SourceGit reads its Git branch-creation reflog entry to suggest the target. An explicitly named creation branch is preferred when available on the selected remote. If creation used a commit ID or HEAD, a target is suggested only when exactly one remote branch has that same tip. Missing, expired, or ambiguous history falls back to the last selected target. Git does not retain a permanent parent-branch relationship. You can always select another target.

Select the target branch and review the title, description, and reviewers. SourceGit loads the commits present on the published source but absent from that target. The oldest included commit's subject becomes the title. For one commit, its remaining body becomes the description; for multiple commits, the description lists every full commit message, oldest first. Changing the target updates generated text but preserves fields you edited. Loading messages is read-only, follows every API page, and retrieves complete messages when list responses truncate them.

Type at least two characters of a reviewer's name or email to search in the background. Suggestions update after a brief typing pause; Enter is unnecessary. Each result has its own **Add** button, so no separate selection step is needed. Already selected reviewers are excluded. Add any number of users or groups and choose **Required** separately for each. The server's branch policies determine which approvals are necessary; SourceGit does not impose a particular issue tracker or review partner. Search uses the documented [Graph subject query](https://learn.microsoft.com/en-us/rest/api/azure/devops/graph/subject-query/query?view=azure-devops-rest-7.1), then resolves the returned descriptors to Azure DevOps reviewer IDs. If search access is denied, the reviewer section explains the required scopes.

Completion options include squash, rebase and fast-forward, or a merge commit. The section starts collapsed and summarizes the selected completion mode, merge strategy, and remote source-branch retention/deletion. First-use defaults are **Auto-complete**, **Squash**, and **Delete remote source branch**. Previously saved choices still take precedence. Auto-complete waits for the required approvals and policies, and source-branch deletion happens only after a successful merge. The local source branch is retained. Drafts do not activate auto-complete, and their summary makes that explicit.

Target choices, reviewer additions/removals, and their Required flags are remembered as soon as they change, including when the dialog is closed without creating a PR. Selected reviewers are restored automatically next time and resolved against Azure DevOps again; unavailable reviewers require correction. Completion options are remembered after successful PR creation and verification. These preferences are isolated by Azure repository and account and stored in the local Git common directory's `sourcegit.settings` file. Source, title, description, squash message, and draft state are fresh for each PR.

## Recover from failures

As soon as the dialog has a source and target, it checks for an existing active PR for that exact pair. The check runs on opening with a suggested/remembered target and whenever the target changes. An active PR, including a draft, is shown beside the branch selectors with **Open existing PR**; creation for that pair is disabled. The target selector stays available, because the same source can have a separate PR into another target. Completed and abandoned PRs do not block a new one with further commits. **Check again** reloads the pair if the existing PR was closed elsewhere.

Creation remains disabled while the early check is pending or failed. A final check before creation catches a matching PR opened elsewhere after the dialog loaded. It shows the same existing-PR notice without posting another PR. If creation succeeds but configuration fails, **Retry setup** uses the original PR ID and settings. Existing PR discovery never changes that PR's reviewers or completion settings and does not replace recovery for an uncertain prior creation attempt.

Errors are red and verified successful creation is green, with colors adapted to the light and dark themes. Existing-PR notices and loading messages remain informational.

If a response is lost, SourceGit records the uncertain attempt locally and will not repeat its POST automatically. **Check creation** searches for a matching result without modifying it. Inspect the PR or repository in Azure DevOps. After acknowledging that inspection, **Clear the recovery record** discards only the local recovery information; it does not delete or change a PR. A known PR whose configuration you prefer to complete in the browser can be handled the same way.

Source validation is automatic before PR creation and again before applying completion settings. There is no separate **Refresh source** button. If the source is out of sync, push or synchronize it through the usual Git workflow, then retry creation. **Retry loading** appears only after a connection or commit-loading failure; it is absent from the ready dialog.

The server remains authoritative for permissions, branch policies, conflicts, and supported merge strategies. SourceGit never bypasses policies, casts review votes, or transitions work items. A source that changes during creation leaves configuration incomplete rather than enabling auto-complete for unverified changes.

## Supported scope

Modern Azure DevOps HTTPS and SSH URLs and legacy `visualstudio.com` HTTPS URLs are supported. Fetch and push URLs identifying different repositories are rejected with an explanation; select the intended remote explicitly. Azure DevOps Server/on-premises and cross-repository fork PRs are not supported by this dialog. Interactive Microsoft sign-in supports work or school accounts.

The integration uses Azure DevOps REST API 7.1 and source-generated JSON models. Microsoft authentication uses MSAL; PAT authentication uses an HTTPS Basic header. The organization connection-data endpoint identifies the PAT account. Redirects remain disabled for both methods. Build and test it with the normal SourceGit .NET/Avalonia toolchain, including a Release NativeAOT publish. The regression checks in `tests/AzureDevOpsPullRequestChecks` use synthetic repositories, a fake HTTP server, and injected authentication and credential storage. They do not send real PRs or notify reviewers.
