using System.Text.RegularExpressions;
using Google.Cloud.Vision.V1;

namespace PictureFinder;

public sealed partial class ReverseImageSearchHandler(IConfiguration configuration)
{
    private const int MaxResults = 50;

    private readonly string[] _ignoreDomains = (configuration.GetSection("IgnoreDomains").Get<string[]>() ?? [])
        .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
        .Where(d => d.Length > 0)
        .ToArray();

    // Created on first use so missing credentials surface as a request error instead of a startup crash.
    private ImageAnnotatorClient? _client;

    public async Task<ReverseImageSearchResult> ExecuteAsync(Stream image, CancellationToken cancellationToken = default)
    {
        _client ??= await ImageAnnotatorClient.CreateAsync(cancellationToken);

        var request = new AnnotateImageRequest
        {
            Image = await Image.FromStreamAsync(image),
            Features = { new Feature { Type = Feature.Types.Type.WebDetection, MaxResults = MaxResults } }
        };

        var response = await _client.AnnotateAsync(request, cancellationToken);
        if (response.Error is not null)
            throw new InvalidOperationException($"Vision API returned an error: {response.Error.Message}");

        var web = response.WebDetection ?? new WebDetection();

        return new ReverseImageSearchResult(
            PagesWithMatchingImages: web.PagesWithMatchingImages
                .Where(p => !IsIgnored(p.Url))
                .Select(p => new MatchingPage(
                    Url: p.Url,
                    PageTitle: StripTags(p.PageTitle),
                    FullMatchingImageUrls: p.FullMatchingImages.Select(i => i.Url).ToList(),
                    PartialMatchingImageUrls: p.PartialMatchingImages.Select(i => i.Url).ToList()))
                .ToList(),
            FullMatchingImages: web.FullMatchingImages.Select(i => i.Url).Where(u => !IsIgnored(u)).ToList(),
            PartialMatchingImages: web.PartialMatchingImages.Select(i => i.Url).Where(u => !IsIgnored(u)).ToList(),
            BestGuessLabels: web.BestGuessLabels.Select(l => l.Label).ToList());
    }

    private bool IsIgnored(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return _ignoreDomains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    // Vision returns titles with <b> highlight tags.
    private static string StripTags(string html) => System.Net.WebUtility.HtmlDecode(HtmlTagRegex().Replace(html ?? "", "")).Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}

public sealed record ReverseImageSearchResult(
    List<MatchingPage> PagesWithMatchingImages,
    List<string> FullMatchingImages,
    List<string> PartialMatchingImages,
    List<string> BestGuessLabels);

public sealed record MatchingPage(
    string Url,
    string PageTitle,
    List<string> FullMatchingImageUrls,
    List<string> PartialMatchingImageUrls);
