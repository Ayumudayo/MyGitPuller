using System.Text.RegularExpressions;

namespace GitPuller_WinUI.Services;

public interface IRemoteLinkBuilder
{
    bool TryBuildBrowserUrl(string? remoteUrl, out string browserUrl);
}

public sealed class RemoteLinkBuilder : IRemoteLinkBuilder
{
    public static RemoteLinkBuilder Instance { get; } = new();

    public bool TryBuildBrowserUrl(string? remoteUrl, out string browserUrl)
    {
        browserUrl = string.Empty;
        var trimmedRemote = remoteUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedRemote))
        {
            return false;
        }

        if (Uri.TryCreate(trimmedRemote, UriKind.Absolute, out var parsedUri))
        {
            if (parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                browserUrl = trimmedRemote;
                return true;
            }

            if (parsedUri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase)
                && TryMapBrowserHost(parsedUri.Host, out var browserHost))
            {
                var remotePath = parsedUri.AbsolutePath.TrimStart('/');
                return TryBuildMappedBrowserUrl(browserHost, remotePath, out browserUrl);
            }

            return false;
        }

        var scpLikeMatch = Regex.Match(
            trimmedRemote,
            @"^(?:(?<user>[^@\s:]+)@)?(?<host>[^:\s]+):(?<path>[^\\]+)$",
            RegexOptions.CultureInvariant);
        if (!scpLikeMatch.Success)
        {
            return false;
        }

        var path = scpLikeMatch.Groups["path"].Value;
        if (path.IndexOf('/') < 0 || !TryMapBrowserHost(scpLikeMatch.Groups["host"].Value, out var mappedHost))
        {
            return false;
        }

        return TryBuildMappedBrowserUrl(mappedHost, path, out browserUrl);
    }

    private static bool TryMapBrowserHost(string host, out string browserHost)
    {
        browserHost = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("github-bf", StringComparison.OrdinalIgnoreCase))
        {
            browserHost = "github.com";
            return true;
        }

        if (host.Contains('.', StringComparison.Ordinal))
        {
            browserHost = host;
            return true;
        }

        return false;
    }

    private static bool TryBuildMappedBrowserUrl(string browserHost, string remotePath, out string browserUrl)
    {
        browserUrl = string.Empty;
        var normalizedPath = remotePath.Trim().TrimStart('/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[..^4];
        }

        browserUrl = $"https://{browserHost}/{normalizedPath}";
        return Uri.TryCreate(browserUrl, UriKind.Absolute, out _);
    }
}
