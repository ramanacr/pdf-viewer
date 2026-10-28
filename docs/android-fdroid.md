---
title: "Android: F-Droid and IzzyOnDroid"
permalink: /android-fdroid/
---

# Android: F-Droid and IzzyOnDroid

Can the Android app (`io.github.ramanacr.pdfviewer`, .NET for Android, MIT licence) be offered
through F-Droid? This page gives the finding of 28 October 2026, the reasons with links, and the
steps for each route. Nothing was submitted anywhere.

## Finding

| Route | Feasible now? | Why |
|---|---|---|
| **F-Droid main repository** (built from source by F-Droid) | **No** | F-Droid's build server cannot build a .NET for Android app under its inclusion policy, and no .NET app has been accepted yet. |
| **IzzyOnDroid** (takes the developer's signed APK from GitHub releases) | **Technically yes, by policy no** | The APK meets the technical rules, but IzzyOnDroid rejects apps whose code is fully or partly written with generative AI tools, and this project's code is (its commits say so). |
| **Own F-Droid repository** or **Obtainium** | **Yes** | No third-party inclusion policy: users add the owner's repository, or point Obtainium at the GitHub releases. |

The listing is already in the layout both F-Droid and IzzyOnDroid read
(`fastlane/metadata/android/en-US/`), and the release workflow already makes the signed APK that
a GitHub release needs. So if either policy changes, little work is left.

## F-Droid main repository: why not

1. **The toolchain must be free and built from source or come from Debian.** The
   [Inclusion Policy](https://f-droid.org/docs/Inclusion_Policy/) asks for a 100% FLOSS toolchain,
   binary dependencies built from source or downloaded from Debian, and allows official prebuilt
   binaries only for a short list (such as the Android SDK and the Flutter SDK). The .NET SDK is
   not packaged in Debian and is not on that list.
2. **The android workload is prebuilt, and source-built SDKs do not have it.** `dotnet workload
   install android` downloads the .NET for Android packs (`Microsoft.Android.Sdk.*`) as prebuilt
   NuGet packages from Microsoft's feeds. The source code is MIT-licensed
   ([dotnet/android](https://github.com/dotnet/android)), but F-Droid would need to build it.
   Source-built SDKs, such as Ubuntu's, "do not have all of the same workloads as
   Microsoft-authored SDKs" ([dotnet/sdk#38171](https://github.com/dotnet/sdk/issues/38171), closed
   as not planned).
3. **No precedent.** In the F-Droid forum thread
   [Why isn't C# .NET MAUI supported?](https://forum.f-droid.org/t/why-isnt-c-net-maui-supported/24842)
   (2024 to 2026), a team member said there is no .NET app in F-Droid, so .NET is not on the
   exception list, and that they do not want more exceptions. In March 2024 the team agreed that
   Ubuntu's (source-built) .NET package would be good enough, but the attempts on record stopped at
   prebuilt workloads. Posts from 2025 and 2026 ask for an exception for the Android workload; no
   answer is shown.
4. **Other prebuilt parts.** `SkiaSharp.NativeAssets.Android` brings a prebuilt `libSkiaSharp.so`
   from nuget.org. F-Droid would also need it built from the Skia and SkiaSharp sources.
5. **The icon's licence is not recorded.** `assets/app_icon.png` came from Flaticon (commit
   4ee2289) and has no entry in `THIRD_PARTY_NOTICES.md`. A non-free icon gets the
   [NonFreeAssets](https://f-droid.org/docs/Anti-Features/) anti-feature on F-Droid. Record its
   licence or replace it with a freely licensed icon, for every route.

The [Build Metadata Reference](https://f-droid.org/docs/Build_Metadata_Reference/) has the means
(`sudo` to install packages, `srclibs`, `prebuild`, `build`), so the blocker is policy and the
prebuilt workload, not the metadata format.

### If F-Droid accepts .NET later: draft metadata

A draft for `metadata/io.github.ramanacr.pdfviewer.yml` in
[fdroiddata](https://gitlab.com/fdroid/fdroiddata). It is not valid for F-Droid today, because
the `dotnet workload install` step downloads prebuilt packs.

```yaml
Categories:
  - Reading
License: MIT
AuthorName: Ramana Reddy Chamakura
WebSite: https://ramanacr.github.io/pdf-viewer/
SourceCode: https://github.com/ramanacr/pdf-viewer
IssueTracker: https://github.com/ramanacr/pdf-viewer/issues

AutoName: PDF Viewer

RepoType: git
Repo: https://github.com/ramanacr/pdf-viewer.git

Builds:
  - versionName: '0.1'
    versionCode: 1
    commit: android-v0.1
    subdir: src/PdfViewer.Android
    sudo:
      - apt-get update
      - apt-get install -y dotnet-sdk-10.0   # Ubuntu's source-built SDK; not in Debian
    output: bin/Release/net10.0-android/io.github.ramanacr.pdfviewer.apk
    build:
      - dotnet workload install android      # prebuilt packs from Microsoft: the blocker
      - dotnet build -c Release -f net10.0-android -p:AndroidPackageFormat=apk
          -p:AndroidSdkDirectory=$ANDROID_HOME

AutoUpdateMode: Version
UpdateCheckMode: Tags ^android-v.*$
CurrentVersion: '0.1'
CurrentVersionCode: 1
```

Before an F-Droid request, ask in the forum thread above whether a .NET for Android app can be
built, and link this project as a real case.

## IzzyOnDroid: what it needs, and the blocker

IzzyOnDroid ships the developer's own signed APKs from GitHub releases
([About IzzyOnDroid](https://apt.izzysoft.de/fdroid/index/info)). Its
[App Inclusion Policy](https://izzyondroid.org/docs/general/AppInclusionPolicy/) asks for:

| Rule | This app |
|---|---|
| FOSS licence (OSI/FSF), source freely accessible | MIT, GitHub |
| APK attached to a GitHub release (tag) | The release workflow makes it; the owner attaches it to a release |
| Signed with a release key, not debuggable, not `testOnly` | Signed with the upload key; Release build, not debuggable. The workflow fails on a debug certificate. |
| The same signing key for every update | The upload key (keep it; see [Google Play release](android-release.md#if-the-upload-key-is-lost-or-leaked)) |
| APK of at most about 30 MB | About 25 MB (arm64 and x86-64 in one APK) |
| Listing in the fastlane layout in the repository | `fastlane/metadata/android/en-US/` |
| No ads or trackers; avoid cleartext traffic | None; `usesCleartextTraffic="false"`, no internet permission |
| Updated within the last 12 months | Yes |
| **No code written fully or partly with generative AI** | **Not met.** The policy rejects such apps, rejects "vibe-coded" apps, and can reject projects that are not open about AI use. 394 of the 420 non-merge commits in this repository (28 October 2026) carry a `Co-Authored-By: Claude` line. |

Reproducible builds are not required; IzzyOnDroid shows the result of its own verification builds
as a badge.

**Do not hide the AI use** to get in: the policy itself says such projects can be moved to the
rejected state. If the owner still wants to ask, the request must say so. Draft text for an issue in
[IzzyOnDroid/repodata](https://codeberg.org/IzzyOnDroid/repodata/issues) (use its "app inclusion"
template):

```text
App: PDF Viewer (io.github.ramanacr.pdfviewer)
Source: https://github.com/ramanacr/pdf-viewer (MIT)
APK: attached to GitHub releases tagged android-v*, signed with a release key
Metadata: fastlane/metadata/android/en-US/
Permissions: none. No trackers, no ads, no network access.
Disclosure: much of the code was written with an AI coding assistant (Claude); the commits are
marked "Co-Authored-By: Claude". I review and test every change. I understand that your policy
may not accept this.
```

## What a GitHub release for Android must contain

Whatever route is used (IzzyOnDroid, Obtainium, an own repository, sideloading):

1. A tag `android-v<versionName>` (for example `android-v0.1`), made by the owner.
2. The **signed release APK** from the `android-release.yml` artifact
   (`pdf-viewer-<versionName>-<versionCode>.apk`), attached to a GitHub release on that tag, and
   `SHA256SUMS.txt`. Never a debug-signed APK from `android.yml`.
3. A **versionCode that is larger** than in every earlier release (the workflow uses the run
   number or the `version_code` input), and `changelogs/<versionCode>.txt` in the repository.
4. The **same signing key** every time. A different key makes Android refuse the update and makes
   IzzyOnDroid hold the release.

Note: the APK on GitHub is signed with the upload key; the app from Google Play is signed with
Google's app signing key. Android does not update one with the other: a user must uninstall
before switching between Play and the GitHub APK.

## Own F-Droid repository or Obtainium

- **Obtainium** installs and updates apps straight from GitHub releases; users add
  `https://github.com/ramanacr/pdf-viewer` and filter on APK assets. It needs only the GitHub
  release above.
- An **own F-Droid repository** made with `fdroidserver` (`fdroid init`, put the signed APK in
  `repo/`, `fdroid update`) can be hosted on GitHub Pages; users add its URL in the F-Droid
  client. It uses the same APK and the fastlane metadata. It needs its own repository signing key,
  kept outside this repository like the upload key.
