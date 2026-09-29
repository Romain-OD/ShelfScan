<#
.SYNOPSIS
    Android APK size and cold start: the .NET defaults vs what ShelfScan ships.

.DESCRIPTION
    Publishes each variant clean, then measures cold starts in rounds: every round
    reinstalls every variant, launches it once to warm up, then -Runs times with
    `am start -S -W` (force-stop, launch, wait for the first frame). TotalTime is
    Android's time to initial display (TTID). Emulator timings are not phone timings:
    compare the variants with each other, not with a phone.

.EXAMPLE
    .\scripts\measure-android.ps1 -Adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
#>
param(
    [string]$Adb = 'adb',
    [int]$Rounds = 4,
    [int]$Runs = 5
)

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\measure'
$package = 'dev.romain.shelfscan'
New-Item -ItemType Directory -Force $out | Out-Null

# The csproj ships full trim + profiled AOT (the Release default); the other variants change
# that from the command line. IsAotCompatible=false for the defaults: it implies IsTrimmable,
# and partial mode trims every assembly marked trimmable, so ours would be trimmed anyway.
$variants = @(
    @{ Id = 'defaults'; Name = 'Defaults: partial trim, profiled AOT'; Props = '-p:TrimMode=partial', '-p:IsAotCompatible=false' }
    @{ Id = 'jit'; Name = 'Full trim, no AOT (JIT)'; Props = @('-p:RunAOTCompilation=false') }
    @{ Id = 'profiled-aot'; Name = 'Full trim, profiled AOT (ShelfScan)'; Props = @() }
    @{ Id = 'full-aot'; Name = 'Full trim, full AOT'; Props = @('-p:AndroidEnableProfiledAot=false') }
)

# A plain Release publish, clean: an AAB, plus the universal APK bundletool makes from it.
$builds = foreach ($v in $variants) {
    Write-Host "Publishing: $($v.Name)"
    foreach ($project in 'ShelfScan.App', 'ShelfScan.Core') {
        Remove-Item "$root\$project\bin\Release", "$root\$project\obj\Release" -Recurse -Force -ErrorAction SilentlyContinue
    }
    $log = Join-Path $out "$($v.Id).log"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    dotnet publish "$root\ShelfScan.App" -f net10.0-android -c Release -tl:off -v:m -nologo $v.Props *> $log
    if ($LASTEXITCODE) { throw "Publish failed, see $log" }
    $apk = Join-Path $out "$($v.Id).apk"
    Copy-Item "$root\ShelfScan.App\bin\Release\net10.0-android\publish\$package-Signed.apk" $apk
    $warnings = @(Select-String $log -Pattern ': warning ' | ForEach-Object Line | Sort-Object -Unique).Count
    # The SDK prints this only when trim warnings are suppressed: partial trim without IsAotCompatible.
    if (Select-String $log -Pattern 'may change the behavior of the app' -Quiet) { $warnings = "$warnings (trim analysis off)" }
    [pscustomobject]@{
        Name           = $v.Name
        Apk            = $apk
        Bytes          = (Get-Item $apk).Length
        PublishSeconds = [int]$clock.Elapsed.TotalSeconds
        Warnings       = $warnings
        Times          = [Collections.Generic.List[int]]::new()
    }
}

# Rounds, so a slow patch on the host hits every variant, not just one.
foreach ($round in 1..$Rounds) {
    foreach ($b in $builds) {
        Write-Host "Round $round of ${Rounds}: $($b.Name)"
        & $Adb uninstall $package *> $null  # fails harmlessly when it isn't installed
        & $Adb install $b.Apk *> $null
        if ($LASTEXITCODE) { throw "adb install $($b.Apk) failed" }
        $activity = (& $Adb shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER $package | Select-Object -Last 1).Trim()
        foreach ($run in 0..$Runs) {
            $lines = & $Adb shell am start -S -W -n $activity
            if (-not ($lines -match '^LaunchState: COLD')) { throw "Not a cold start:`n$($lines -join "`n")" }
            if ($run) { $b.Times.Add([int](($lines -match '^TotalTime:')[0] -replace '\D')) }  # run 0 warms up
            Start-Sleep -Seconds 3
        }
    }
}

$device = "$(& $Adb shell getprop ro.product.model), Android $(& $Adb shell getprop ro.build.version.release)"
''
"Device: $device. Cold start: median of $($Rounds * $Runs) launches per variant, in $Rounds rounds (am start -S -W, TotalTime)."
''
'| Variant | APK | Publish | Warnings | Cold start, median (min-max) |'
'|---|---|---|---|---|'
foreach ($b in $builds) {
    $sorted = @($b.Times | Sort-Object)
    $median = ($sorted[[math]::Floor(($sorted.Count - 1) / 2)] + $sorted[[math]::Ceiling(($sorted.Count - 1) / 2)]) / 2
    "| $($b.Name) | $([math]::Round($b.Bytes / 1MB, 2)) MB ($($b.Bytes) bytes) | $($b.PublishSeconds) s | $($b.Warnings) | $median ms ($($sorted[0])-$($sorted[-1])) |"
}
''
foreach ($b in $builds) { "$($b.Name): $($b.Times -join ' ') ms" }
