using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Widget;
using PdfEngine.Geometry;
using Android.Views;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Signatures;
using PdfViewer.Web.Engine;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Sign (a PAdES signature with a certificate file the reader picks, .pfx or .p12, written as an
/// incremental update into a new copy) and Verify signatures (integrity, the signer's chain against
/// this device's trusted roots, what changed after each signature). Both work offline: no timestamp
/// server is asked (the app has no internet permission) and revocation is judged only from what the
/// document holds. The certificate file and its password are read for the one signature and never
/// kept: nothing about them is stored or logged.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>A certificate file larger than this is not a certificate file.</summary>
    private const long MaxCertificateFileBytes = 1024 * 1024;

    /// <summary>The size of a visible signature, in points, before it is fitted to the page.</summary>
    private const double SignatureWidth = 200, SignatureHeight = 60, SignatureMargin = 28;

    // ------------------------------------------------------------------ sign

    /// <summary>Sign: the certificate file, its password, then the signature's options; the signed copy is saved.</summary>
    private void SignDocument()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        if (!doc.CanFillForms)
        {
            // Signing fills a signature field: the permissions and a certification must allow it.
            Tell("Sign", doc.EditRestriction ?? "This document's permissions do not allow signing it.");
            return;
        }
        Toast.MakeText(this, "Choose your certificate file (.pfx or .p12).", ToastLength.Short)?.Show();
        PickFiles(["application/x-pkcs12", "application/pkcs12", "*/*"], false, uris =>
        {
            if (!ReferenceEquals(doc, _doc)) return;
            AskLine("Certificate password", "The password of the certificate file. It is used for this signature only and not kept.", "", "Password",
                InputTypes.ClassText | InputTypes.TextVariationPassword, "Continue", password => _ = LoadCertificateAsync(doc, uris[0], password));
        });
    }

    /// <summary>Reads the certificate file and its private key on a worker thread; then the options.</summary>
    private async Task LoadCertificateAsync(ViewerDocument doc, Uri file, string password)
    {
        var certificate = await RunBusyAsync("Opening the certificate…", _ =>
        {
            byte[] pfx = ReadUri(file, MaxCertificateFileBytes);
            X509Certificate2 loaded;
            try
            {
                loaded = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
            }
            catch (CryptographicException)
            {
                throw new UserMessageException("The password is not correct, or this is not a certificate file (.pfx or .p12).");
            }
            finally
            {
                Array.Clear(pfx);
            }
            if (!loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new UserMessageException("This certificate has no private key, so it cannot sign. Use the .pfx or .p12 file that holds your key.");
            }
            return loaded;
        });
        if (certificate == null) return;
        if (!ReferenceEquals(doc, _doc))
        {
            certificate.Dispose();
            return;
        }
        ShowSignOptions(doc, certificate);
    }

    /// <summary>
    /// The signature's options: reason, location, and where it shows (a corner of a page, or
    /// nowhere). The certificate is disposed once the signature is made or the dialog is left.
    /// </summary>
    private void ShowSignOptions(ViewerDocument doc, X509Certificate2 certificate)
    {
        string signer = CertificateName(certificate);
        var column = DialogColumn();
        string issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        bool expired = certificate.NotAfter < DateTime.Now, early = certificate.NotBefore > DateTime.Now;
        column.AddView(DialogNote($"Signing as {signer}, issued by {(issuer.Length > 0 ? issuer : "an unnamed issuer")}, " +
                                  $"valid until {certificate.NotAfter.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}." +
                                  (expired ? "\n⚠ This certificate has expired: readers will not accept the signature." : "") +
                                  (early ? "\n⚠ This certificate is not valid yet." : "")));
        var reason = new EditText(this) { Hint = "Reason (optional), e.g. I approve this document", InputType = InputTypes.ClassText | InputTypes.TextFlagCapSentences };
        reason.SetSingleLine(true);
        reason.SetMinHeight((int)Dp(48));
        var location = new EditText(this) { Hint = "Location (optional), e.g. a city", InputType = InputTypes.ClassText | InputTypes.TextFlagCapWords };
        location.SetSingleLine(true);
        location.SetMinHeight((int)Dp(48));
        column.AddView(reason);
        column.AddView(location);

        var placement = new RadioGroup(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        string[] places = ["Visible, bottom right", "Visible, bottom left", "Visible, top right", "Visible, top left", "Invisible (only in the signature list)"];
        for (int i = 0; i < places.Length; i++)
        {
            var radio = new RadioButton(this) { Text = places[i], Id = 1000 + i };
            radio.SetMinHeight((int)Dp(48));
            placement.AddView(radio);
        }
        placement.Check(1000);
        column.AddView(placement);
        int pageCount = doc.PageCount;
        var page = new EditText(this)
        {
            Text = Math.Clamp(_pages.CurrentPage, 1, Math.Max(1, pageCount)).ToString(CultureInfo.InvariantCulture),
            Hint = $"Page (1 to {pageCount})",
            InputType = InputTypes.ClassNumber,
        };
        page.SetSingleLine(true);
        page.SetMinHeight((int)Dp(48));
        page.ContentDescription = "Page the signature shows on";
        column.AddView(DialogNote("On page:"));
        column.AddView(page);
        // A picture beside the details (a drawn or scanned handwritten signature, a seal), as on Windows.
        PdfImageContent? picture = null;
        var pictureNote = DialogNote("Picture: none (the signature shows its details only).");
        var pictureRow = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        Button PictureButton(string text, string description, Action run)
        {
            var b = new Button(this) { Text = text, ContentDescription = description };
            b.SetAllCaps(false);
            b.SetMinHeight((int)Dp(48));
            b.Click += (_, _) => run();
            pictureRow.AddView(b);
            return b;
        }
        Button? removePicture = null;
        void SetPicture(PdfImageContent? chosen, string what)
        {
            picture = chosen;
            pictureNote.Text = chosen == null ? "Picture: none (the signature shows its details only)." : $"Picture: {what}, shown left of the details.";
            if (removePicture != null) removePicture.Enabled = chosen != null;
        }
        PictureButton("Draw…", "Draw your signature with your finger", () => DrawSignature(drawn => SetPicture(drawn, "drawn here")));
        PictureButton("Picture…", "Use a picture of your signature or a seal", () => PickFiles(["image/*"], false, async uris =>
        {
            var chosen = await RunBusyAsync("Reading the picture…", _ => ReadContentPicture(uris[0]));
            if (chosen != null) SetPicture(chosen, "from the device");
        }));
        removePicture = PictureButton("Remove", "Use no picture", () => SetPicture(null, ""));
        removePicture.Enabled = false;
        column.AddView(pictureNote);
        column.AddView(pictureRow);
        placement.CheckedChange += (_, e) =>
        {
            bool shown = e.CheckedId != 1000 + places.Length - 1;
            page.Enabled = shown;
            pictureNote.Visibility = pictureRow.Visibility = shown ? ViewStates.Visible : ViewStates.Gone;
        };
        column.AddView(DialogNote("No timestamp is added: this app has no internet access, so the time is this device's clock."));
        column.AddView(DialogNote("The signed copy is a new file. Keep it unchanged: any later change shows when its signature is checked."));

        bool used = false;
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Sign")!
            .SetView(DialogScroll(column))!
            .SetPositiveButton("Sign and save", (_, _) => { })! // replaced below, so a refused entry keeps the dialog open
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        // Left without signing: the key goes now, not when the collector gets to it.
        dialog.DismissEvent += (_, _) =>
        {
            if (!used) certificate.Dispose();
        };
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            bool visible = placement.CheckedRadioButtonId != 1000 + places.Length - 1;
            int corner = placement.CheckedRadioButtonId - 1000;
            int pageNumber = 1;
            if (visible && (!int.TryParse(page.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pageNumber) || pageNumber < 1 || pageNumber > pageCount))
            {
                page.Error = $"A page from 1 to {pageCount}.";
                return;
            }
            if (!ReferenceEquals(doc, _doc) || !EditsWritten()) return;
            used = true;
            dialog.Dismiss();
            var request = new PdfSignatureRequest
            {
                SignerName = signer,
                Reason = (reason.Text ?? "").Trim() is { Length: > 0 } r ? r : null,
                Location = (location.Text ?? "").Trim() is { Length: > 0 } l ? l : null,
                PageNumber = pageNumber,
                SigningTime = DateTimeOffset.Now,
                Appearance = visible && picture != null
                    ? new PdfSignatureAppearance { Layout = PdfSignatureLayout.ImageAndText, Image = picture, Fonts = PdfEngine.Vector.Fonts.SystemFontCatalog.Installed }
                    : null,
            };
            byte[] bytes = CurrentBytes()!;
            // The certificate goes with the signature (if the save dialog is cancelled, the collector takes it).
            SaveNewPdf(BaseName + " (signed).pdf", "Signing…",
                "The signed copy is saved. Keep it unchanged: a later change shows when the signature is checked.", token =>
                {
                    try
                    {
                        using var engine = OpenForTool(bytes);
                        var placed = visible ? request with { Rect = CornerRect(engine, pageNumber, corner) } : request;
                        return Sign(engine, bytes, placed, certificate, token);
                    }
                    finally
                    {
                        certificate.Dispose();
                    }
                });
        };
    }

    /// <summary>The signature pad: sign with a finger, then Use (or Clear, or Cancel).</summary>
    private void DrawSignature(Action<PdfImageContent> drawn)
    {
        var pad = new SignaturePadView(this);
        var frame = new FrameLayout(this);
        frame.SetPadding((int)Dp(16), (int)Dp(8), (int)Dp(16), 0);
        frame.AddView(pad, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)Dp(200)));
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Sign here")!
            .SetView(frame)!
            .SetPositiveButton("Use", (_, _) => { if (pad.ToPicture() is { } picture) drawn(picture); })!
            .SetNeutralButton("Clear", (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        var use = dialog.GetButton((int)DialogButtonType.Positive)!;
        use.Enabled = false;
        pad.Changed += () => use.Enabled = pad.HasInk;
        // Clear keeps the pad open.
        dialog.GetButton((int)DialogButtonType.Neutral)!.Click += (_, _) => pad.Clear();
    }

    /// <summary>Signs; when the signature outgrows the space reserved for it (a long chain), reserves more and signs again.</summary>
    private static byte[] Sign(PdfVectorDocument engine, byte[] bytes, PdfSignatureRequest request, X509Certificate2 certificate, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var prepared = PdfSigner.Prepare(engine, bytes, request);
            // No timestamp client: the app has no internet permission.
            byte[] cms = PdfCmsSigner.SignAsync(prepared.SignedBytes(), certificate, timestamp: null, token).GetAwaiter().GetResult();
            if (cms.Length <= request.ContentsSize || attempt >= 3) return prepared.Complete(cms);
            request = request with { ContentsSize = Math.Max(request.ContentsSize * 2, cms.Length + 4096) };
        }
    }

    /// <summary>
    /// The box of a visible signature in a corner of the page as it is shown (0 bottom right, 1 bottom
    /// left, 2 top right, 3 top left), in the page's default user space: the corner is turned with the
    /// page's /Rotate, so it is where the reader sees it.
    /// </summary>
    internal static PdfRect CornerRect(PdfVectorDocument engine, int pageNumber, int corner)
    {
        var node = engine.PageTree.Pages[pageNumber - 1];
        var crop = node.CropBox;
        int rotation = ((node.RotationDegrees % 360) + 360) % 360;
        bool quarter = rotation is 90 or 270;
        double shownW = quarter ? crop.Height : crop.Width, shownH = quarter ? crop.Width : crop.Height;
        double w = Math.Min(SignatureWidth, shownW * 0.6), h = Math.Min(SignatureHeight, shownH * 0.2);
        double mx = Math.Min(SignatureMargin, shownW * 0.05), my = Math.Min(SignatureMargin, shownH * 0.05);
        bool right = corner is 0 or 2, bottom = corner is 0 or 1;
        // The box as shown, from the top left, as fractions of the shown page.
        double u0 = (right ? shownW - mx - w : mx) / shownW, u1 = u0 + w / shownW;
        double v0 = (bottom ? shownH - my - h : my) / shownH, v1 = v0 + h / shownH;
        // Shown (u, v) to unrotated (s, t), both from the top left; the page turns clockwise by its /Rotate.
        (double S, double T) Unturn(double u, double v) => rotation switch
        {
            90 => (v, 1 - u),
            180 => (1 - u, 1 - v),
            270 => (1 - v, u),
            _ => (u, v),
        };
        var a = Unturn(u0, v0);
        var b = Unturn(u1, v1);
        double s0 = Math.Min(a.S, b.S), s1 = Math.Max(a.S, b.S), t0 = Math.Min(a.T, b.T), t1 = Math.Max(a.T, b.T);
        return new PdfRect(crop.X + s0 * crop.Width, crop.Y + (1 - t1) * crop.Height, (s1 - s0) * crop.Width, (t1 - t0) * crop.Height);
    }

    /// <summary>The name a signature shows: the certificate's common name, else its subject.</summary>
    private static string CertificateName(X509Certificate2 certificate)
    {
        string name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
    }

    // ------------------------------------------------------------------ verify

    private sealed record SignatureReport(IReadOnlyList<PdfSignatureCheck> Checks, int Revisions);

    /// <summary>
    /// Verify signatures: each signature's verdict, signer, time, identity, what changed after it and
    /// what it covers, offline. The document as it is now is checked, unsaved edits included, so the
    /// report shows what they do to the signatures.
    /// </summary>
    private async void VerifySignatures()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        string? password = _password;
        bool unsaved = _session?.IsDirty == true;
        var report = await RunBusyAsync("Checking the signatures…", token =>
        {
            // OnlineRevocation stays null: nothing is fetched, only what the document holds is used.
            var options = new PdfSignatureValidationOptions { Password = password, OnlineRevocation = null };
            var checks = PdfSignatureValidator.ValidateAsync(bytes, options, token).GetAwaiter().GetResult();
            return new SignatureReport(checks, checks.Count > 0 ? PdfSignatureValidator.RevisionEnds(bytes).Count : 1);
        });
        if (report == null || !ReferenceEquals(doc, _doc)) return;
        if (report.Checks.Count == 0)
        {
            Tell("Verify signatures", "This document has no digital signatures.");
            return;
        }
        var lines = new List<string>();
        int n = report.Checks.Count;
        bool bad = report.Checks.Any(c => c.Verdict is PdfSignatureVerdict.Invalid or PdfSignatureVerdict.ModifiedAfterSigning);
        bool doubt = report.Checks.Any(c => c.Trust != PdfSignatureTrust.Trusted && !c.IsDocumentTimestamp);
        lines.Add(bad ? $"At least one of the {n} signature{(n == 1 ? "" : "s")} is invalid, or the document was changed in a way it does not allow."
            : doubt ? "The document is intact since it was signed, but a signer's identity could not be confirmed on this device."
            : n == 1 ? "The signature is valid." : $"All {n} signatures are valid.");
        if (unsaved) lines.Add("Your unsaved changes are included: they show as changes after signing.");
        int index = 0;
        foreach (var c in report.Checks)
        {
            index++;
            lines.Add("");
            lines.Add($"{index}. " + (c.IsDocumentTimestamp ? "Document timestamp" : $"Signed by {(c.SignerName.Length > 0 ? c.SignerName : "an unnamed signer")}")
                      + (c.CertificationLevel is not null ? " (certification)" : ""));
            lines.Add("Result: " + Verdict(c.Verdict));
            if (c.Summary.Length > 0) lines.Add(c.Summary);
            if (c.TimestampTime is { } stamped)
                lines.Add($"Time: {SignedAt(stamped)}, vouched for by a timestamp" + (c.TimestampAuthority is { Length: > 0 } tsa ? $" ({tsa})" : "") + ".");
            else if (c.ClaimedTime is { } claimed)
                lines.Add($"Time: {SignedAt(claimed)}, as the signer's device claimed (no timestamp).");
            if (!c.IsDocumentTimestamp)
                lines.Add("Identity: " + c.Trust switch
                {
                    PdfSignatureTrust.Trusted => "trusted on this device",
                    PdfSignatureTrust.Untrusted => "not trusted",
                    _ => "unknown (the certificate does not lead to a root this device trusts)",
                } + (c.TrustDetail.Length > 0 ? $". {c.TrustDetail}" : "."));
            if (c.Revocation != PdfRevocationStatus.NotChecked || c.RevocationDetail.Length > 0)
                lines.Add("Revocation: " + c.Revocation switch
                {
                    PdfRevocationStatus.Good => "not revoked",
                    PdfRevocationStatus.Revoked => "REVOKED",
                    PdfRevocationStatus.Unknown => "unknown",
                    _ => "not checked (the document holds no revocation information)",
                } + (c.RevocationDetail.Length > 0 ? $". {c.RevocationDetail}" : "."));
            lines.Add("Covers: " + (c.CoversWholeFile ? "the whole file."
                : $"revision {c.Revision} of {report.Revisions}; what was added after it is weighed below."));
            lines.Add("Changes after signing: " + Changes(c.LaterChanges));
            if (c.CertificationLevel is { } level)
                lines.Add("Certification allows: " + level switch
                {
                    1 => "no changes.",
                    2 => "filling in forms and signing.",
                    _ => "filling in forms, signing and comments.",
                });
            if (c.Reason is { Length: > 0 } reason) lines.Add($"Reason: {reason}");
            if (c.Location is { Length: > 0 } where) lines.Add($"Location: {where}");
            lines.Add(c.PageNumber > 0 && c.Rect.Width > 0 ? $"Shown on page {c.PageNumber}." : "Not shown on a page (invisible).");
        }
        lines.Add("");
        lines.Add("Checked offline: certificates against the roots this device trusts, revocation only from what the document holds.");
        ShowReport("Signatures", string.Join("\n", lines), BaseName + " signatures");
    }

    private static string Verdict(PdfSignatureVerdict verdict) => verdict switch
    {
        PdfSignatureVerdict.Valid => "valid; nothing changed after it.",
        PdfSignatureVerdict.ValidWithPermittedChanges => "valid; later changes are ones the signature allows.",
        PdfSignatureVerdict.ModifiedAfterSigning => "the document was changed after signing in a way the signature does not allow.",
        PdfSignatureVerdict.Invalid => "INVALID: the signed content does not match the signature.",
        _ => "not checked (a signature format this app does not check).",
    };

    private static string Changes(PdfLaterChanges changes)
    {
        if (changes == PdfLaterChanges.None) return "none.";
        var parts = new List<string>();
        if (changes.HasFlag(PdfLaterChanges.Signatures)) parts.Add("signatures added");
        if (changes.HasFlag(PdfLaterChanges.FormFields)) parts.Add("form fields filled");
        if (changes.HasFlag(PdfLaterChanges.Annotations)) parts.Add("comments");
        if (changes.HasFlag(PdfLaterChanges.Metadata)) parts.Add("document information");
        if (changes.HasFlag(PdfLaterChanges.ValidationData)) parts.Add("validation data (not a change to the content)");
        if (changes.HasFlag(PdfLaterChanges.DocumentTimestamp)) parts.Add("a document timestamp (not a change to the content)");
        if (changes.HasFlag(PdfLaterChanges.Other)) parts.Add("other changes (page content, pages or resources)");
        return string.Join(", ", parts) + ".";
    }

    private static string SignedAt(DateTimeOffset time) => time.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
}
