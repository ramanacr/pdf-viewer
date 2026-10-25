---
title: Microsoft Store build
permalink: /microsoft-store/
---

# Microsoft Store build

The application can ship through the Microsoft Store as an MSIX package. The Store signs the
package when you submit it, so this build needs no code signing certificate. Registration is free
for individual developers.

The installer (`PdfViewerSetup.exe`) and the portable `PdfViewer.exe` stay as they are. The Store
package is a third way to get the same application.

## What is in the repository

| File | What it does |
| --- | --- |
| `scripts/build_msix.ps1` | Publishes the application, generates the logos, writes the manifest, makes `resources.pri` and packs the `.msix`. |
| `eng/msix/AppxManifest.xml` | The manifest template. The script fills the identity and the version. |
| `eng/msix/identity.json` | The package identity from Partner Center. Empty until you register. |
| `.github/workflows/msix.yml` | Builds the `.msix` on each release tag and uploads it as a workflow artifact. It publishes nothing. |
| `src/PdfViewer/Services/PackagedApp.cs` | Tells the application that it runs from a package. |

## Steps for the owner

1. Register as an individual developer at <https://storedeveloper.microsoft.com>. Sign in with
   your Microsoft account and complete the account in Partner Center.
2. In Partner Center, select **Apps and games** > **New product** > **MSIX or PWA app**, and
   reserve the app name (for example "PDF Viewer Native").
3. Open the new app and go to **Product management** > **Product identity**. Copy these four
   values into `eng/msix/identity.json`:

   | Partner Center | `identity.json` |
   | --- | --- |
   | Package/Identity/Name | `identityName` |
   | Package/Identity/Publisher (starts with `CN=`) | `publisher` |
   | Package/Properties/PublisherDisplayName | `publisherDisplayName` |
   | The reserved app name | `displayName` |

   These values are public (they are in every package), so commit them.
4. Get the package. Use one of these:
   - Run the script on a Windows machine with the Windows SDK:
     ```powershell
     pwsh -File .\scripts\build_msix.ps1
     ```
     The package is `publish\msix\PdfViewer-<version>-x64.msix`.
   - Or push a release tag (`v1.2.3`). The **Microsoft Store package** workflow uploads the
     package as the artifact `PdfViewer-msix-v1.2.3`.
5. In Partner Center, start a submission. Fill in pricing, properties, age ratings and the Store
   listing. Under **Packages**, upload the `.msix`.
6. The application declares the `runFullTrust` restricted capability, as every desktop
   (WPF) application does. Partner Center asks why: write that it is a desktop PDF viewer
   that opens and saves files the user chooses.
7. Submit. The Store certifies the package, signs it and publishes it.

For a new version, push the next release tag and upload the new package in a new submission. The
package version comes from the tag (`v4.2.0` gives `4.2.0.0`). The Store requires the fourth
number to be 0.

## What is different in the Store version

| | Installer build | Store build |
| --- | --- | --- |
| Updates | The application can check GitHub (only if you allow it) and run the new installer. | The Store updates the application. **Help** > **Check for Updates** and the update question on first start are not shown. |
| .pdf association | The installer (or `PdfViewer.exe --register`) writes keys under `HKCU\Software\Classes`. | The package manifest declares it. Windows adds it on install and removes it on uninstall. `--register` and `--unregister` do nothing. Choose the default PDF app in **Settings** > **Apps** > **Default apps**. |
| Read Aloud | An optional download, checked against a pinned SHA-256. | Inside the package. Nothing is downloaded: the package folder is read-only, so a component cannot be added beside the application. The build checks the file against the same pinned hash. |
| .NET runtime | Uses the .NET 9 Desktop Runtime installed on the machine. | Self-contained: the runtime is inside the package. |
| Files | One single-file `PdfViewer.exe`. | Loose files. The Store downloads only the changed parts of each file on update. |
| Settings | `%LOCALAPPDATA%\PdfViewerNative` | The same path. Windows writes new files there into the package's own data folder, and uninstall removes them. |

## Local test

The script can build without a real identity:

```powershell
pwsh -File .\scripts\build_msix.ps1 -TestIdentity
```

This package has the identity `PdfViewer.LocalTest` and the publisher `CN=PdfViewer Local Test`.
You cannot submit it. To run it without a certificate, turn on Developer Mode and register the
unpacked layout:

```powershell
Add-AppxPackage -Register .\publish\msix\layout\AppxManifest.xml
# ... test from the Start menu ...
Get-AppxPackage PdfViewer.LocalTest | Remove-AppxPackage
```

To install the `.msix` file itself, Windows needs a signature that it trusts. The script signs
with a PFX that you give it (`-CertificatePfx`, `-CertificatePassword`); the certificate subject
must equal the publisher. Do not commit the certificate.
