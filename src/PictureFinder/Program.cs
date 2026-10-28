using PictureFinder;
using PictureFinder.Providers;

const long MaxImageBytes = 10 * 1024 * 1024;
string[] allowedContentTypes = ["image/jpeg", "image/png"];

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<GoogleVisionProvider>();
builder.Services.AddHttpClient<TinEyeProvider>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<SerpApiGoogleLensProvider>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddTransient<IReverseImageSearchProvider>(sp => sp.GetRequiredService<GoogleVisionProvider>());
builder.Services.AddTransient<IReverseImageSearchProvider>(sp => sp.GetRequiredService<TinEyeProvider>());
builder.Services.AddTransient<IReverseImageSearchProvider>(sp => sp.GetRequiredService<SerpApiGoogleLensProvider>());
builder.Services.AddTransient<ReverseImageSearchHandler>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/providers", (IEnumerable<IReverseImageSearchProvider> providers) =>
    providers.Select(p => new
    {
        p.Id,
        p.Name,
        p.Description,
        p.SupportsUpload,
        p.SupportsUrl,
        p.IsConfiguredOnServer,
        p.KeyFields,
    }));

// Form fields: image (file) and/or imageUrl, providers (comma separated ids),
// and optional keys as "key:{providerId}:{fieldName}". Keys are only used for this request.
app.MapPost("/api/check", async (HttpRequest http, ReverseImageSearchHandler handler, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Error("Expected multipart/form-data.");

    var form = await http.ReadFormAsync(ct);
    var file = form.Files.GetFile("image");
    var imageUrl = form["imageUrl"].ToString().Trim();

    if ((file is null || file.Length == 0) && imageUrl.Length == 0)
        return Error("Upload an image or enter an image URL.");

    byte[]? bytes = null;
    if (file is { Length: > 0 })
    {
        if (file.Length > MaxImageBytes)
            return Error("Image is larger than 10 MB.");
        if (!allowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return Error("Only JPG and PNG images are supported.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        bytes = buffer.ToArray();
    }

    if (imageUrl.Length > 0 && !(Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        return Error("The image URL must be an http(s) URL.");

    var providerIds = form["providers"].ToString()
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet();
    if (providerIds.Count == 0)
        return Error("Select at least one search provider.");

    var keys = form
        .Where(f => f.Key.StartsWith("key:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(f.Value))
        .Select(f => (Parts: f.Key.Split(':', 3), Value: f.Value.ToString().Trim()))
        .Where(f => f.Parts.Length == 3)
        .GroupBy(f => f.Parts[1])
        .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(f => f.Parts[2], f => f.Value));

    var image = new SearchImage(bytes, file?.FileName, file?.ContentType, imageUrl.Length > 0 ? imageUrl : null);
    return Results.Ok(await handler.ExecuteAsync(image, providerIds, keys, ct));
});

app.Run();

static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest) =>
    Results.Json(new { error = message }, statusCode: statusCode);
