# Build log

Each step is one commit and one tag (`step-NN-name`), so any step can be checked out,
built and diffed on its own. For each step: what was run, what was decided and why,
and what came out.

Toolchain: .NET SDK 10.0.302 (pinned in `global.json`), MAUI 10.0.20, Android API 36,
Windows 11. No Mac: iOS is published by GitHub Actions (step 11).

## Step 01 · Scaffold (`step-01-scaffold`)

```powershell
git init -b main
dotnet new maui      -n ShelfScan.App        -o ShelfScan.App -ap dev.romain.shelfscan
dotnet new classlib  -n ShelfScan.Core       -o ShelfScan.Core -f net10.0
dotnet new mstest    -n ShelfScan.Core.Tests -o ShelfScan.Core.Tests -f net10.0
dotnet new sln       -n ShelfScan            # .NET 10 writes ShelfScan.slnx
dotnet sln add ShelfScan.App ShelfScan.Core ShelfScan.Core.Tests
dotnet add ShelfScan.App reference ShelfScan.Core
dotnet add ShelfScan.Core.Tests reference ShelfScan.Core
dotnet new gitignore
dotnet new globaljson --sdk-version 10.0.302 --roll-forward latestFeature
dotnet build ShelfScan.slnx
```

Decisions:

- **Three projects, no more.** `ShelfScan.Core` is a plain `net10.0` library, so the parts
  that matter (matching, parsing, storage) are unit-tested on the desktop in seconds.
  Anything that needs a phone (camera, OCR, UI) stays in `ShelfScan.App`.
- **Android + iOS only.** Removed `Platforms/Windows`, `Platforms/MacCatalyst`,
  `Properties/launchSettings.json` and the template's `dotnet_bot.png`. The
  TFMs are `net10.0-android;net10.0-ios`.
- **`MauiXamlInflator=SourceGen` stays on** (the .NET 10 template default): XAML becomes C#
  at compile time, which is what trimming and AOT want anyway.
- `global.json` pins the SDK band because MAUI workloads are per band, so CI gets the same one.

Result: `dotnet build ShelfScan.slnx` builds `net10.0-android` **and** `net10.0-ios` on
Windows with 0 warnings. Compiling iOS needs no Mac. Only publishing does (Xcode, signing).

## Step 02 · AOT and trimming on (`step-02-aot-trim`)

`ShelfScan.App.csproj`:

```xml
<IsAotCompatible>true</IsAotCompatible>
<PublishAot Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">true</PublishAot>

<PropertyGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
  <TrimMode>full</TrimMode>
  <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
</PropertyGroup>
```

`ShelfScan.Core.csproj` gets `<IsAotCompatible>true</IsAotCompatible>` too.

```powershell
dotnet publish ShelfScan.App -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Decisions (sources: [Native AOT on iOS/Mac Catalyst](https://learn.microsoft.com/dotnet/maui/deployment/nativeaot?view=net-maui-10.0),
[Trim a .NET MAUI app](https://learn.microsoft.com/dotnet/maui/deployment/trimming?view=net-maui-10.0),
[.NET for Android build properties](https://learn.microsoft.com/dotnet/android/building-apps/build-properties)):

- **`IsAotCompatible` on every TFM** marks the assembly trimmable and turns on the trim, AOT and
  single-file analyzers. Code the trimmer can't follow becomes an `IL2xxx`/`IL3xxx`
  build warning. The rule for this repo is **zero** of those.
- **iOS → Native AOT.** Supported on iOS/Mac Catalyst. It implies full trimming, so
  `TrimMode` must *not* be set, and `PublishAot` is not conditioned on `Configuration`.
- **Android → Mono, full AOT, full trimming.** Native AOT for Android is experimental
  in .NET 10, so this sticks to what's supported. Release already runs Mono AOT
  (`RunAOTCompilation` defaults to `true`), but *profiled*: only methods in a
  startup profile are precompiled. `AndroidEnableProfiledAot=false` precompiles
  everything. Release trims only the framework by default (`partial`). `TrimMode=full`
  trims our code and every package too.
- **Nothing conditioned on `Configuration`.** MAUI feature switches (QueryProperty,
  implicit operators, compiled bindings with `Source`...) follow `TrimMode`, and Debug must behave like
  Release. Debug builds still don't trim or AOT-compile. They just flip the same switches.
- `PublishTrimmed` is not set: the docs say the SDK sets it when needed.

Result: Release publish of the template app, full AOT + full trim: **0 warnings**, 76
assemblies AOT-compiled to `.so`, 108 s, universal APK (arm64 + x64) **31.97 MB**.
Step 10 compares size and startup against the defaults.

## Step 03 · Book, JSON, Library (`step-03-core-library`)

Files: `ShelfScan.Core/Book.cs`, `ShelfJson.cs`, `Text.cs`, `Library.cs`, tests in
`ShelfScan.Core.Tests/LibraryTests.cs`.

```powershell
dotnet test ShelfScan.Core.Tests
```

Decisions:

- **`Book` is a positional record** whose `Key` is the Open Library *work* key
  (`/works/OL45804W`). A work groups every edition of a book, so owning *any*
  edition counts as owning the book. Hand-saved books get `local:<guid>`.
- **System.Text.Json source generation (`ShelfJson : JsonSerializerContext`)**. The
  reflection-based serializer is what `IsAotCompatible` warns about (IL2026/IL3050).
  The generated `ShelfJson.Default.ListBook` needs no reflection. JSON is snake_case, which
  matches Open Library's own field names (next step).
- **One JSON file**, rewritten on every add: write `books.json.tmp`, then
  `File.Move(..., overwrite: true)`. The rename swaps the file in one step, so a crash
  can't leave a half-written shelf. `ponytail:` comment: switch to SQLite past ~10k books.
- **"Do I already own it?" works offline**: `FindOwned(ocrText)` matches when *every*
  title word and *one* author word appear in what the camera read. Words are lowercased,
  accents stripped (Unicode FormD minus non-spacing marks), single letters dropped. So
  `ANTOINE DE SAINT-EXUPERY / LE PETIT PRINCE` finds *Le Petit Prince* by *Antoine de
  Saint-Exupéry*, while *Vol de nuit* by the same author does not match.
- Plain MSTest `Assert`, a temp file per test, no mocks.

Result: 3 tests pass (129 ms). `ShelfScan.Core` builds with 0 warnings, trim/AOT analyzers on.

## Step 04 · Open Library client (`step-04-openlibrary-client`)

Files: `ShelfScan.Core/OpenLibraryClient.cs` (+ two DTO records), `ShelfJson.cs` gains
`[JsonSerializable(typeof(SearchResponse))]`, test in `OpenLibraryClientTests.cs`.

First a real request, to know the exact response shape:

```powershell
curl.exe -A "ShelfScan/1.0 (+https://github.com/Romain-OD/ShelfScan)" "https://openlibrary.org/search.json?q=moby%20dick%20herman%20melville&fields=key,title,author_name,first_publish_year,cover_i&limit=3"
```

```json
{"numFound":967, ..., "docs":[
 {"author_name":["Herman Melville"],"cover_i":10544254,"first_publish_year":1851,"key":"/works/OL102749W","title":"Moby Dick"},
 {"author_name":["Herman Melville"],"first_publish_year":2019,"key":"/works/OL30237660W","title":"Moby Dick"}, ...]}
```

Decisions:

- **`fields=` + `limit=5`**: ask only for the five fields the app shows. The payload stays
  small, and Open Library does less work per request.
- **Snake_case everywhere**: the `ShelfJson` naming policy from step 03 already maps
  `AuthorName` to `author_name` and `CoverI` to `cover_i`, so the DTOs need no `[JsonPropertyName]`.
  Deserializing uses `ReadFromJsonAsync(ShelfJson.Default.SearchResponse)`, the source-generated
  path with no reflection.
- **Self-contained requests**: the client builds an absolute URL and sets the `User-Agent`
  on each request, so any `HttpClient` works and there's no DI configuration to forget.
- **API etiquette** ([developers/api](https://openlibrary.org/developers/api)): identify the
  app with a `User-Agent`, cache when possible, and don't bulk-harvest. Unidentified requests get
  1 request/s, and a contact *email* in the UA raises that to 3/s. `ponytail:` the UA carries
  the repo URL, not a personal email, because one scan makes one request. The offline
  "already own it?" check (step 03) runs *before* any network call, so the local shelf acts
  as the cache.
- Duplicate works (three "Moby Dick" works above) are real Open Library data. The UI shows
  covers and years so you can pick the right one.
- The test uses a canned `HttpMessageHandler` (no mocking library). It checks the exact URL,
  the UA header, and the mapping, including a doc with no author/cover/year.

Result: 4 tests pass. `ShelfScan.Core` builds with 0 warnings.
