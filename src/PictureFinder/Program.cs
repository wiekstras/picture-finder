using Grpc.Core;
using PictureFinder;

const long MaxImageBytes = 10 * 1024 * 1024;
string[] allowedContentTypes = ["image/jpeg", "image/png"];

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ReverseImageSearchHandler>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/check", async (IFormFile? image, ReverseImageSearchHandler handler, ILogger<Program> logger, CancellationToken ct) =>
{
    if (image is null || image.Length == 0)
        return Error("No image uploaded.", StatusCodes.Status400BadRequest);
    if (image.Length > MaxImageBytes)
        return Error("Image is larger than 10 MB.", StatusCodes.Status400BadRequest);
    if (!allowedContentTypes.Contains(image.ContentType, StringComparer.OrdinalIgnoreCase))
        return Error("Only JPG and PNG images are supported.", StatusCodes.Status400BadRequest);

    try
    {
        await using var stream = image.OpenReadStream();
        var result = await handler.ExecuteAsync(stream, ct);
        return Results.Ok(result);
    }
    catch (RpcException ex)
    {
        logger.LogError(ex, "Google Cloud Vision call failed");
        return Error($"Google Cloud Vision API call failed: {ex.Status.StatusCode} - {ex.Status.Detail}", StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogError(ex, "Google credentials not configured");
        return Error("Google credentials are not configured. Set GOOGLE_APPLICATION_CREDENTIALS to the path of a service account key file.", StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Reverse image search failed");
        return Error($"Reverse image search failed: {ex.Message}", StatusCodes.Status500InternalServerError);
    }
})
.DisableAntiforgery();

app.Run();

static IResult Error(string message, int statusCode) => Results.Json(new { error = message }, statusCode: statusCode);
