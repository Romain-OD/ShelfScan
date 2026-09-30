# Build log

Each step is one commit and one tag (`step-NN-name`), so any step can be checked out,
built and diffed on its own. For each step: what was run, what was decided and why,
and what came out.

Toolchain: .NET SDK 10.0.302 and workload set 10.0.303.1 (both pinned in `global.json`
since step 11), MAUI 10.0.20, Android API 36, Windows 11. No Mac: iOS is published by
GitHub Actions (step 11).

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
  *It didn't: `latestFeature` accepts any newer band, and nothing pinned the workload versions.
  Step 11 fixes both.*

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
dotnet publish ShelfScan.App -f net10.0-android -c Release
```

Decisions (sources: [Native AOT on iOS/Mac Catalyst](https://learn.microsoft.com/dotnet/maui/deployment/nativeaot?view=net-maui-10.0),
[Trim a .NET MAUI app](https://learn.microsoft.com/dotnet/maui/deployment/trimming?view=net-maui-10.0),
[.NET for Android build properties](https://learn.microsoft.com/dotnet/android/building-apps/build-properties)):

- **`IsAotCompatible` on every TFM** marks the assembly trimmable and turns on the trim, AOT and
  single-file analyzers. Code the trimmer can't follow becomes an `IL2xxx`/`IL3xxx`
  build warning. The rule for this repo is **zero** of those.
  *On iOS, MAUI 10.0.20 itself brings 2 (step 11).*
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
assemblies AOT-compiled to `.so`, universal APK (arm64 + x64) **31.99 MB**.
Step 10 measures size and startup against the defaults, and goes back to profiled AOT.

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
| ML Kit OCR on three covers | `MOBY DICK or The Whale HERMAN MELVILLE`, `PRIDE AND PREJUDICE JANE AUSTEN`, `FRANKENSTEIN or The Modern Prometheus MARY SHELLEY`. Lines under 40% of the tallest are left out (step 05): *A Novel* is, *or, The Whale* isn't |
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
needs a device too: step 11 builds it in CI but can't run it.

The screenshots are in `docs/e2e/` (`screen-results.png`, `screen-shelf.png`,
`screen-owned.png`). A first launch took 2.5 s (`am start -W`), but that's one cold boot,
not a measurement: step 10 measures size and startup properly.

**Sizes, re-measured.** A clean publish of this step gave 43.98 MB, which didn't match the
43.57 MB logged for step 08, although the only change was two colours. So each tagged step was
published again from scratch (`git worktree add <dir> <tag>`, then
`dotnet publish ShelfScan.App -f net10.0-android -c Release`):

| Tag | APK | Warnings |
|---|---|---|
| `step-02-aot-trim` | 31.99 MB (33,548,959 bytes) | 0 |
| `step-06-ocr-android` | 42.97 MB (45,055,886 bytes) | 0 |
| `step-08-ui-pages` | 43.98 MB (46,117,661 bytes) | 0 |
| `step-09-emulator-e2e` | 43.98 MB (46,117,661 bytes) | 0 |

Step 06 matches. Step 02 had first been published with `-p:AndroidPackageFormat=apk`, and
that command gives its 31.97 MB again (33,520,222 bytes). With that flag, .NET packages the APK
itself. A plain publish builds the AAB, then bundletool extracts a universal APK from it
(`_CreateUniversalApkFromBundle` in the Android SDK targets). Same app, two sizes: 28 KB apart
for step 02, 36 KB for this step. So every size in this log now comes from a clean plain
publish, and step 02 shows that command. The 43.57 MB for step 08 doesn't come back with either
command, so it was most likely read from a stale APK.

## Step 10 · Size and startup, measured (`step-10-measure-android`)

Files: `scripts/measure-android.ps1` (new), `ShelfScan.App.csproj` (back to profiled AOT).

```powershell
emulator -avd shelfscan -memory 4096 -no-snapshot-load
.\scripts\measure-android.ps1 -Adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
```

The script publishes four Release builds, each from a clean `bin`/`obj`. Only command-line
properties change:

| Variant | Properties | What it is |
|---|---|---|
| Defaults | `-p:TrimMode=partial -p:IsAotCompatible=false` | a new MAUI app in Release: profiled AOT, framework-only trimming |
| JIT | `-p:RunAOTCompilation=false` | full trim, no AOT |
| Profiled AOT | none | full trim, profiled AOT: **what ships from this step on** |
| Full AOT | `-p:AndroidEnableProfiledAot=false` | full trim, full AOT: what steps 02 to 09 shipped |

- The defaults need `IsAotCompatible=false`. It implies `IsTrimmable`
  (`Microsoft.NET.Publish.targets`), and partial mode trims every assembly marked trimmable,
  so our own code would still be trimmed.
- `RunAOTCompilation=false` alone gives a real JIT build. The Android SDK runs AOT only when
  `AotAssemblies` is true, and that property follows `RunAOTCompilation`. The JIT APK has no
  `libaot-*.so` file at all.

**How startup is timed.** `adb shell am start -S -W` force-stops the app, launches it and waits
for the first frame. `TotalTime` is Android's [time to initial display](https://developer.android.com/topic/performance/vitals/launch-time#time-initial).
Each round reinstalls each variant, launches it once to warm up, then times five cold starts
(the script checks `LaunchState: COLD`). Four rounds interleave the variants, so a slow moment
on the host hits all four, not just one: 20 launches per variant. This is an emulator (x86_64,
Android 16, 4 GB, cold-booted just before), not a phone, so compare the variants with each other.

Two runs, each on its own fresh boot. Run C came first, with the csproj still on full AOT. Run D
is this commit. The APKs are identical to the byte across both runs
(MB = 1,048,576 bytes, here and in every step).

| Variant | APK | Warnings | Cold start, run C | Cold start, run D |
|---|---|---|---|---|
| Defaults | 38.29 MB (40,147,271 bytes) | 0, analysis off | 827 ms (745–981) | 925 ms (777–1142) |
| JIT | 30.06 MB (31,517,518 bytes) | 0 | 1311 ms (1153–3000) | 1422.5 ms (1201–3665) |
| **Profiled AOT** | **35.69 MB (37,425,949 bytes)** | **0** | **888 ms (745–1042)** | **949 ms (821–1048)** |
| Full AOT | 43.98 MB (46,117,661 bytes) | 0 | 872 ms (772–1016) | 922 ms (815–3684) |

Medians, with min–max. Run D is 50–110 ms slower across the board: that's the spread between
two boots of the same emulator. So compare within a run (Mann–Whitney, two-sided):

| Against profiled AOT | Run C | Run D |
|---|---|---|
| JIT | +423 ms, p < 0.001 | +473.5 ms, p < 0.001 |
| Full AOT | −16 ms, p = 0.58 | −27 ms, p = 0.53 |
| Defaults | −61 ms, p = 0.03 | −24 ms, p = 0.52 |

What the numbers say:

1. **AOT is what makes startup fast.** Without it, the first frame comes about 50% later, in
   both runs. It's the only difference that clearly beats the noise.
2. **Full AOT costs 8.29 MB and buys no measurable startup.** The APK grows by 8,691,712 bytes
   (+23%). The `libaot-*.so` files account for all of it but 681 bytes: 182 of them in both
   builds, 5.56 MB compressed with the startup profile and 13.85 MB without (16.30 → 44.25 MB
   uncompressed, two ABIs). Startup moves by −16 and −27 ms, with p around 0.5 both times.
   That's what the profile is for: it already precompiles what runs at startup.
3. **Full trim saves 2.60 MB, not startup time.** Against the defaults, startup is a wash.
   Run C had the defaults 45–61 ms ahead of both full-trim builds (p = 0.02 and 0.03), run D
   didn't repeat it (p > 0.5), and the 2 GB run below had ours 23 ms ahead.
4. **The defaults hide trim warnings.** Their 0 doesn't mean what ours means. With
   `TrimMode=partial` and no `IsAotCompatible`, the Android SDK sets
   `SuppressTrimAnalysisWarnings=true` and leaves `EnableTrimAnalyzer` off
   (`Microsoft.Android.Sdk.DefaultProperties.targets`, checked with `dotnet msbuild -getProperty`).
   The trimmer still removes code. It just doesn't report what might break. The only trace is
   one line per RID, which ILLink prints only when warnings are suppressed: *"Optimizing
   assemblies for size may change the behavior of the app. Be sure to test after publishing."*
   The script turns that line into "0 (trim analysis off)". ShelfScan's 0 is with the analysis on.
5. **Publish time is an order, not a number.** The same build moves by tens of seconds between
   runs. JIT (95–103 s) < defaults and profiled AOT (163–185 s) < full AOT (193–211 s).

**Memory changes the answer.** The AVD first ran with its default 2 GB. Timings crept up round
after round, and the emulator ended with 245 MB free and 770 MB in swap. Medians there:
1089.5 / 1525.5 / 1066.5 / 1159.5 ms (defaults / JIT / profiled / full), with full AOT the
slowest of the AOT builds. So both runs above use `-memory 4096` and a cold boot.

**Decision: back to profiled AOT.** `AndroidEnableProfiledAot=false` is gone from the csproj.
Release already defaults to `RunAOTCompilation=true` and `AndroidEnableProfiledAot=true`,
which the [migration guide](https://learn.microsoft.com/dotnet/maui/migration/android-projects?view=net-maui-10.0#ahead-of-time-compilation)
says "chooses the optimal settings for startup time and app size". Full AOT had one way to win
the size back, `AndroidStripILAfterAOT`, but it was experimental in .NET 8 and is removed in
.NET 10 ([build properties](https://learn.microsoft.com/dotnet/android/building-apps/build-properties)).
`TrimMode=full` stays: 2.60 MB smaller, with the trim analysis on. Step 02 guessed. This step
measured.

**Not measured.** With profiled AOT, code outside the startup profile is JIT-compiled the first
time it runs. The scan page, ML Kit and the Open Library JSON are in that group, so the first
scan after a launch may be a little slower than with full AOT. Nothing here times it.

**The shipped build, again.** The profiled-AOT APK ran the step 09 flow on a fresh boot: scan,
pick a result, **Add**, scan again, *Already on your shelf*, force-stop and relaunch, still on
the shelf. Covers load in the results and on the shelf. No errors in logcat. `dotnet test`: 8
passed. Solution build: only the known iOS "no connection to a Mac" warning.

## Step 11 · iOS in GitHub Actions (`step-11-ios-ci`)

Files: `.github/workflows/ios.yml` (new), `global.json`, `scripts/measure-android.ps1`.

There's no Mac here, so a macOS runner does the iOS work. It runs the tests, then publishes an
unsigned Release build for a device (`ios-arm64`) twice: once as ShelfScan is configured
(Native AOT, full trimming), and once with the MAUI defaults (Mono AOT, partial trimming), the
same "defaults" as step 10's Android script. Each job writes the `.app` and `.ipa` sizes and its
warnings to the job summary and to the log. The workflow runs on pushes to `main` and on pull
requests ([runs](https://github.com/Romain-OD/ShelfScan/actions/workflows/ios.yml)).

```yaml
runs-on: macos-26
strategy:
  matrix:
    include:
      - build: Native AOT (ShelfScan)
        props: ""
      - build: Mono AOT (defaults)
        props: "-p:PublishAot=false -p:IsAotCompatible=false"
steps:
  - uses: actions/checkout@v7
  - uses: actions/setup-dotnet@v6
    with:
      global-json-file: global.json
  - run: sudo xcode-select -s /Applications/Xcode_26.6.app
  - run: dotnet workload restore ShelfScan.App/ShelfScan.App.csproj
  - run: dotnet test ShelfScan.Core.Tests
  - run: >-
      dotnet publish ShelfScan.App -f net10.0-ios -r ios-arm64 -c Release
      -p:EnableCodeSigning=false ${{ matrix.props }}
      | tee publish.log
```

A last step adds up the files in the `.app`, reads the size of the `.ipa`, and counts the
warning lines in `publish.log`.

Why each piece is there:

- **SDK band and workload set, both pinned.** Since step 01, `global.json` had
  `rollForward: latestFeature`, which lets the SDK roll forward to any newer band, and MAUI
  workloads are per band. Now:

  ```json
  { "sdk": { "rollForward": "latestPatch", "version": "10.0.302", "workloadVersion": "10.0.303.1" } }
  ```

  `latestPatch` stays in the 10.0.3xx band. `workloadVersion` pins MAUI, the iOS SDK and the
  Android SDK together: "If you have a workload-set version in the global.json file, the workload
  commands are in `workload-set` mode even if you haven't run the `config` command or used
  `--version`" ([workload sets](https://learn.microsoft.com/dotnet/core/tools/dotnet-workload-sets)).
  CI got SDK 10.0.303 and the dev machine has 10.0.302: same band, same workload set.
- **Xcode 26.6.** The iOS SDK in that set (26.5.10315) recommends Xcode 26.6
  (`Microsoft.iOS.Sdk.Versions.props`), and it fails the build with error E0191 when the selected
  Xcode has a different major.minor version (`Xamarin.Shared.Sdk.targets`). So the job selects
  Xcode 26.6 explicitly instead of relying on the image's default.
- **`dotnet workload restore` on the app project** installs what the project needs, at the set's
  versions. The log says `Installing workload version 10.0.303.1.`, then
  `Successfully installed workload(s) maui-android maui-ios.` Android is included because restore
  evaluates every target framework, and it fails with NETSDK1147 if a workload is missing.
- **Unsigned.** There's no certificate or provisioning profile in CI, so the job passes
  `EnableCodeSigning=false`. It's still the device build (`ios-arm64`, Release), just without
  a signature.
- **`PublishAot` stays in the csproj.** On the command line, `-p:PublishAot=true` is a global
  property. It also reaches ShelfScan.Core (`net10.0`), which then fails with NETSDK1203 (I tried
  it locally). The csproj sets it for iOS only, so the Native AOT job passes nothing extra.
- **`shell: bash`, spelled out.** When the shell is named, GitHub runs bash with `-o pipefail`, so
  `| tee` can't turn a failed publish into a green step.
- **Files, not log lines.** On Windows, with no Mac, an iOS publish printed "Created the
  package" but wrote no `.ipa`. So the last step measures the files themselves, and `find` or
  `stat` fails the job when one is missing (checked in Git Bash with `-e -o pipefail`).
- **Both warning formats.** Compilers print `file(1,1): warning IL2026: ...`. ILLink and ILC
  print `Trim analysis warning IL2026: ...` and `AOT analysis warning IL3050: ...`, with no colon
  before `warning`. Step 10's pattern (`: warning `) only matched the first format. Both scripts now
  use `warning [A-Z]+[0-9]+:|: warning :`. Step 10's numbers don't change: all four of its logs
  count 0 with either pattern.

Results. Both jobs passed, and all 8 tests passed in each (MB = 1,048,576 bytes):

| ios-arm64, Release, unsigned | `.app` | `.ipa` | Warnings | Publish step (2 runs) |
|---|---|---|---|---|
| **Native AOT (ShelfScan)** | **14.88 MB** (15,603,548 bytes) | **5.98 MB** (6,265,748 bytes) | **2**, both in `Microsoft.Maui` | 2 min 38 s, 1 min 57 s |
| Mono AOT (defaults) | 45.10 MB (47,291,085 bytes) | 15.47 MB (16,217,904 bytes) | 0, analysis off | 9 min 29 s, 8 min 40 s |

The `.app` size is its files added up, and the `.ipa` is that bundle zipped. The sizes come from
one run, the publish times from two.

What the numbers say:

1. **About 3× smaller.** The `.app` is 3.03× smaller and the `.ipa` 2.59×, from Native AOT plus
   full trimming against the defaults. Microsoft's illustrative figure for a `dotnet new maui`
   app is "typically up to 2.5x smaller"
   ([Native AOT deployment](https://learn.microsoft.com/dotnet/maui/deployment/nativeaot?view=net-maui-10.0#native-aot-performance-benefits)).
2. **Publish about 4× faster**: 3.6× in one run and 4.4× in the other. Microsoft's figure is
   "up to 2.8x faster build times on iOS devices". As in step 10, treat this as an order of
   magnitude, not a precise number: each job's time moved by 41 to 49 s between the two runs.
   *Four runs give 2.6× to 4.4× (step 12).*
3. **The 2 warnings are MAUI's.** By default, trim analysis produces "at most one warning for each
   assembly that comes from a `PackageReference`"
   ([trimming options](https://learn.microsoft.com/dotnet/core/deploying/trimming/trimming-options#show-detailed-warnings)).
   With Native AOT, that's one warning of each kind:

   ```text
   Microsoft.Maui.dll : warning IL2104: Assembly 'Microsoft.Maui' produced trim warnings. For more information see https://aka.ms/il2104
   Microsoft.Maui.dll : warning IL3053: Assembly 'Microsoft.Maui' produced AOT analysis warnings.
   ```

   One run with `-p:TrimmerSingleWarn=false` in the Native AOT job's `props` listed what's behind
   them: 20 warnings, an IL2026 (trim) and an IL3050 (AOT) for each of 10 members. All 20 are in
   `Microsoft.Maui`, and none in ShelfScan.App or ShelfScan.Core:

   | Private class in `HybridWebViewHandler` (iOS) | Members |
   |---|---|
   | `SchemeHandler : NSObject, IWKUrlSchemeHandler` | static constructor, constructor, `Handler`, `GetResponseBytesAsync`, `StartUrlSchemeTask`, `StopUrlSchemeTask` |
   | `WebViewScriptMessageHandler : NSObject, IWKScriptMessageHandler` | static constructor, constructor, `Handler`, `DidReceiveScriptMessage` |

   All 20 come from the same method:

   ```text
   ILC : AOT analysis warning IL3050: <Module>..cctor(): Using member 'Microsoft.Maui.Handlers.HybridWebViewHandler.SchemeHandler..cctor()' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling. HybridWebView uses dynamic System.Text.Json serialization features.
   ```

   How that happens:

   - MAUI 10.0.20 marks both classes `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`
     ([source](https://github.com/dotnet/maui/blob/10.0.20/src/Core/src/Handlers/HybridWebView/HybridWebViewHandler.iOS.cs#L113-L139)).
   - MAUI already turns HybridWebView off in this build. Its target `_MauiPrepareForILLink`
     (`Microsoft.Maui.Controls.targets`) sets `MauiHybridWebViewSupported=false` when `PublishAot`
     is true or `TrimMode` is `full`, and passes it to the trimmer as the feature switch
     `Microsoft.Maui.RuntimeFeature.IsHybridWebViewSupported`. Running that target locally gives
     `false`. ShelfScan has no `WebView` or `HybridWebView` anywhere.
   - The references come from `<Module>..cctor()`, the assembly's module constructor. Before the
     trimmer marks anything, the iOS SDK's `MarkNSObjectsStep` adds a `[DynamicDependency]` there
     for `NSObject` subclasses
     ([source, at this SDK's tag](https://github.com/dotnet/macios/blob/dotnet-10.0.1xx-xcode26.5-10315/tools/dotnet-linker/MarkNSObjectsStep.cs#L44-L50)).
     That keeps both classes, so the analysis sees a method that uses members marked unsafe.
   - MAUI's own .NET 11 CI hit the same warnings ([#35740](https://github.com/dotnet/maui/issues/35740)).
     [PR #35626](https://github.com/dotnet/maui/pull/35626) makes HybridWebView AOT-safe with a
     source generator, for .NET 11. MAUI 10.0.20, the version in the pinned workload set, doesn't
     have it.

   **Decision: leave them visible.** No `NoWarn`, and no workaround. Silencing IL2104 and IL3053
   would also silence the next package that really does break under trimming. Microsoft's docs
   cover this case: "There may be cases where fixing trimming and AOT warnings isn't possible,
   such as when they occur for third-party libraries. In such cases, third-party libraries will
   need to be updated to become fully compatible." The same page says that a single warning means
   the app "might not work correctly". Here the flagged code belongs to HybridWebView, which
   ShelfScan never creates, but nothing has run on an iPhone yet (see below). When a MAUI update
   fixes it, the job summary goes from 2 to 0. Step 02's rule still holds for ShelfScan's own
   code: 0 warnings.
4. **On iOS, the "may change the behavior" line proves nothing.** Step 10 used ILLink's
   *"Optimizing assemblies for size may change the behavior of the app"* to spot trim analysis
   that was switched off. On iOS both jobs print it, for different reasons:
   - Native AOT: ILLink runs first, then ILC compiles its output (the warnings above point at
     `obj/.../linked/Microsoft.Maui.dll`). The iOS SDK silences ILLink's trim warnings when Native
     AOT is on and turns them back on for ILC (`Xamarin.Shared.Sdk.Trimming.props`, target
     `_XamarinComputeIlcCompileInputs`). ILC is what reported the 2.
   - Mono (defaults): MAUI turns the analysis off. `Microsoft.Maui.Controls.Common.targets` sets
     `EnableTrimAnalyzer=false` and `SuppressTrimAnalysisWarnings=true` unless `PublishAot` is true
     or `TrimMode` is `full`, next to a FIXME that links to
     [xamarin-macios#21351](https://github.com/xamarin/xamarin-macios/pull/21351). MSBuild
     evaluates it before the iOS SDK's own defaults, which would have turned the analysis on
     (checked with `dotnet msbuild -pp`). So, as with step 10's Android defaults, this 0 means
     "not analyzed".

**Not covered here.** Nothing ran on an iPhone: no Vision OCR on a real photo, no camera
permission prompt, no EXIF check, no startup time. The `.ipa` is unsigned, so it can't be
installed as it is. The log also doesn't show where the Mono job spends its time: after
`IL stripping assemblies`, it's silent for about 8 minutes.

Result: both jobs are green, with 8 tests passing in each. Locally, `dotnet test` passes 8 tests,
and the solution builds with only the known no-Mac iOS warning.

## Step 12 · README (`step-12-docs`)

Files: `README.md` (new), `docs/build-log.md` (a fix in step 09, a note in step 11, this section).

The README is the short version of this log: what the app does, how to build and measure it,
the numbers from steps 10 and 11, and what was never tested. Each claim in it was checked
against the code and this log instead of written from memory. That caught three mistakes in the
first draft, and one in this log:

- **The query rule.** The draft said the query is the three tallest lines. The code keeps every
  line at least 40% as tall as the tallest one (step 05). Step 09's table said the same wrong
  thing ("the three tallest lines win"), and now states the real rule. Step 09's results couldn't
  show the difference, because both rules give the same query on the three test covers: two have
  three lines, and on the third the extra line, *A Novel*, is the smallest.
- **"Save it by hand" is a label, not a button.** It sits above a Title/Author form prefilled
  with the two biggest lines, and the button says **Save**.
- **"It runs on Android and iOS."** iOS has never run. The README says the app *targets* both,
  and says near the top that iOS is untested.

The README links to headings in this log (`#step-09--the-release-apk-on-an-emulator-…`). Their
ids were computed with [`github-slugger`](https://github.com/Flet/github-slugger), which
generates heading ids the way GitHub does, and all 5 links resolve.

**iOS, run again.** Step 11's sizes came from one run and its publish times from two. The merge
added two runs of the final commit: the pull request's last run, and the push to `main`. All four
runs used the same app code, SDK (10.0.303), workload set (10.0.303.1), Xcode (26.6) and runner
image (20260828.587). Sizes in bytes:

| ios-arm64 | [Run 2](https://github.com/Romain-OD/ShelfScan/actions/runs/36619196087) | [Run 3](https://github.com/Romain-OD/ShelfScan/actions/runs/36622502923) | [Run 4](https://github.com/Romain-OD/ShelfScan/actions/runs/36623986897) |
|---|---|---|---|
| Native AOT `.app` | 15,603,548 | 15,603,548 | 15,603,548 |
| Native AOT `.ipa` | 6,265,748 | 6,265,747 | 6,265,755 |
| Mono (defaults) `.app` | 47,291,085 | 47,291,085 | 47,291,085 |
| Mono (defaults) `.ipa` | 16,217,904 | 16,217,876 | 16,217,919 |

- The `.app` totals repeat to the byte, including run 2's, which had
  `-p:TrimmerSingleWarn=false`.
- The `.ipa`s move by up to 43 bytes. I didn't look into why, since no MB figure changes.
- [Run 1](https://github.com/Romain-OD/ShelfScan/actions/runs/36618205365) wrote its sizes only
  to the job summary, and the API doesn't return job summaries.

Publish step times:

| | Run 1 | Run 2 | Run 3 | Run 4 |
|---|---|---|---|---|
| Native AOT | 2 min 38 s | 1 min 57 s | 2 min 28 s | 3 min 29 s |
| Mono (defaults) | 9 min 29 s | 8 min 40 s | 9 min 23 s | 8 min 57 s |
| Native AOT faster by | 3.6× | 4.4× | 3.8× | 2.6× |

Run 4's Native AOT job was slower in every phase after restore (log timestamps, against run 3):

- compiling up to `ShelfScan.App.dll`: 94 s instead of 63 s
- ILLink: 36 s instead of 29 s
- native code through to the `.ipa`: 72 s instead of 43 s

The code, SDK and image were the same, which points at the machine, not the build. Each job
gets its own runner, so every ratio divides one machine's time by another's. Step 11's "about 4×"
is really 2.6× to 4.4×: Native AOT was faster every time, but not by a fixed factor. The README
gives the range.

Result: all four CI runs are green, with 8 tests passing in every job. Locally, `dotnet test`
passes 8 tests, and the solution builds with only the known no-Mac iOS warning.

## After step 12 · A logo of its own

Added after the twelve steps, in its own pull request, so there's no step tag.

Files: `ShelfScan.App/Resources/AppIcon/appicon.svg` and `appiconfg.svg` (redrawn),
`ShelfScan.App/Resources/Splash/splash.svg` (deleted), `ShelfScan.App/ShelfScan.App.csproj`.

Until now the app used the template's icon and splash: the .NET logo on #512BD4. The new logo
is a camera viewfinder around a shelf of books, crossed by a scan line. The background is a
violet gradient (#6C47F5 to #3B1BB0) around #512BD4, so the icon, the splash screen, the status
bar and the **Scan a book** button still belong together.

Decisions:

- **Drawn on Android's adaptive-icon grid.** `appiconfg.svg` has a 108-unit viewBox, like an
  adaptive icon layer (108 dp). Launchers crop that layer to a circle, a squircle or a square,
  and only a circle 66 units wide in the middle is never cut. Every shape fits in it: the
  farthest point, the outside of a bracket corner, is 32.6 units from the centre.
- **The same file is the themed icon.** MAUI writes
  `<monochrome android:drawable="@mipmap/appicon_foreground" />` into
  `mipmap-anydpi-v26/appicon.xml`, so on Android 13+ with themed icons on, the launcher paints
  the foreground in one colour and only its shape counts. A first draft had a translucent glow
  under the scan line, which came out as a smudge. Now every shape is opaque, with gaps between
  the books.
- **iOS scales the foreground up.** iOS shows the whole square, where the safe-zone artwork
  covers only 47% of the width. The docs present `ForegroundScale` as an Android option, but
  Resizetizer applies it to every icon it composites (`SkiaSharpAppIconTools.cs` in
  dotnet/maui), iOS included. So the `MauiIcon` item sets it for iOS only, as conditional
  metadata:

  ```xml
  <MauiIcon Include="Resources\AppIcon\appicon.svg" ForegroundFile="Resources\AppIcon\appiconfg.svg" Color="#512BD4">
    <ForegroundScale Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">1.3</ForegroundScale>
  </MauiIcon>
  ```

  Side by side, 1.25 still looked small and 1.4 crowded the rounded corners.
- **The splash screen uses the icon foreground.** The first version cropped the artwork so it
  filled the splash image. On the Android 16 emulator, the corners of the viewfinder were cut
  off. Since Android 12, the system splash screen masks its icon to a circle, the way launchers
  mask adaptive icons. MAUI's `Maui.SplashTheme` sets `android:windowSplashScreenAnimatedIcon`
  to its splash drawable, and on Android 12+ that drawable (`drawable-v31/maui_splash_image.xml`)
  stretches the splash image over 108 dp. So the splash needs the adaptive icon's safe zone too,
  and `MauiSplashScreen` now points at `appiconfg.svg` instead of a copy of it. `BaseSize` goes
  from 128 to 192 because iOS and older Android draw the image at that size, padding included:
  the viewfinder comes out about 89 points wide there, close to the 88 dp measured on the
  Android 16 splash.

Result: a clean `dotnet publish ShelfScan.App -f net10.0-android -c Release` has 0 warnings.
Screenshots from the Android 16 emulator show the new launcher icon, the themed home-screen
icon, and the whole viewfinder on the splash screen. Themed icons were switched on for the
check through the launcher's settings provider (`adb root`, then
`adb shell content update --uri content://com.google.android.apps.nexuslauncher.grid_control/icon_themed --bind boolean_value:b:true`),
and off again afterwards. On Windows, `dotnet build ShelfScan.App -f net10.0-ios -c Release`
generates the iOS icons with only the known no-Mac warning: the 1024 px App Store icon is fully
opaque and has the 1.3× foreground.

Size, measured against `main`: the Release APK grows by 92 KB (35.69 to 35.78 MB), and the iOS
`.app` by 123 KB in the pull request's CI run (Native AOT: `.app` 14.88 to 15.00 MB, `.ipa` 5.98
to 6.09 MB). Inside the APK, all of it is the icon and splash images, 41 KB before and 129 KB now:
a gradient and coloured shapes compress less than a flat colour with white letters, and the
splash is drawn at 192 instead of 128. Images don't depend on trimming or AOT, so every variant
gains the same amount (both iOS builds grew by exactly 126,096 bytes). The size comparisons from
steps 10 and 11 still hold, and the README keeps their figures.
