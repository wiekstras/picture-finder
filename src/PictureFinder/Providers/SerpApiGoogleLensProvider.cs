using System.Text.Json;

namespace PictureFinder.Providers;

/// <summary>
/// Google Lens "exact matches" through SerpApi (https://serpapi.com). Lens often finds news sites
/// that Vision misses. SerpApi only accepts a public image URL, not an upload.
/// </summary>
public sealed class SerpApiGoogleLensProvider(HttpClient http, IConfiguration configuration) : IReverseImageSearchProvider
{
    public string Id => "serpapi-lens";
    public string Name => "Google Lens (SerpApi)";
    public string Description => "Google Lens exact matches, searched from the Netherlands. Needs a public image URL.";
    public bool SupportsUpload => false;
    public bool SupportsUrl => true;
    public IReadOnlyList<KeyField> KeyFields { get; } = [new("apiKey", "SerpApi key", Help: "From serpapi.com → Your account. Each search uses one credit.")];
    public bool IsConfiguredOnServer => !string.IsNullOrEmpty(configuration["Providers:SerpApi:ApiKey"]);

    public async Task<ProviderResult> SearchAsync(SearchImage image, IReadOnlyDictionary<string, string> keys, CancellationToken ct)
    {
        var apiKey = keys.GetValueOrDefault("apiKey") ?? configuration["Providers:SerpApi:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            throw new ProviderException("No SerpApi key.");

        var url = "https://serpapi.com/search.json?engine=google_lens&type=exact_matches&hl=nl&country=nl" +
                  $"&url={Uri.EscapeDataString(image.Url!)}&api_key={Uri.EscapeDataString(apiKey)}";

        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            throw new ProviderException($"SerpApi returned HTTP {(int)response.StatusCode} with a non-JSON response.");
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.GetString() ?? "unknown error";
            // SerpApi reports "no results" as an error.
            if (message.Contains("hasn't returned any results", StringComparison.OrdinalIgnoreCase))
                return new ProviderResult([], [], [], []);
            throw new ProviderException($"SerpApi: {message}");
        }

        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"SerpApi returned HTTP {(int)response.StatusCode}.");

        var pages = new List<ProviderMatch>();
        if (root.TryGetProperty("exact_matches", out var matches))
        {
            foreach (var match in matches.EnumerateArray())
            {
                var link = Str(match, "link");
                if (link is null)
                    continue;
                var imageUrl = Str(match, "image") ?? Str(match, "thumbnail");
                pages.Add(new ProviderMatch(link, Str(match, "title"), ImageMatchType.Full, imageUrl is null ? [] : [imageUrl]));
            }
        }

        return new ProviderResult(pages, [], [], []);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
