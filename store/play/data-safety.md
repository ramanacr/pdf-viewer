# Data safety form and content rating: answers

Answers for Play Console's **App content → Data safety** form and the **Content rating** (IARC)
questionnaire, for version 0.1 (versionCode 1) of `io.github.ramanacr.pdfviewer`. Each answer
says where in the code it comes from. Check it again when the app gets a permission, a network
call or a library.

## What the code does (evidence)

| Fact | Evidence |
|---|---|
| No permission at all | `src/PdfViewer.Android/AndroidManifest.xml` has no `uses-permission`. The `net10.0-android` Release APK built by CI for PR #136 (`android.yml`, run 40671291592, 28 October 2026) was checked with `aapt dump permissions` and `aapt dump badging` (build-tools 36.0.0): no permission, `targetSdkVersion:'36'`, `sdkVersion:'24'`, `versionCode='1'`, not debuggable. CI (`android.yml`) and the release workflow fail if one appears. |
| No network use | Without `android.permission.INTERNET` the app cannot open a socket. There is no `HttpClient`, `WebRequest`, `Socket` or `TcpClient` in `src/PdfViewer.Android`, `src/PdfViewer.Web.Engine`, `src/PdfEngine.Vector`, `src/PdfEngine.Vector.Skia` or `src/PdfEngine.Abstractions`. `android:usesCleartextTraffic="false"`. |
| No analytics, ads, crash reporting or third-party SDK | Package references: `SkiaSharp.NativeAssets.Android` (drawing) and the project's own engine libraries only. `EventSourceSupport` is off. |
| Files | Opened through the system picker (`ACTION_OPEN_DOCUMENT`) or handed over by another app (`ACTION_VIEW`); a copy is written only through the system save dialog (`ACTION_CREATE_DOCUMENT`). |
| Data kept on the device | `files/recent.json` in the app's private storage: up to 12 entries with the document's location (content URI), file name, page and zoom. No document text, no passwords. Removed when the app is uninstalled or its storage is cleared; long press removes one entry. |
| Backups | `android:allowBackup="false"`: nothing goes to Google's cloud backup. |
| Share | **⋮ → Share** opens the system share sheet with the document's own URI; the user picks the receiving app. The app itself sends nothing. |
| Accounts | None. |

## Data safety form

| Question | Answer |
|---|---|
| Does your app collect or share any of the required user data types? | **No** |
| (Because of "No", the questions about encryption in transit and deletion requests are not asked.) | |

Reasons:

1. **Collection** in Play's sense means data sent off the device. The app has no internet
   permission and no network code, so it sends nothing. Data that stays on the device (the recent
   files list) is not "collected".
2. **Sharing** with the share sheet is started by the user, who picks the receiving app. Play does
   not count user-initiated transfers that the user expects as sharing.
3. No SDK in the app collects data.

Result shown on the store page: "No data collected", "No data shared with third parties".

## Content rating questionnaire (IARC)

| Question | Answer |
|---|---|
| Email address for the rating | the owner's contact address |
| Category | **All Other App Types** (a utility / productivity app; not a game, not social, not a reference or news app) |
| Violence, blood, gore | No |
| Fear, horror | No |
| Sexuality, nudity | No |
| Language (profanity, crude humour) | No |
| Controlled substances (drugs, alcohol, tobacco) | No |
| Gambling, simulated gambling | No |
| Does the app let users interact or exchange content with other users? | No (the share sheet hands a file to another app; there is no chat, no feed, no account) |
| Does the app share the user's current physical location with other users? | No |
| Does the app let users buy digital goods? | No |
| Does the app contain or show user-generated content from others, or unrestricted internet content? | No (it shows only files that the user opens from their own device; no web browser, no internet) |
| Is the app a web browser or search engine? | No |
| Miscellaneous: does the app show ads? | No |

Expected rating: Everyone / PEGI 3 / USK 0 (to be confirmed by the questionnaire's result).

## Other checks in Play Console

- **Permissions declaration**: not needed (no sensitive permission).
- **Foreground service, exact alarm, all-files access, accessibility**: not used.
- **Account deletion**: not applicable (no accounts).
