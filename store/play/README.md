# Google Play: store settings

The listing **text and images** are in [`fastlane/metadata/android/en-US/`](../../fastlane/metadata/android/en-US/),
the one source that Google Play (copied in by hand) and F-Droid / IzzyOnDroid (read directly) use.
This folder holds the Play Console settings that are not in that layout. How to release:
[docs/android-release.md](../../docs/android-release.md).

## Main store listing

| Play Console field | Value | Source |
|---|---|---|
| App name (30) | `PDF Viewer - Fill & Annotate` (28) | `fastlane/.../title.txt` |
| Short description (80) | 75 characters | `fastlane/.../short_description.txt` |
| Full description (4000) | about 2150 characters | `fastlane/.../full_description.txt` |
| App icon, 512 × 512 PNG | `images/icon.png` | made from `assets/app_icon.png` by `scripts/android_store_images.ps1` |
| Feature graphic, 1024 × 500 PNG | `images/featureGraphic.png` | the same script |
| Phone screenshots (2 to 8) | `images/phoneScreenshots/01…06.png`, 1080 × 2160 | Android 13 emulator (Pixel 3a), lossless PNG |
| Release notes, 0.1 (500) | `changelogs/1.txt` (versionCode 1) | |

The screenshots were taken at the emulator's 1080 × 2220 and cut to 1080 × 2160 (the bottom 60
pixels, the gesture bar), because Play allows at most 2:1 between the long and the short side. The
document in them was written in code for the screenshots.

## Store settings

| Field | Value |
|---|---|
| App or game | App |
| Free or paid | Free |
| Category | **Productivity** (alternative: Tools) |
| Tags (up to 5) | PDF viewer, PDF editor, Document viewer, Forms |
| Email address | a contact address that the owner reads (Play shows it publicly) |
| Website | https://ramanacr.github.io/pdf-viewer/ |
| Privacy policy | https://ramanacr.github.io/pdf-viewer/privacy/ |

The privacy policy URL is the site's base URL (GitHub Pages project site, `ramanacr.github.io/pdf-viewer`)
plus the page's `permalink: /privacy/` (`docs/privacy.md`); it returns HTTP 200. It has an Android
section.

## App content (other declarations)

| Declaration | Answer |
|---|---|
| Privacy policy | the URL above |
| Ads | No, the app does not contain ads |
| App access | All functionality is available without special access (no login) |
| Content rating | see [data-safety.md](data-safety.md#content-rating-questionnaire-iarc) |
| Target audience | 18 and over (a general document tool; not designed for children, so the Families policy does not apply) |
| Data safety | see [data-safety.md](data-safety.md) |
| Government app | No |
| Financial features | None |
| Health | No health features |
| News app | No |
| Advertising ID | No (the app does not use it; it has no permission at all) |

## Before the first upload, check

1. The icon. `assets/app_icon.png` came in as "Flaticon 4649604" (commit 4ee2289), and
   `THIRD_PARTY_NOTICES.md` has no entry for it. Confirm that its licence allows use as an app
   icon in a store (Flaticon's free licence asks for attribution), and add the notice or replace
   the icon. The same icon is already used by the Microsoft Store package.
2. The launcher label in the app is still "PDF Viewer (preview)". Play shows the store name above;
   the label on the phone is set in `PdfViewer.Android.csproj` (`ApplicationTitle`) and
   `AndroidManifest.xml`.
