using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Android reader's "Save" (src/PdfViewer.Android, <see cref="ViewerSaveBack"/>), run on the
/// desktop: when it writes the opened file and when it refuses or asks, that the bytes written are
/// the file as opened plus an incremental update, and that only a verified write clears the dirty
/// state.
/// </summary>
public class AndroidSaveTests
{
    private static byte[] TwoPages()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Length > prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static async Task<IReadOnlyList<ViewerAnnotation>> ReadBack(byte[] bytes)
    {
        await using var doc = await ViewerDocument.OpenAsync(bytes);
        return await doc.ReadAnnotationsAsync();
    }

    [Fact]
    public void FileStamps_ChangeOnlyWhenAKnownValueDiffers()
    {
        var opened = new ViewerFileStamp(1000, 5);
        Assert.False(opened.ChangedSince(opened));
        Assert.True(new ViewerFileStamp(1001, 5).ChangedSince(opened));
        Assert.True(new ViewerFileStamp(1000, 6).ChangedSince(opened));
        // A provider that gives no size or time proves nothing either way.
        Assert.False(new ViewerFileStamp(null, null).ChangedSince(opened));
        Assert.False(new ViewerFileStamp(1000, 9).ChangedSince(new ViewerFileStamp(1000, null)));
        Assert.False(default(ViewerFileStamp).ChangedSince(default));
    }

    [Fact]
    public async Task Check_RefusesWithoutEdits_WithoutWriteAccess_AndAsksWhenTheFileChanged()
    {
        await using var doc = await ViewerDocument.OpenAsync(TwoPages());
        var session = await ViewerEditSession.StartAsync(doc);
        var stamp = new ViewerFileStamp(100, 1);

        Assert.Equal(ViewerSaveBackStep.NothingToSave, ViewerSaveBack.Check(null, true, stamp, stamp));
        Assert.Equal(ViewerSaveBackStep.NothingToSave, ViewerSaveBack.Check(session, true, stamp, stamp));

        session.AddNote(1, new ViewerPoint(50, 50), "Check");
        Assert.True(session.IsDirty);
        Assert.Equal(ViewerSaveBackStep.Write, ViewerSaveBack.Check(session, true, stamp, stamp));
        Assert.Equal(ViewerSaveBackStep.CannotWrite, ViewerSaveBack.Check(session, false, stamp, stamp));
        var changed = new ViewerFileStamp(120, 2);
        Assert.Equal(ViewerSaveBackStep.ChangedOnDisk, ViewerSaveBack.Check(session, true, stamp, changed));
        Assert.Equal(ViewerSaveBackStep.Write, ViewerSaveBack.Check(session, true, stamp, changed, overwrite: true));

        // One save at a time.
        var save = await session.BeginSaveAsync();
        Assert.Equal(ViewerSaveBackStep.Busy, ViewerSaveBack.Check(session, true, stamp, stamp));
        var busy = await ViewerSaveBack.SaveAsync(session, _ => throw new InvalidOperationException("must not write"));
        Assert.Equal((false, ViewerSaveBack.BusyMessage), (busy.Saved, busy.Problem));
        session.EndSave(save!, saved: false);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Save_WritesTheFileAsOpenedPlusAnUpdate_ClearsDirty_AndKeepsUndo()
    {
        byte[] pdf = TwoPages();
        await using var doc = await ViewerDocument.OpenAsync(pdf);
        var session = await ViewerEditSession.StartAsync(doc);
        var note = session.AddNote(1, new ViewerPoint(50, 50), "First");
        Assert.NotNull(note);

        byte[]? file = null;
        var result = await ViewerSaveBack.SaveAsync(session, bytes =>
        {
            Assert.True(session.IsSaving);
            file = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        });
        Assert.True(result.Saved);
        Assert.Null(result.Problem);
        Assert.NotNull(file);
        Assert.Equal(file!.Length, result.Length);
        Assert.True(StartsWith(file, pdf)); // the bytes as opened, then the incremental update
        Assert.Single(await ReadBack(file));
        Assert.False(session.IsDirty);
        Assert.False(session.IsSaving);
        // The undo history stays after a save (as in the desktop and web viewers); undoing makes it dirty again.
        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.True(session.IsDirty);

        // A second save writes the whole file again: still the file as opened plus one update, now without the note.
        Assert.True(session.Redo());
        session.AddNote(2, new ViewerPoint(80, 80), "Second");
        byte[]? again = null;
        var second = await ViewerSaveBack.SaveAsync(session, bytes =>
        {
            again = bytes.ToArray();
            return Task.FromResult<long?>(null); // a provider that cannot tell the length
        });
        Assert.True(second.Saved);
        Assert.True(StartsWith(again!, pdf));
        Assert.Equal(2, (await ReadBack(again!)).Count);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task AFailedOrShortWrite_IsNotASave_AndTheEditsStayDirty()
    {
        await using var doc = await ViewerDocument.OpenAsync(TwoPages());
        var session = await ViewerEditSession.StartAsync(doc);
        session.AddNote(1, new ViewerPoint(50, 50), "Keep me");

        // The write throws part-way: the reader is told the file may be incomplete.
        var failed = await ViewerSaveBack.SaveAsync(session, _ => throw new IOException("disk full"));
        Assert.False(failed.Saved);
        Assert.True(failed.FileMayBeDamaged);
        Assert.Contains("IOException", failed.Problem);
        Assert.DoesNotContain("disk full", failed.Problem); // type names only
        Assert.True(session.IsDirty);
        Assert.False(session.IsSaving);

        // The provider reports a different length afterwards.
        var shortWrite = await ViewerSaveBack.SaveAsync(session, bytes => Task.FromResult<long?>(bytes.Length - 10));
        Assert.False(shortWrite.Saved);
        Assert.True(shortWrite.FileMayBeDamaged);
        Assert.Contains("may now be incomplete", shortWrite.Problem);
        Assert.True(session.IsDirty);

        Assert.Null(ViewerSaveBack.Verify(10, 10));
        Assert.Null(ViewerSaveBack.Verify(10, null));
        Assert.NotNull(ViewerSaveBack.Verify(10, 12));

        // A later save still works.
        Assert.True((await ViewerSaveBack.SaveAsync(session, bytes => Task.FromResult<long?>(bytes.Length))).Saved);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task AFileThatCannotBeOpenedForWriting_IsUnchanged_AndACopyIsOffered()
    {
        await using var doc = await ViewerDocument.OpenAsync(TwoPages());
        var session = await ViewerEditSession.StartAsync(doc);
        session.AddNote(1, new ViewerPoint(50, 50), "Keep me");
        int calls = 0;
        var result = await ViewerSaveBack.SaveAsync(session, _ =>
        {
            calls++;
            throw new ViewerFileNotOpenedException("FileNotFoundException");
        }, previous: [1, 2, 3]);
        Assert.False(result.Saved);
        Assert.False(result.FileMayBeDamaged); // nothing was written
        Assert.True(result.OfferCopy);
        Assert.Contains("was not changed", result.Problem);
        Assert.Equal(1, calls); // no "restore" of a file that was never touched
        Assert.True(session.IsDirty);
        Assert.False(session.IsSaving);
    }

    [Fact]
    public async Task AWriteThatFailsPartWay_WritesThePreviousBytesBack()
    {
        byte[] pdf = TwoPages();
        await using var doc = await ViewerDocument.OpenAsync(pdf);
        var session = await ViewerEditSession.StartAsync(doc);
        session.AddNote(1, new ViewerPoint(50, 50), "Keep me");

        // The file as a provider would hold it: the new bytes fail half-way, the old ones go back.
        byte[] file = pdf.ToArray();
        var restored = await ViewerSaveBack.SaveAsync(session, bytes =>
        {
            if (bytes.Length != pdf.Length)
            {
                file = bytes.AsSpan(0, bytes.Length / 2).ToArray();
                throw new IOException("broken pipe");
            }
            file = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        }, previous: pdf);
        Assert.False(restored.Saved);
        Assert.True(restored.Restored);
        Assert.False(restored.FileMayBeDamaged);
        Assert.True(restored.OfferCopy);
        Assert.Contains("written back", restored.Problem);
        Assert.Equal(pdf, file);
        Assert.True(session.IsDirty);

        // A short write is treated the same way.
        var shortWrite = await ViewerSaveBack.SaveAsync(session, bytes => Task.FromResult<long?>(bytes.Length == pdf.Length ? pdf.Length : 7), previous: pdf);
        Assert.True(shortWrite.Restored);
        Assert.True(session.IsDirty);

        // When writing the old bytes back fails too, the file may be damaged and the reader is told.
        var lost = await ViewerSaveBack.SaveAsync(session, _ => throw new IOException("gone"), previous: pdf);
        Assert.False(lost.Restored);
        Assert.True(lost.FileMayBeDamaged);
        Assert.Contains("may now be incomplete", lost.Problem);
    }

    [Fact]
    public async Task ALengthThePlatformCannotTell_IsSavedButUnverified()
    {
        await using var doc = await ViewerDocument.OpenAsync(TwoPages());
        var session = await ViewerEditSession.StartAsync(doc);
        session.AddNote(1, new ViewerPoint(50, 50), "n");
        var result = await ViewerSaveBack.SaveAsync(session, _ => Task.FromResult<long?>(null));
        Assert.True(result.Saved);
        Assert.True(result.Unverified);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ACopy_LeavesTheOpenedFileUnsaved_AndSaveStillWritesEveryEdit()
    {
        byte[] pdf = TwoPages();
        await using var doc = await ViewerDocument.OpenAsync(pdf);
        var session = await ViewerEditSession.StartAsync(doc);
        session.AddNote(1, new ViewerPoint(50, 50), "In the copy");

        byte[]? copy = null;
        var copied = await ViewerSaveBack.CopyAsync(session, bytes =>
        {
            copy = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        });
        Assert.True(copied.Saved);
        Assert.True(StartsWith(copy!, pdf));
        // The opened file does not have the edits: still dirty, still asked about, Save still offered.
        Assert.True(session.IsDirty);
        Assert.False(session.IsSaving);
        Assert.Equal(ViewerSaveBackStep.Write, ViewerSaveBack.Check(session, true, default, default));
        Assert.StartsWith("Your changes are not saved in this file", ViewerSaveBack.UnsavedMessage);

        byte[]? file = null;
        Assert.True((await ViewerSaveBack.SaveAsync(session, bytes =>
        {
            file = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        })).Saved);
        Assert.Single(await ReadBack(file!));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void TheStampAfterASave_IgnoresALateLastModifiedTime()
    {
        // A cloud or pipe provider may set its time after this app's write: only the size counts then.
        var afterSave = ViewerSaveBack.StampAfterSave(5000);
        Assert.False(new ViewerFileStamp(5000, 1_700_000_000_000).ChangedSince(afterSave));
        Assert.True(new ViewerFileStamp(5001, 1_700_000_000_000).ChangedSince(afterSave));
    }

    private static async Task<byte[]> Signed(int? certificationLevel)
    {
        byte[] pdf = FormPdfFixture.Build();
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Author", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Author", CertificationLevel = certificationLevel });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    [Fact]
    public async Task ASignedFile_IsSavedBackAsAnIncrementalUpdate_AndItsSignatureStaysValid()
    {
        byte[] signed = await Signed(2);
        await using var doc = await ViewerDocument.OpenAsync(signed);
        var session = await ViewerEditSession.StartAsync(doc);
        Assert.True(session.SetFieldValue(session.Widgets.First(w => w.Name == "person.name"), ["Grace"], out _));

        byte[]? file = null;
        var result = await ViewerSaveBack.SaveAsync(session, bytes =>
        {
            file = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        });
        Assert.True(result.Saved);
        Assert.True(StartsWith(file!, signed));
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(file!));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
    }

    [Fact]
    public async Task ADocumentThatAllowsNoChanges_HasNothingToSave()
    {
        // A certification that allows no changes: no edit is taken, so Save has nothing to write.
        await using var doc = await ViewerDocument.OpenAsync(await Signed(1));
        var session = await ViewerEditSession.StartAsync(doc);
        Assert.Throws<ViewerAnnotationException>(() => session.AddNote(1, new ViewerPoint(50, 50), "n"));
        Assert.Equal(ViewerSaveBackStep.NothingToSave, ViewerSaveBack.Check(session, true, default, default));
    }
}
