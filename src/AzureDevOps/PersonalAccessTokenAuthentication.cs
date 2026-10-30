using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AzureDevOps
{
    public sealed class PersonalAccessTokenAuthentication : IAuthentication
    {
        public string AccountId { get; private set; } = string.Empty;
        public string UserName { get; private set; } = string.Empty;
        public string TenantId => string.Empty;
        public bool SessionOnly { get; private set; } = true;
        public bool HasCredentials => !string.IsNullOrEmpty(_token);

        private PersonalAccessTokenAuthentication(string organization, Native.OS.IKeychain keychain)
        {
            _keychain = keychain;
            _storageKey = "AzureDevOps.PAT." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(organization.ToLowerInvariant())));
        }

        public static async Task<PersonalAccessTokenAuthentication> CreateAsync(string organization, string token, Native.OS.IKeychain keychain)
        {
            var result = new PersonalAccessTokenAuthentication(organization, keychain);
            result._token = token?.Trim() ?? string.Empty;
            if (!result.HasCredentials && keychain != null)
            {
                try
                {
                    result._token = await Task.Run(() => keychain.Read(result._storageKey, StorageAccount)).ConfigureAwait(false) ?? string.Empty;
                    result.SessionOnly = !result.HasCredentials;
                }
                catch { result._token = string.Empty; }
            }
            return result;
        }

        public Task SignInAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasCredentials || _token.Length > 1024 || _token.Any(x => x <= ' ' || x > '~'))
                throw new ServiceException("PersonalAccessTokenRequired");
            return Task.CompletedTask;
        }

        public async Task<AuthenticationHeaderValue> GetAuthorizationAsync(CancellationToken cancellationToken)
        {
            await SignInAsync(cancellationToken).ConfigureAwait(false);
            return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _token)));
        }

        public void SetIdentity(Identity identity)
        {
            if (!Guid.TryParse(identity?.Id, out var id) || id == Guid.Empty || identity.IsContainer)
                throw new ServiceException("InvalidResponse");
            AccountId = "pat/" + id.ToString();
            UserName = identity.Label;
        }

        public async Task RememberAsync()
        {
            if (_keychain == null || !HasCredentials || string.IsNullOrEmpty(AccountId))
                return;
            try
            {
                var token = _token;
                SessionOnly = !await Task.Run(() =>
                {
                    _keychain.Store(_storageKey, StorageAccount, token);
                    return _keychain.Read(_storageKey, StorageAccount) == token;
                }).ConfigureAwait(false);
            }
            catch { SessionOnly = true; }
        }

        public async Task SignOutAsync()
        {
            try
            {
                if (_keychain != null)
                    await Task.Run(() => _keychain.Erase(_storageKey, StorageAccount)).ConfigureAwait(false);
            }
            catch { throw new ServiceException("CredentialRemovalFailed"); }
            Dispose();
        }

        public void Dispose()
        {
            _token = string.Empty;
            AccountId = string.Empty;
            UserName = string.Empty;
        }

        private const string StorageAccount = "SourceGit";
        private readonly Native.OS.IKeychain _keychain;
        private readonly string _storageKey;
        private string _token;
    }
}
