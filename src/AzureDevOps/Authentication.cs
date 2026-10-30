using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace SourceGit.AzureDevOps
{
    public interface IAuthentication : IDisposable
    {
        string AccountId { get; }
        string UserName { get; }
        string TenantId { get; }
        bool SessionOnly { get; }
        bool HasCredentials { get; }
        Task SignInAsync(CancellationToken cancellationToken);
        Task<AuthenticationHeaderValue> GetAuthorizationAsync(CancellationToken cancellationToken);
        Task SignOutAsync();
    }

    public sealed class Authentication : IAuthentication
    {
        public string AccountId => _account?.HomeAccountId.Identifier ?? string.Empty;
        public string UserName => _account?.Username ?? string.Empty;
        public string TenantId { get; private set; } = string.Empty;
        public bool SessionOnly { get; private set; }
        public bool HasCredentials => _account != null;

        public static async Task<Authentication> CreateAsync(ConnectionSettings settings, string configDirectory)
        {
            if (!Guid.TryParse(settings.ClientId, out _) || (!Guid.TryParse(settings.Tenant, out _) && settings.Tenant != "organizations"))
                throw new ServiceException("ConnectionSetup");

            var result = new Authentication();
            result._app = PublicClientApplicationBuilder.Create(settings.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, settings.Tenant)
                .WithRedirectUri("http://localhost")
                .Build();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ClientId + "/" + settings.Tenant)));
            var directory = Path.Combine(configDirectory, "azure-devops-auth");
            try
            {
                Directory.CreateDirectory(directory);
                var storage = new StorageCreationPropertiesBuilder(key + ".cache", directory)
                    .WithMacKeyChain("SourceGit.AzureDevOps", key)
                    .WithLinuxKeyring("org.sourcegit.AzureDevOps", MsalCacheHelper.LinuxKeyRingDefaultCollection, "SourceGit Azure DevOps",
                        new KeyValuePair<string, string>("Application", "SourceGit"), new KeyValuePair<string, string>("Connection", key))
                    .Build();
                result._cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
                result._cache.VerifyPersistence();
                result._cache.RegisterCache(result._app.UserTokenCache);
            }
            catch
            {
                result.SessionOnly = true;
                result._cache = null;
            }

            var accounts = await result._app.GetAccountsAsync().ConfigureAwait(false);
            result._account = accounts.FirstOrDefault(x => x.HomeAccountId.Identifier == settings.AccountId);
            return result;
        }

        public async Task SignInAsync(CancellationToken cancellationToken)
        {
            var result = await _app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _account = result.Account;
            TenantId = result.TenantId;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (_account == null)
                throw new ServiceException("SignInRequired");
            try
            {
                var result = await _app.AcquireTokenSilent(Scopes, _account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                TenantId = result.TenantId;
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                throw new ServiceException("SignInRequired");
            }
        }

        public async Task<AuthenticationHeaderValue> GetAuthorizationAsync(CancellationToken cancellationToken) =>
            new("Bearer", await GetTokenAsync(cancellationToken).ConfigureAwait(false));

        public async Task SignOutAsync()
        {
            foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
                await _app.RemoveAsync(account).ConfigureAwait(false);
            _account = null;
            TenantId = string.Empty;
        }

        public void Dispose()
        {
            _cache?.UnregisterCache(_app.UserTokenCache);
        }

        private static readonly string[] Scopes = ["https://app.vssps.visualstudio.com/.default"];
        private IPublicClientApplication _app;
        private IAccount _account;
        private MsalCacheHelper _cache;
    }
}
