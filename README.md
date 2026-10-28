# Picture Finder

A small proof of concept: a photographer uploads one of their photos and sees where it is used on the internet, with Dutch (`.nl`) sites listed first. It can search several reverse image search services at once and merges the results.

| Provider | Input | Key | Notes |
| --- | --- | --- | --- |
| Google Cloud Vision | upload or URL | API key, service account JSON, or `GOOGLE_APPLICATION_CREDENTIALS` | Web Detection, full and partial matches. Free tier of 1,000 images/month. |
| TinEye | upload or URL | TinEye API key | Paid. Good at exact and resized copies. All TinEye results are shown as full matches. |
| Google Lens (SerpApi) | **URL only** | SerpApi key | Google Lens exact matches, searched with `country=nl`. One credit per search. |

When you enter an image URL, the page also shows links to search Google Lens, Bing, Yandex and TinEye by hand.

- `src/PictureFinder/Program.cs`: minimal API with `GET /api/providers` and `POST /api/check`
- `src/PictureFinder/ReverseImageSearchHandler.cs`: runs the selected providers in parallel, merges pages found by several providers, drops ignored domains and sorts `.nl` first
- `src/PictureFinder/Providers/`: one adapter per service, all implementing `IReverseImageSearchProvider`
- `src/PictureFinder/wwwroot/`: the UI (plain HTML, CSS and JavaScript)
- `src/PictureFinder/appsettings.json`: `IgnoreDomains` (for example your own website and your agency). Pages and images on these domains and their subdomains are left out of the results.

## Entering keys

The quickest way to test is to paste keys into the page under **Search providers**. Keys are sent to the app with each check and used only for that request; the server doesn't store them. By default the browser keeps them for the current tab only; tick **Remember keys in this browser** to keep them in `localStorage`.

For Google you can enter an API key (simplest), paste a service account JSON key (or click **Load file**), or leave both empty to use `GOOGLE_APPLICATION_CREDENTIALS` (see below). Only service account keys are accepted as JSON.

Keys can also be set on the server instead, so you don't have to enter them in the page. Use [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables, not `appsettings.json`:

```bash
cd src/PictureFinder
dotnet user-secrets init
dotnet user-secrets set "Providers:Google:ApiKey" "<key>"
dotnet user-secrets set "Providers:TinEye:ApiKey" "<key>"
dotnet user-secrets set "Providers:SerpApi:ApiKey" "<key>"
```

The page marks providers configured this way with "key set on server".

### Adding a provider

Add a class in `Providers/` that implements `IReverseImageSearchProvider` and register it in `Program.cs` like the others. The UI picks it up from `GET /api/providers`.

## 1. Create a Google Cloud project

1. Go to <https://console.cloud.google.com/> and sign in.
2. Open the project picker at the top and choose **New project**. Give it a name (for example `picture-finder`) and click **Create**.
3. Make sure billing is enabled for the project (**Billing** in the menu). The Vision API has a free monthly tier, but it still needs a billing account.

## 2. Enable the Vision API

Go to **APIs & Services → Library**, search for **Cloud Vision API** and click **Enable**.

Or with the gcloud CLI:

```bash
gcloud services enable vision.googleapis.com --project <your-project-id>
```

## 3. Create credentials

**Option A, API key (simplest for testing):** go to **APIs & Services → Credentials → Create credentials → API key**. Restrict the key to the Cloud Vision API and paste it into the page.

**Option B, service account:**

1. Go to **IAM & Admin → Service Accounts** and click **Create service account**. Give it a name (for example `picture-finder`). It doesn't need any roles to call the Vision API.
2. Open the new service account, go to the **Keys** tab and choose **Add key → Create new key → JSON**. A `.json` key file downloads.
3. Store the key file outside the repository and never commit it.
4. Paste the file's contents into the page, or point the `GOOGLE_APPLICATION_CREDENTIALS` environment variable at the file:

   ```bash
   # macOS / Linux
   export GOOGLE_APPLICATION_CREDENTIALS="$HOME/keys/picture-finder.json"
   ```

   ```powershell
   # Windows (PowerShell)
   $env:GOOGLE_APPLICATION_CREDENTIALS = "C:\keys\picture-finder.json"
   ```

With the environment variable the app uses Application Default Credentials, so no keys go in the code or config. On a development machine you can use `gcloud auth application-default login` instead of a key file.

## 4. Run the app

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
cd src/PictureFinder
dotnet run
```

Open the URL shown in the console (for example `http://localhost:5000`). Drop in a JPG or PNG (max 10 MB) or enter an image URL, switch on the providers you want, enter their keys and click **Check**. You can filter the results (NL / full / partial), search them, and export them as CSV.

## API

`GET /api/providers` lists the providers, their key fields and whether the server has a key configured.

`POST /api/check` takes `multipart/form-data` with:

- `image`: the photo (JPG/PNG, max 10 MB), and/or `imageUrl`: a public http(s) URL of the photo
- `providers`: comma-separated provider ids, for example `google,tineye,serpapi-lens`
- optional keys as `key:{providerId}:{field}`, for example `key:tineye:apiKey`

Response:

```json
{
  "providers": [
    { "id": "google", "name": "Google Cloud Vision", "status": "ok", "message": null, "resultCount": 12, "durationMs": 1240 },
    { "id": "tineye", "name": "TinEye", "status": "error", "message": "No TinEye API key.", "resultCount": 0, "durationMs": 2 }
  ],
  "pagesWithMatchingImages": [
    {
      "url": "https://www.example.nl/article",
      "pageTitle": "Article title",
      "domain": "www.example.nl",
      "isDutch": true,
      "matchType": "full",
      "fullMatchingImageUrls": ["https://www.example.nl/photo.jpg"],
      "partialMatchingImageUrls": [],
      "providers": ["google"]
    }
  ],
  "fullMatchingImages": ["https://..."],
  "partialMatchingImages": ["https://..."],
  "bestGuessLabels": ["..."]
}
```

A page counts as a **full match** if any provider found an exact copy of the photo on it. Otherwise it is a **partial match**, which is usually a crop or an edit. Each provider's status is `ok`, `error` (with a message) or `skipped` (for example Google Lens without an image URL). A failing provider doesn't stop the others. Invalid input returns `{ "error": "..." }` with status 400.

## Status of the adapters

The Google Cloud Vision adapter uses the official client library. The TinEye and SerpApi adapters were written from those services' public API descriptions but haven't been tried with real keys yet. If one returns an error or no results where you expect some, check the field names in `TinEyeProvider.cs` / `SerpApiGoogleLensProvider.cs` against a real response.
