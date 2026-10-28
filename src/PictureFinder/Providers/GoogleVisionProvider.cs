using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Vision.V1;
using Grpc.Core;

namespace PictureFinder.Providers;

public sealed partial class GoogleVisionProvider(IConfiguration configuration) : IReverseImageSearchProvider
{
    private const int MaxResults = 50;

    // Clients hold a gRPC channel, so reuse one per credential instead of creating one per request.
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageAnnotatorClient>>> _clients = new();

    public string Id => "google";
    public string Name => "Google Cloud Vision";
    public string Description => "Web Detection: pages, full and partial (cropped/edited) matches from Google's index.";
    public bool SupportsUpload => true;
    public bool SupportsUrl => true;

    public IReadOnlyList<KeyField> KeyFields { get; } =
    [
        new("apiKey", "API key", Help: "A Google Cloud API key restricted to the Vision API."),
        new("credentialsJson", "Or: service account JSON", Multiline: true, Help: "Paste the contents of the service account key file."),
    ];

    // We can't cheaply check ADC without a call, so treat the env var or a configured key as "configured".
    public bool IsConfiguredOnServer =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")) ||
        !string.IsNullOrEmpty(configuration["Providers:Google:ApiKey"]);

    public async Task<ProviderResult> SearchAsync(SearchImage image, IReadOnlyDictionary<string, string> keys, CancellationToken ct)
    {
        var client = await GetClientAsync(keys, ct);

        var request = new AnnotateImageRequest
        {
            Image = image.HasBytes ? Image.FromBytes(image.Bytes) : Image.FromUri(image.Url),
            Features = { new Feature { Type = Feature.Types.Type.WebDetection, MaxResults = MaxResults } }
        };

        AnnotateImageResponse response;
        try
        {
            response = await client.AnnotateAsync(request, ct);
        }
        catch (RpcException ex)
        {
            throw new ProviderException($"Vision API call failed: {ex.Status.StatusCode} - {ex.Status.Detail}", ex);
        }

        if (response.Error is not null)
            throw new ProviderException($"Vision API returned an error: {response.Error.Message}");

        var web = response.WebDetection ?? new WebDetection();

        return new ProviderResult(
            Pages: web.PagesWithMatchingImages
                .Select(p => new ProviderMatch(
                    PageUrl: p.Url,
                    PageTitle: StripTags(p.PageTitle),
                    MatchType: p.FullMatchingImages.Count > 0 ? ImageMatchType.Full : ImageMatchType.Partial,
                    ImageUrls: p.FullMatchingImages.Concat(p.PartialMatchingImages).Select(i => i.Url).ToList()))
                .ToList(),
            FullMatchingImages: web.FullMatchingImages.Select(i => i.Url).ToList(),
            PartialMatchingImages: web.PartialMatchingImages.Select(i => i.Url).ToList(),
            BestGuessLabels: web.BestGuessLabels.Select(l => l.Label).ToList());
    }

    private async Task<ImageAnnotatorClient> GetClientAsync(IReadOnlyDictionary<string, string> keys, CancellationToken ct)
    {
        var builder = new ImageAnnotatorClientBuilder();
        string cacheKey;

        if (keys.TryGetValue("credentialsJson", out var json))
        {
            builder.GoogleCredential = ParseServiceAccount(json);
            cacheKey = "json:" + Hash(json);
        }
        else if (keys.TryGetValue("apiKey", out var apiKey) || (apiKey = configuration["Providers:Google:ApiKey"]) is { Length: > 0 })
        {
            builder.ApiKey = apiKey;
            cacheKey = "key:" + Hash(apiKey);
        }
        else
        {
            cacheKey = "adc"; // Application Default Credentials (GOOGLE_APPLICATION_CREDENTIALS)
        }

        var lazy = _clients.GetOrAdd(cacheKey, _ => new Lazy<Task<ImageAnnotatorClient>>(() => builder.BuildAsync(ct)));
        try
        {
            return await lazy.Value;
        }
        catch (Exception ex)
        {
            _clients.TryRemove(cacheKey, out _);
            throw new ProviderException(cacheKey == "adc"
                ? "No Google credentials. Enter an API key or service account JSON, or set GOOGLE_APPLICATION_CREDENTIALS on the server."
                : $"Could not use the Google credentials: {ex.Message}", ex);
        }
    }

    // Only plain service account keys: other credential types (external_account etc.) can make the
    // server fetch URLs or run programs named in the JSON.
    private static GoogleCredential ParseServiceAccount(string json)
    {
        try
        {
            return CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential();
        }
        catch (Exception ex)
        {
            throw new ProviderException("The pasted Google credentials are not a valid service account key file.", ex);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Vision returns titles with <b> highlight tags and HTML entities.
    private static string StripTags(string html) => WebUtility.HtmlDecode(HtmlTagRegex().Replace(html ?? "", "")).Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
