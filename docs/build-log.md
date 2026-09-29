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
