using System.Net.Http.Headers;
using System.Text.Json;

namespace PictureFinder.Providers;

/// <summary>TinEye commercial API (https://api.tineye.com). Paid, but very good at finding exact copies.</summary>
public sealed class TinEyeProvider(HttpClient http, IConfiguration configuration) : IReverseImageSearchProvider
{
    private const string SearchUrl = "https://api.tineye.com/rest/search/?limit=100&backlink_limit=20";

    public string Id => "tineye";
    public string Name => "TinEye";
    public string Description => "TinEye API: exact and modified copies, with the pages (backlinks) that use them.";
    public bool SupportsUpload => true;
    public bool SupportsUrl => true;
    public IReadOnlyList<KeyField> KeyFields { get; } = [new("apiKey", "API key", Help: "From your TinEye API account (sent as the x-api-key header).")];
    public bool IsConfiguredOnServer => !string.IsNullOrEmpty(configuration["Providers:TinEye:ApiKey"]);

    public async Task<ProviderResult> SearchAsync(SearchImage image, IReadOnlyDictionary<string, string> keys, CancellationToken ct)
    {
        var apiKey = keys.GetValueOrDefault("apiKey") ?? configuration["Providers:TinEye:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            throw new ProviderException("No TinEye API key.");

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl);
        request.Headers.Add("x-api-key", apiKey);

        using var form = new MultipartFormDataContent();
        if (image.HasBytes)
        {
            var file = new ByteArrayContent(image.Bytes!);
            file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType ?? "application/octet-stream");
            form.Add(file, "image_upload", image.FileName ?? "image.jpg");
        }
        else
        {
            form.Add(new StringContent(image.Url!), "image_url");
        }
        request.Content = form;

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            throw new ProviderException($"TinEye returned HTTP {(int)response.StatusCode} with a non-JSON response.");
        }

        if (!response.IsSuccessStatusCode || (root.TryGetProperty("code", out var code) && code.GetInt32() != 200))
            throw new ProviderException($"TinEye returned HTTP {(int)response.StatusCode}: {Messages(root)}");

        var pages = new List<ProviderMatch>();
        var images = new List<string>();

        if (root.TryGetProperty("results", out var results) && results.TryGetProperty("matches", out var matches))
        {
            foreach (var match in matches.EnumerateArray())
            {
                var imageUrl = Str(match, "image_url");
                if (imageUrl is not null)
                    images.Add(imageUrl);

                if (!match.TryGetProperty("backlinks", out var backlinks))
                    continue;

                foreach (var backlink in backlinks.EnumerateArray())
                {
                    var pageUrl = Str(backlink, "backlink");
                    if (pageUrl is null)
                        continue;
                    var backlinkImage = Str(backlink, "url") ?? imageUrl;
                    // TinEye also finds resized/edited copies; it doesn't say which, so all count as full matches.
                    pages.Add(new ProviderMatch(pageUrl, null, ImageMatchType.Full, backlinkImage is null ? [] : [backlinkImage]));
                }
            }
        }

        return new ProviderResult(pages, images, [], []);
    }

    private static string Messages(JsonElement root) =>
        root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? string.Join("; ", messages.EnumerateArray().Select(m => m.ToString()))
            : "unknown error";

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
