namespace PictureFinder.Providers;

/// <summary>One reverse image search service (Google Vision, TinEye, ...).</summary>
public interface IReverseImageSearchProvider
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    bool SupportsUpload { get; }
    bool SupportsUrl { get; }
    IReadOnlyList<KeyField> KeyFields { get; }

    /// <summary>True when the server has its own credentials, so the user doesn't need to enter a key.</summary>
    bool IsConfiguredOnServer { get; }

    /// <param name="keys">Keys entered in the UI, by <see cref="KeyField.Name"/>. Empty values are left out.</param>
    Task<ProviderResult> SearchAsync(SearchImage image, IReadOnlyDictionary<string, string> keys, CancellationToken ct);
}

public sealed record KeyField(string Name, string Label, bool Multiline = false, string? Help = null);

/// <summary>The photo to search for: uploaded bytes, a public URL, or both.</summary>
public sealed record SearchImage(byte[]? Bytes, string? FileName, string? ContentType, string? Url)
{
    public bool HasBytes => Bytes is { Length: > 0 };
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
}

public enum ImageMatchType { Full, Partial }

public sealed record ProviderMatch(string PageUrl, string? PageTitle, ImageMatchType MatchType, IReadOnlyList<string> ImageUrls);

public sealed record ProviderResult(
    IReadOnlyList<ProviderMatch> Pages,
    IReadOnlyList<string> FullMatchingImages,
    IReadOnlyList<string> PartialMatchingImages,
    IReadOnlyList<string> BestGuessLabels);

/// <summary>A provider failure with a message that is safe and useful to show in the UI.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);
