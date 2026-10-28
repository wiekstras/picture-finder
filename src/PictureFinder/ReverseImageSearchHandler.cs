using System.Diagnostics;
using PictureFinder.Providers;

namespace PictureFinder;

/// <summary>Runs the selected providers in parallel, then merges, filters and sorts their results.</summary>
public sealed class ReverseImageSearchHandler(IEnumerable<IReverseImageSearchProvider> providers, IConfiguration configuration, ILogger<ReverseImageSearchHandler> logger)
{
    private readonly string[] _ignoreDomains = (configuration.GetSection("IgnoreDomains").Get<string[]>() ?? [])
        .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
        .Where(d => d.Length > 0)
        .ToArray();

    public async Task<CheckResponse> ExecuteAsync(SearchImage image, IReadOnlyCollection<string> providerIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> keys, CancellationToken ct)
    {
        var selected = providers.Where(p => providerIds.Contains(p.Id)).ToList();
        var runs = await Task.WhenAll(selected.Select(p => RunAsync(p, image, keys.GetValueOrDefault(p.Id) ?? new Dictionary<string, string>(), ct)));

        var pages = new Dictionary<string, MergedPage>();
        foreach (var (provider, result) in selected.Zip(runs.Select(r => r.Result)))
        {
            if (result is null)
                continue;

            foreach (var match in result.Pages.Where(m => !IsIgnored(m.PageUrl)))
            {
                var key = NormalizeUrl(match.PageUrl);
                if (!pages.TryGetValue(key, out var page))
                    pages[key] = page = new MergedPage(match.PageUrl, HostOf(match.PageUrl));

                if (string.IsNullOrEmpty(page.Title) && !string.IsNullOrEmpty(match.PageTitle))
                    page.Title = match.PageTitle;
                if (match.MatchType == ImageMatchType.Full)
                    page.IsFull = true;
                page.Providers.Add(provider.Id);
                foreach (var imageUrl in match.ImageUrls)
                    (match.MatchType == ImageMatchType.Full ? page.FullImages : page.PartialImages).Add(imageUrl);
            }
        }

        var successful = runs.Where(r => r.Result is not null).Select(r => r.Result!).ToList();

        return new CheckResponse(
            Providers: runs.Select(r => r.Status).ToList(),
            PagesWithMatchingImages: pages.Values
                .Select(p => new PageResult(
                    Url: p.Url,
                    PageTitle: p.Title ?? "",
                    Domain: p.Domain,
                    IsDutch: p.Domain.EndsWith(".nl", StringComparison.Ordinal),
                    MatchType: p.IsFull ? "full" : "partial",
                    FullMatchingImageUrls: p.FullImages.ToList(),
                    PartialMatchingImageUrls: p.PartialImages.Except(p.FullImages).ToList(),
                    Providers: p.Providers.ToList()))
                // NL first, then full before partial, then pages found by more providers.
                .OrderByDescending(p => p.IsDutch)
                .ThenByDescending(p => p.MatchType == "full")
                .ThenByDescending(p => p.Providers.Count)
                .ToList(),
            FullMatchingImages: Distinct(successful.SelectMany(r => r.FullMatchingImages)),
            PartialMatchingImages: Distinct(successful.SelectMany(r => r.PartialMatchingImages)),
            BestGuessLabels: successful.SelectMany(r => r.BestGuessLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<(ProviderStatus Status, ProviderResult? Result)> RunAsync(IReverseImageSearchProvider provider, SearchImage image,
        IReadOnlyDictionary<string, string> keys, CancellationToken ct)
    {
        if (!image.HasUrl && !provider.SupportsUpload)
            return (new ProviderStatus(provider.Id, provider.Name, "skipped", "Needs a public image URL; uploads aren't supported.", 0, 0), null);
        if (!image.HasBytes && !provider.SupportsUrl)
            return (new ProviderStatus(provider.Id, provider.Name, "skipped", "Needs an uploaded file.", 0, 0), null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await provider.SearchAsync(image, keys, ct);
            var count = result.Pages.Count(p => !IsIgnored(p.PageUrl));
            return (new ProviderStatus(provider.Id, provider.Name, "ok", null, count, stopwatch.ElapsedMilliseconds), result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Provider {Provider} failed", provider.Id);
            var message = ex switch
            {
                ProviderException => ex.Message,
                HttpRequestException => $"Could not reach {provider.Name}: {ex.Message}",
                TaskCanceledException => $"{provider.Name} timed out.",
                _ => $"{provider.Name} failed: {ex.Message}",
            };
            return (new ProviderStatus(provider.Id, provider.Name, "error", message, 0, stopwatch.ElapsedMilliseconds), null);
        }
    }

    private List<string> Distinct(IEnumerable<string> urls) =>
        urls.Where(u => !IsIgnored(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private bool IsIgnored(string url)
    {
        var host = HostOf(url);
        return host.Length > 0 && _ignoreDomains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";

    // So the same page found by two providers (http vs https, www, trailing slash) merges into one result.
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host + uri.AbsolutePath.TrimEnd('/') + uri.Query;
    }

    private sealed class MergedPage(string url, string domain)
    {
        public string Url { get; } = url;
        public string Domain { get; } = domain;
        public string? Title { get; set; }
        public bool IsFull { get; set; }
        public HashSet<string> Providers { get; } = [];
        public HashSet<string> FullImages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PartialImages { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CheckResponse(
    List<ProviderStatus> Providers,
    List<PageResult> PagesWithMatchingImages,
    List<string> FullMatchingImages,
    List<string> PartialMatchingImages,
    List<string> BestGuessLabels);

public sealed record ProviderStatus(string Id, string Name, string Status, string? Message, int ResultCount, long DurationMs);

public sealed record PageResult(
    string Url,
    string PageTitle,
    string Domain,
    bool IsDutch,
    string MatchType,
    List<string> FullMatchingImageUrls,
    List<string> PartialMatchingImageUrls,
    List<string> Providers);
