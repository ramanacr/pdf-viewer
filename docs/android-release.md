---
title: "Android: Google Play release"
permalink: /android-release/
---

# Android: Google Play release

This page tells the owner how to publish the Android app (`src/PdfViewer.Android`,
application ID `io.github.ramanacr.pdfviewer`) on Google Play. The repository holds **no signing
key and no secret**. The owner makes the key on their own computer and gives it to GitHub as
secrets. GitHub builds the signed bundle; the owner uploads it in Play Console by hand.

## What the repository does

| Part | What it does |
|---|---|
| `src/PdfViewer.Android/PdfViewer.Android.csproj` | `net10.0-android`, so the app targets **API level 36**. A Release build makes an **Android App Bundle** (`.aab`); `-p:AndroidPackageFormat=apk` makes an APK. |
| `.github/workflows/android.yml` | On each change: builds an APK signed with a throwaway debug key, checks that there is no permission and that the target is API 36, checks the listing text lengths. Not for a store. |
| `.github/workflows/android-release.yml` | On a tag `android-v*` or a manual run: builds the **signed** `.aab` (for Play) and a signed universal `.apk` (for a GitHub release), in the protected environment `play`, with the upload key from secrets. It uploads both as a workflow artifact. **It never uploads to Google Play.** |
| `fastlane/metadata/android/en-US/` | The listing: title, short and full description, release notes, icon, feature graphic, phone screenshots. Google Play and F-Droid read the same files ([F-Droid and IzzyOnDroid](android-fdroid.md)). |
| `store/play/` | Play Console settings and the answers for the Data safety form and the content rating questionnaire. |

### Target API level

Google Play requires new apps and app updates to target **Android 16 (API level 36)** or higher
since 31 August 2026 ([Target API level requirements for Google Play apps](https://developer.android.com/google/play/requirements/target-sdk)).
The .NET 9 Android workload stops at API 35; `net10.0-android` targets API 36 by default
([Target frameworks in SDK-style projects](https://learn.microsoft.com/dotnet/standard/frameworks)).
So the app now builds with the **.NET 10 SDK and its `android` workload**. The workflows install
that workload on the runner.

To build on your own computer, install the workload for SDK 10 once (`dotnet workload install
android` with the .NET 10 SDK selected). Until you do, `-p:AndroidNet9=true` builds with the .NET 9
workload (API 35): good for `adb install` and the emulator, but Play refuses it.

Play also requires native libraries aligned for 16 KB memory pages. In the `net10.0-android` APK
from this branch's CI run (and in a local `net9.0-android` bundle), all 126 native libraries,
SkiaSharp 3.116.1 included, have 16 KB aligned segments. After the first upload, Play Console's
**App bundle explorer** shows the same check.

### Version numbers

| Value | Where it comes from |
|---|---|
| `versionName` (`ApplicationDisplayVersion`) | The tag without `android-v`: tag `android-v0.1` gives `0.1`. A manual run takes the `version_name` input. |
| `versionCode` (`ApplicationVersion`) | The `version_code` input of a manual run, if given (a positive whole number); otherwise the highest `N` of `fastlane/metadata/android/en-US/changelogs/N.txt`. |

The run fails if `changelogs/<versionCode>.txt` does not exist, so a code always has its release
notes. Play refuses a `versionCode` that it has seen before, and F-Droid and IzzyOnDroid need it to
grow too. So for each release, add the next file (`2.txt`, `3.txt`, ...) with its notes, then tag.

## Steps for the owner

Do these in order. Steps 1 to 3 happen once.

### 1. Make the upload key on your own computer

Use the `keytool` of any JDK 17 or later. Keep the file and both passwords in your password
manager and in a safe backup. **Never commit the keystore** and never paste it in an issue or a
chat.

```powershell
keytool -genkeypair -v -keystore pdfviewer-upload.keystore -alias upload `
  -keyalg RSA -keysize 4096 -validity 10000 `
  -dname "CN=Ramana Reddy Chamakura, O=PDF Viewer, C=IN"
```

`keytool` asks for the keystore password and the key password (with a PKCS12 keystore, the
default, use the same password for both). Then make the Base64 text for GitHub:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("pdfviewer-upload.keystore")) | Set-Clipboard
```

### 2. Make the `play` environment and its four secrets

1. On GitHub: **Settings → Environments → New environment**, name it `play`.
2. Under **Deployment protection rules**, tick **Required reviewers** and add yourself. Under
   **Deployment branches and tags**, choose **Selected branches and tags** and add the tag rule
   `android-v*` (and `main`, for manual runs).
3. Under **Environment secrets**, add:

   | Secret | Value |
   |---|---|
   | `ANDROID_UPLOAD_KEYSTORE_BASE64` | the Base64 text from step 1 |
   | `ANDROID_UPLOAD_KEYSTORE_PASSWORD` | the keystore password |
   | `ANDROID_UPLOAD_KEY_ALIAS` | `upload` |
   | `ANDROID_UPLOAD_KEY_PASSWORD` | the key password |

The workflow writes the keystore only to the runner's temporary folder (`$RUNNER_TEMP`) and deletes
it in a step that always runs. The passwords reach the build through environment variables
(`env:` in `AndroidSigningStorePass` and `AndroidSigningKeyPass`), never on a command line.

### 3. Create the app in Play Console and turn on Play App Signing

1. Finish the account verification (phone number) in Play Console.
2. **Create app**: name from `fastlane/metadata/android/en-US/title.txt`, default language
   English (United States), **App**, **Free**. Accept the declarations.
3. Play App Signing is on by default for new apps: Google keeps the **app signing key**, and your
   key from step 1 is the **upload key**. Keep the default ("Use a Google-generated key"). You
   only register your upload key by uploading the first bundle signed with it (step 5).
4. Fill in **App content** and **Store listing** from [`store/play/`](https://github.com/ramanacr/pdf-viewer/tree/main/store/play)
   and `fastlane/metadata/android/en-US/`: privacy policy URL, ads (none), app access (no
   restrictions), content rating, target audience, data safety, and the listing text and images.

### 4. Build the signed bundle

Either push a tag:

```powershell
git tag android-v0.1
git push origin android-v0.1
```

or run **Actions → Android release (Google Play bundle) → Run workflow** with `version_name` and,
if you want, `version_code`. Approve the run when GitHub asks (the `play` environment). When it is
green, download the artifact `pdf-viewer-android-<name>-<code>`. It holds:

- `pdf-viewer-<name>-<code>.aab`: the bundle for Play.
- `pdf-viewer-<name>-<code>.apk`: a universal APK signed with the upload key, for a GitHub release.
- `SHA256SUMS.txt`.

The run summary shows the SHA-256 fingerprint of the upload certificate.

### 5. Upload to Play Console

A new **personal** developer account must first run a **closed test with at least 12 testers who
stay opted in for at least 14 days in a row** before it can apply for production access
([App testing requirements for new personal developer accounts](https://support.google.com/googleplay/android-developer/answer/14151465)).

1. **Test and release → Testing → Closed testing → Create track** (or use the default
   "Alpha" track). Add a list of testers (at least 12 Google accounts).
2. **Create new release**, upload the `.aab`, paste the release notes from
   `changelogs/<versionCode>.txt`, review and roll out to the closed track.
3. Share the opt-in link with the testers. Keep at least 12 of them opted in for 14 days.
4. Then, in the Dashboard, **Apply for production** and answer the questions about the test.
5. After approval: **Production → Create new release**, upload the bundle (or promote the tested
   release).

For each later release: add `changelogs/<next code>.txt`, update the description if features changed,
push a new tag (`android-v0.2`), approve the run, upload the new `.aab`.

### If the upload key is lost or leaked

Because Google holds the app signing key, a lost upload key is not the end of the app: ask for an
upload key reset in **Play Console → Test and release → App integrity → Play app signing**, make a
new key as in step 1, and replace the four secrets. The GitHub-release APK would then have a new
signature, so IzzyOnDroid would need to be told (see [F-Droid and IzzyOnDroid](android-fdroid.md)).
