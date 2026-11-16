using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageCore;

namespace ClaudeUsageTray;

/// <summary>
/// Fetches the signed-in account's plan usage from Claude, using Claude Code's saved login.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>On Windows, Claude Code keeps its login in <c>%USERPROFILE%\.claude\.credentials.json</c>
/// (or <c>.credentials.json</c> in <c>CLAUDE_CONFIG_DIR</c>); this reads it the same way.</item>
/// <item>The token is only ever sent to api.anthropic.com, and is never logged or stored by this app.</item>
/// <item>The token is never refreshed here: refreshing rotates it and could sign Claude Code out.
/// When it has expired, using Claude Code renews it.</item>
/// </list>
/// </remarks>
static class ClaudeAccount
{
    public sealed class FetchException(string message) : Exception(message)
    {
        public static FetchException NotSignedIn() => new("Not signed in to Claude Code with a Claude account.");
        public static FetchException Expired() => new("Claude Code's login has expired. Use Claude Code to renew it.");
        public static FetchException BadResponse() => new("Claude returned usage data this app doesn't understand.");
        public static FetchException Network(string detail) => new($"Couldn't reach Claude: {detail}");
    }

    static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<PlanUsage> FetchPlanUsageAsync(IEnumerable<string> roots)
    {
        var token = AccessToken(roots);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("ClaudeUsageTray/1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (TaskCanceledException)
        {
            throw FetchException.Network("the request timed out.");
        }
        catch (HttpRequestException e)
        {
            throw FetchException.Network(e.Message);
        }

        using (response)
        {
            switch ((int)response.StatusCode)
            {
                case 200: break;
                case 401 or 403: throw FetchException.Expired();
                default: throw FetchException.Network($"HTTP {(int)response.StatusCode}");
            }
            var body = await response.Content.ReadAsByteArrayAsync();
            return PlanUsage.Parse(body) ?? throw FetchException.BadResponse();
        }
    }

    /// <summary>Returns Claude Code's current access token, or throws if there isn't a usable one.</summary>
    static string AccessToken(IEnumerable<string> roots)
    {
        foreach (var file in CredentialFiles(roots))
        {
            string? token;
            double? expiresAt;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                    || oauth.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetDouble()
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            if (string.IsNullOrEmpty(token)) continue;

            if (expiresAt is { } ms && DateTimeOffset.FromUnixTimeMilliseconds((long)ms) <= DateTimeOffset.UtcNow)
            {
                throw FetchException.Expired();
            }
            return token;
        }
        throw FetchException.NotSignedIn();
    }

    static IEnumerable<string> CredentialFiles(IEnumerable<string> roots)
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return roots.Append(home)
            .Select(root => Path.Combine(root, ".credentials.json"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists);
    }
}
