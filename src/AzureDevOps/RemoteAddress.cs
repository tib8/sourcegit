using System;
using System.Linq;

namespace SourceGit.AzureDevOps
{
    public sealed record RemoteAddress(string Organization, string Project, string Repository)
    {
        public string OrganizationUrl => $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}";
        public string ProjectUrl => OrganizationUrl + (string.IsNullOrEmpty(Project) ? string.Empty : "/" + Uri.EscapeDataString(Project));
        public string RepositoryApi => $"{ProjectUrl}/_apis/git/repositories/{Uri.EscapeDataString(Repository)}";
        public string WebUrl => $"{ProjectUrl}/_git/{Uri.EscapeDataString(Repository)}";
        public string Key => $"{Organization.ToLowerInvariant()}/{Project.ToLowerInvariant()}/{Repository.ToLowerInvariant()}";

        public static bool TryParse(string value, out RemoteAddress address)
        {
            address = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("git@ssh.dev.azure.com:", StringComparison.OrdinalIgnoreCase))
                value = "ssh://git@ssh.dev.azure.com/" + value[22..];

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (parts.Any(x => x.Length == 0 || x.Contains('/') || x.Contains('\\') || x.Any(char.IsControl) || x is "." or ".."))
                return false;

            if (uri.Scheme == "ssh" && uri.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase) && (uri.Port == -1 || uri.Port == 22) && parts.Length == 4 && parts[0] == "v3")
                address = new(parts[1], parts[2], parts[3]);
            else if (uri.Scheme == "https" && uri.IsDefaultPort)
            {
                if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 4 && parts[2] == "_git")
                    address = new(parts[0], parts[1], parts[3]);
                else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                {
                    var organization = uri.Host[..^17];
                    if (organization.Length == 0 || organization.Contains('.'))
                        return false;
                    if (parts.Length > 0 && parts[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
                        parts = parts[1..];
                    if (parts.Length == 3 && parts[1] == "_git")
                        address = new(organization, parts[0], parts[2]);
                    else if (parts.Length == 2 && parts[0] == "_git")
                        address = new(organization, string.Empty, parts[1]);
                }
            }

            return address != null;
        }
    }
}
