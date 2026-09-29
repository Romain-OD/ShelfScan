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
assemblies AOT-compiled to `.so`, 108 s, universal APK (arm64 + x64) **31.99 MB**.
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

## Step 05 · From OCR lines to a search query (`step-05-ocr-query`)

Files: `ShelfScan.Core/OcrQuery.cs` (`OcrLine` record + `OcrQuery.FromLines`), tests in
`OcrQueryTests.cs`.

Both OCR engines return lines with a bounding box: ML Kit in pixels, Apple Vision normalized
to 0..1. The platform code (steps 06–07) maps them to `OcrLine(Text, Height)`, and everything
after that is shared, plain .NET that runs in tests.

Measured first: how does Open Library's `q=` react to cover noise? (1.2 s between requests)

| Query | Found | Top result |
|---|---|---|
| `MOBY-DICK HERMAN MELVILLE` | 267 | Moby Dick, Herman Melville, 1851 ✅ |
| `… PENGUIN CLASSICS` | 6 | Moby Dick, 1851 ✅ |
| `Pride and Prejudice Jane Austen A NOVEL` | 56 | Pride and Prejudice, 1813 ✅ |
| `FRANKENSTEIN MARY SHELLEY or the Modern Prometheus` | 164 | Frankenstein; or, The Modern Prometheus ✅ |
| `FRANKENSTEIN MARY SHELLEY THE CLASSIC TALE OF TERROR THAT HAS HAUNTED READERS` | 1 | *American Film* ❌ |
| `LE PETIT PRINCE ANTOINE DE SAINT EXUPERY GALLIMARD` | 20 | Le petit prince, 1943 ✅ (accents optional) |

Every word must match, so a few extra words are fine but a tagline sinks the search.

Decisions:

- **Keep the big text**: lines at least 40% as tall as the tallest line, in the OCR's reading
  order. Title and author pass. Series names, taglines and prices don't. Lines without a letter
  (prices, volume numbers) are dropped before the tallest is picked.
- **Strip search syntax**: only letters, digits and apostrophes (`'` and `’`) survive. `-`, `:`
  and `"` are query operators to the search engine, and `MOBY-DICK` still finds *Moby Dick*.
- `ponytail:` it's a heuristic (stylised covers will beat it). The query is shown in an
  editable box, so a wrong guess costs one edit.
- The offline "already own it?" check (step 03) uses **all** OCR text, not this query.

Result: 7 tests pass. `ShelfScan.Core` builds with 0 warnings.

## Step 06 · Android OCR with ML Kit (`step-06-ocr-android`)

Files: `ShelfScan.App/Ocr.cs` (shared `static partial` declaration),
`Platforms/Android/Ocr.Android.cs` (ML Kit), `Platforms/iOS/Ocr.iOS.cs` (placeholder until
step 07), csproj.

**Bundled or unbundled?** From Google's
[text recognition v2 docs](https://developers.google.com/ml-kit/vision/text-recognition/v2/android):

| | Unbundled `play-services-mlkit-text-recognition` | Bundled `com.google.mlkit:text-recognition` |
|---|---|---|
| Model | downloaded by Google Play services | linked into the app |
| Size | about 260 KB per architecture | about 4 MB per architecture |
| First use | may wait for the download | available immediately |

A shelf scanner should work in a bookshop basement with no signal, on its first launch, so we
use **bundled**: NuGet `Xamarin.Google.MLKit.TextRecognition` 116.0.1.9 (binds 16.0.1, has a
`net10.0-android36.0` build). It also depends on the Play Services package, and so does
Google's own POM. That package holds the API classes, and `text-recognition-bundled-common`
adds the model.

Three build fixes, each for a reason:

1. **NU1608**: ML Kit brings newer AndroidX packages than MAUI 10.0.20's `*.Ktx` companions
   allow (for example, `Fragment.Ktx 1.8.8.1` requires `Fragment < 1.8.9`, and 1.9.0 is
   resolved). We reference the four companions at the matching versions (`Collection.Ktx
   1.6.0.1`, `Fragment.Ktx 1.9.0`, `Lifecycle.LiveData 2.11.0.1`,
   `Lifecycle.LiveData.Core.Ktx 2.11.0.1`) instead of `NoWarn`. Restore is clean.
2. **minSdk 21 → 23**: `androidx.tracing` (transitive) declares minSdk 23, so the manifest
   merge fails. Android 6 is a fine floor for 2026. `tools:overrideLibrary` "may lead to
   runtime failures", so we don't use it.
3. The binding's XML docs are empty, so the .NET names (`TextRecognizerOptions.DefaultOptions`,
   `ITextRecognizer.Process`, `Text.TextBlocks[].Lines[].BoundingBox`,
   `Android.Gms.Extensions.AsAsync<T>()`) were read from the assemblies' metadata.

Code decisions:

- `static partial` method, declared once and implemented per platform. The compiler refuses
  to build a platform that lacks an implementation, and there's no interface/DI for a single
  static call. Until step 07, the iOS part throws `PlatformNotSupportedException`, which keeps
  this tag building.
- `InputImage.FromFilePath` applies the photo's EXIF rotation. One `ITextRecognizer` for the
  app's lifetime, as ML Kit recommends. `AsAsync<Text>()` turns the Play Services `Task` into
  an awaitable .NET `Task`.
- Each ML Kit line becomes `OcrLine(text, box height in px)`, and the rest is step 05's shared code.

On Windows, the iOS build now warns *"The linker has been disabled because there's no
connection to a Mac"*. That comes from `PublishAot` (NativeAOT links during the iOS build).
Building with `-p:PublishAot=false` gives 0 warnings. C# still compiles on Windows, and the
real iOS gate is the macOS CI job (step 11).

Result: Release `dotnet publish -f net10.0-android` took 110 s with **0 warnings** under full trim
and full AOT. The universal APK (arm64 + x64) went from 31.99 MB to **42.97 MB**:
`libmlkit_google_ocr_pipeline.so` is 4.2 MB compressed per ABI, plus 1 MB of `.tflite` models and
the ML Kit/Play Services dex. That matches Google's "about 4 MB per architecture".

## Step 07 · iOS OCR with Apple Vision (`step-07-ocr-ios`)

File: `Platforms/iOS/Ocr.iOS.cs` replaces the placeholder. No package: Vision ships with iOS.

- `VNRecognizeTextRequest` defaults are already right for a cover (Accurate level, language
  correction on), so the only setting is `RecognitionLanguages = ["en-US", "fr-FR"]`. That
  matches Android, where ML Kit's Latin model reads both without configuration.
- `VNImageRequestHandler(NSUrl, VNImageOptions)` reads the photo file. **Orientation**: the
  camera call in step 08 uses .NET 10's `MediaPickerOptions.RotateImage = true`, which, per its
  [API docs](https://learn.microsoft.com/dotnet/api/microsoft.maui.media.mediapickeroptions.rotateimage),
  rotates the image "based on EXIF orientation data". Both engines get upright pixels.
- `Perform` is synchronous and CPU-heavy, so it runs in `Task.Run`. A failure becomes an
  `NSErrorException`.
- Vision boxes are normalized (0..1) with the origin at the **bottom-left**, so lines are sorted
  by descending `Y` to read the cover top to bottom. Each line becomes
  `OcrLine(TopCandidates(1)[0].String, box height)`. `OcrQuery` compares heights only relative
  to each other, so pixels (Android) and fractions (iOS) both work.
- Signatures are checked by compiling for `net10.0-ios` on Windows. The docs in
  `Microsoft.iOS.xml` just link to Apple. The compile shows that the completion handler accepts
  `null`, `Results` is `VNRecognizedTextObservation[]?`, and `Perform` has `out NSError?`.

Result: `dotnet build -f net10.0-ios -p:PublishAot=false` gives 0 warnings. The solution builds.
The one warning is the known no-Mac linker notice from step 06, and 7 tests pass. **Not
device-tested** (no Mac): the NativeAOT publish is verified in GitHub Actions in step 11.

## Step 08 · Two pages: shelf and scan (`step-08-ui-pages`)

Files: `App.xaml.cs`, `MainPage.xaml(.cs)`, `ScanPage.xaml(.cs)`, `MauiProgram.cs`,
`Platforms/Android/AndroidManifest.xml`, `Platforms/iOS/Info.plist`. Deleted: `AppShell.xaml(.cs)`.
Core gets `Library.Search` and one more test.

The flow: **Scan a book** → camera → `ScanPage` reads the cover (steps 06/07) → builds the
query (step 05) → checks the shelf offline (step 03) → otherwise asks Open Library (step 04)
→ **Add**, or save by hand.

Decisions:

- **No Shell.** Two pages don't need routes or a flyout: `App.CreateWindow` returns
  `new NavigationPage(mainPage)`, which pushes `ScanPage` and pops back, and `ScanPage` gets the
  photo path as a constructor argument. Shell would work too, but not its `[QueryProperty]`
  attribute: under Native AOT it
  ["won't work"](https://learn.microsoft.com/dotnet/maui/deployment/nativeaot?view=net-maui-10.0#native-aot-limitations)
  (use `IQueryAttributable` instead).
- **Compiled bindings only.** Both item templates declare `x:DataType` (`core:Book`,
  `local:Candidate`), so bindings compile to typed getters instead of reflection. No view
  models, no `INotifyPropertyChanged`: code-behind sets `ItemsSource` again when the list
  changes (on appearing, on each keystroke). One list per page doesn't need MVVM plumbing.
- **Offline first.** After OCR, `Library.FindOwned` checks the raw OCR text against the shelf.
  On a match, the page says *Already on your shelf* and sends **no request**. Otherwise it
  searches Open Library, and a result whose work key you own shows a disabled **Owned** button
  (`Candidate` record).
- **Save by hand** is always on the page, for books Open Library doesn't know or OCR garbles.
  The two tallest OCR lines prefill Title and Author. The key is `local:<guid>`.
- **Camera** (.NET 10 [`MediaPickerOptions`](https://learn.microsoft.com/dotnet/maui/platform-integration/device-media/picker?view=net-maui-10.0#using-media-picker)):
  `RotateImage = true` gives both OCR engines upright pixels. `MaximumWidth/Height = 1600` is
  plenty for cover text. `FullPath` "doesn't always return the physical path", so the photo is
  copied through `OpenReadAsync` into `FileSystem.CacheDirectory` as `scan-<name>`.
- **Permissions**, per [Learn's media picker setup](https://learn.microsoft.com/dotnet/maui/platform-integration/device-media/picker?view=net-maui-10.0#get-started).
  Android: `CAMERA`; `WRITE_EXTERNAL_STORAGE` with `maxSdkVersion="32"` (listed under "taking
  photo", Android 12 and older only); and a `<queries>` entry for `IMAGE_CAPTURE` (package
  visibility, target API 30+). Skipped: `READ_*` media permissions (only for picking from the
  gallery) and `uses-feature` (an optional store filter). iOS: `NSCameraUsageDescription`
  only. There's no gallery and no video, so no photo-library or microphone keys.
- **Shelf search** reuses step 03's `Text.Words`: every typed word must *start* a word of the
  title or author, case and accents ignored. `exup` finds *Saint-Exupéry*, and `moby MEL` finds
  *Moby Dick* by Herman Melville. One-letter words are dropped, so filtering starts at the
  second letter.

Result: 8 tests pass. The solution builds with only the known no-Mac iOS warning. Release
publish (full AOT + full trim): 111 s, **0 warnings**, APK **43.98 MB**, 1.01 MB more than
step 06. Comparing the two APKs entry by entry, most of it is **System.Text.Json**: +391 KB of
AOT code for the two ABIs, plus IL in the assembly store (+335 KB across all assemblies).
Until now no page called `Library` or `OpenLibraryClient`, so the trimmer had removed JSON
completely: with trimming, you pay for code when something uses it. MAUI Controls shrank by 83 KB
(Shell is gone) and Essentials grew by 62 KB (MediaPicker). Whether the app *runs* trimmed is
step 09.

## Step 09 · The Release APK on an emulator (`step-09-emulator-e2e`)

Files: `Resources/Styles/Styles.xaml` (one fix), `docs/e2e/` (camera images and screenshots).

Trimming and AOT only happen in Release (step 02), so a Debug run proves nothing about them.
Everything below is the published Release APK (full Mono AOT, `TrimMode=full`) on the
`shelfscan` AVD (API 36, x86_64):

```powershell
emulator -avd shelfscan -camera-back imagefile:$env:TEMP\camera.png
dotnet publish ShelfScan.App -f net10.0-android -c Release
adb install -r ShelfScan.App\bin\Release\net10.0-android\publish\dev.romain.shelfscan-Signed.apk
```

**A book cover without a book.** The emulator's back camera can show a still image
(`-camera-back imagefile:<file>`). The default `virtualscene` is a 3D room with a poster on the
wall, but the poster isn't in view from where the camera starts, and walking the camera to it
isn't a repeatable test step. With `imagefile`, the camera app only gets part of the picture:
about the left 55% of the width and the middle 55% of the height. I measured that with a
labelled grid, and the saved photo matches the preview. So each test cover sits inside that
window on a 1200×1600 table-coloured canvas (`docs/e2e/moby-dick.png`, `pride-and-prejudice.png`,
`frankenstein.png`). The covers are self-made (public-domain titles, no real cover art). The
emulator reads the file **each time the camera opens**, so switching books means copying
another cover over `camera.png`, with no reboot.

**The first scan hit the network, not the app.** OCR worked straight away, but Open Library
didn't: *"Open Library didn't answer (Connection failure). You can save it by hand."*
The host reached the API fine. The network I'm on inspects HTTPS: a corporate proxy re-signs
every certificate with its own root CA. Windows trusts that root, but the emulator doesn't, so
the TLS handshake failed. The app behaved as designed: it showed the message, kept the manual
form, and trusts only the system CAs. Android's
[network security config](https://developer.android.com/privacy-and-security/security-config)
says apps trust user-added CAs by default only when they target API 23 or lower, so a phone on
such a network fails the same way. For the test, the proxy's root went into the throwaway
emulator's **system** store (rooted `google_apis` image). Since Android 14 the roots ship in the
[Conscrypt APEX](https://source.android.com/docs/core/ota/modular-system/conscrypt), so that
takes a tmpfs copy bind-mounted into the zygote's mount namespace, and it's gone at the next
boot. The app didn't change.

**One fix: the navigation bar title.** The template's `NavigationPage` style draws the title
and back arrow in `Gray200` on a `White` bar in light theme, which is hard to read. The template
starts with Shell, whose own style uses `Black`. Step 08 swapped Shell for a `NavigationPage`,
so this style now applies, and it gets the same `Black`.

What ran, all on the Release APK:

| Check | Result |
|---|---|
| ML Kit OCR on three covers | `MOBY DICK or The Whale HERMAN MELVILLE`, `PRIDE AND PREJUDICE JANE AUSTEN`, `FRANKENSTEIN or The Modern Prometheus MARY SHELLEY`. The three tallest lines win, so *A Novel* is dropped when the title takes two lines |
| Open Library search, source-generated JSON | 5 candidates per cover, with author, year and cover |
| Cover images (URL string bound to `Image.Source`) | load under full trim |
| **Add**, then restart the app (`am force-stop`) | the shelf comes back from `books.json` (snake_case, null fields omitted) |
| Scan a book you own | *✔ Already on your shelf: Moby Dick by Herman Melville*, from the OCR text alone, no search |
| Search Open Library for a book you own | its row shows a disabled **Owned** |
| Save by hand | key `local:<guid>`, and a rescan finds it offline |
| Shelf search | `austen` and `melville` filter as expected |
| Camera permission prompt, cancelled capture | prompt shown once; cancel returns to the shelf |

No crash, and nothing from `AndroidRuntime`, `DOTNET`, `mono` or `monodroid` in logcat's
error level across all of it.

**Not covered here.** The emulator's photos arrive upright, and MediaPicker's processed file
says EXIF orientation 1, so the sideways-photo path isn't exercised. The remaining check is a
real phone: does anything rotate twice (`RotateImage`, then ML Kit's own EXIF handling)? iOS
waits for step 11.

The screenshots are in `docs/e2e/` (`screen-results.png`, `screen-shelf.png`,
`screen-owned.png`). A first launch took 2.5 s (`am start -W`), but that's one cold boot,
not a measurement: step 10 measures size and startup properly.

**Sizes, re-measured.** A clean publish of this step gave 43.98 MB, which didn't match the
43.57 MB logged for step 08, although the only change was two colours. So each tagged step was
published again from scratch (`git worktree add <dir> <tag>`, then `dotnet publish`):

| Tag | APK | Warnings |
|---|---|---|
| `step-02-aot-trim` | 31.99 MB (33,548,959 bytes) | 0 |
| `step-06-ocr-android` | 42.97 MB (45,055,886 bytes) | 0 |
| `step-08-ui-pages` | 43.98 MB (46,117,661 bytes) | 0 |
| `step-09-emulator-e2e` | 43.98 MB (46,117,661 bytes) | 0 |

Two earlier figures were wrong (31.97 and 43.57 MB), probably read from a stale APK. They're
corrected above, and from here on sizes come from clean builds only.
