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
