# Picture Finder

A small proof of concept: a photographer uploads one of their photos and sees where it is used on the internet, with Dutch (`.nl`) sites listed first. It uses the **Web Detection** feature of the Google Cloud Vision API.

- `src/PictureFinder/Program.cs`: minimal API with `POST /api/check`
- `src/PictureFinder/ReverseImageSearchHandler.cs`: the Vision API call
- `src/PictureFinder/wwwroot/index.html`: the UI
- `src/PictureFinder/appsettings.json`: `IgnoreDomains` (for example your own website and your agency). Pages and images on these domains and their subdomains are left out of the results.

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

## 3. Create a service account and set `GOOGLE_APPLICATION_CREDENTIALS`

1. Go to **IAM & Admin → Service Accounts** and click **Create service account**. Give it a name (for example `picture-finder`). It doesn't need any roles to call the Vision API.
2. Open the new service account, go to the **Keys** tab and choose **Add key → Create new key → JSON**. A `.json` key file downloads.
3. Store the key file outside the repository and never commit it.
4. Point the `GOOGLE_APPLICATION_CREDENTIALS` environment variable at the file:

   ```bash
   # macOS / Linux
   export GOOGLE_APPLICATION_CREDENTIALS="$HOME/keys/picture-finder.json"
   ```

   ```powershell
   # Windows (PowerShell)
   $env:GOOGLE_APPLICATION_CREDENTIALS = "C:\keys\picture-finder.json"
   ```

The app uses Application Default Credentials, so no keys go in the code or config. On a development machine you can use `gcloud auth application-default login` instead of a key file.

## 4. Run the app

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
cd src/PictureFinder
dotnet run
```

Open the URL shown in the console (for example `http://localhost:5000`), choose a JPG or PNG (max 10 MB) and click **Check**.

## API

`POST /api/check` with `multipart/form-data` and a single `image` field. Response:

```json
{
  "pagesWithMatchingImages": [
    {
      "url": "https://www.example.nl/article",
      "pageTitle": "Article title",
      "fullMatchingImageUrls": ["https://www.example.nl/photo.jpg"],
      "partialMatchingImageUrls": []
    }
  ],
  "fullMatchingImages": ["https://..."],
  "partialMatchingImages": ["https://..."],
  "bestGuessLabels": ["..."]
}
```

A page counts as a **full match** if it contains at least one full matching image. Otherwise it is a **partial match**, which is usually a crop or an edit. On failure the endpoint returns `{ "error": "..." }` with a 4xx/5xx status code.
