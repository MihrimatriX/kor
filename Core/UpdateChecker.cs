using System.Net.Http;
using System.Text.Json;

namespace Kor.Core;

/// <summary>
/// GitHub Releases üzerinden yeni sürüm denetimi (günde en fazla bir kez, arka planda).
/// Hiçbir şey indirmez; sadece "yeni sürüm var" der ve sürüm sayfasını açar.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "MihrimatriX/kor";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases/latest";
    public const string SiteUrl = "https://mihrimatrix.github.io/kor/";

    public sealed record Release(Version Version, string Tag, string Url);

    public static async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kor-UpdateCheck/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return Version.TryParse(tag.TrimStart('v', 'V'), out var version)
            ? new Release(version, tag, url ?? ReleasesUrl)
            : null;
    }

    /// <summary>"v0.3.0" ile "0.3" gibi farklı yazımları karşılaştırabilmek için 3 haneye tamamlar.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
