using System;
using System.IO;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>Reduce File Size in the viewer: a smaller copy, never over the original, with images downsampled when chosen.</summary>
public class ReduceSizeViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReduceSize_" + Guid.NewGuid().ToString("N"));
    public ReduceSizeViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A page with a 600 × 600 noise image shown 2 inches wide (300 ppi), and some text.</summary>
    private string Document()
    {
        var b = new VectorPdfBuilder();
        var rgb = new byte[600 * 600 * 3];
        new Random(3).NextBytes(rgb);
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 600 /Height 600 /ColorSpace /DeviceRGB /BitsPerComponent 8", rgb, flate: true);
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("q 144 0 0 144 100 100 cm /Im1 Do Q BT /F1 12 Tf 100 60 Td (Site photograph) Tj ET",
            $"<< /XObject << /Im1 {image} 0 R >> /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 400]");
        string path = Path.Combine(_dir, "report.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task ASmallerCopy_IsSaved_WithImagesDownsampled_AndTheOriginalUntouched()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        using var vm = await Open(path);
        var reduce = vm.CreateReduceSizeViewModel()!;
        Assert.True(reduce.CanReduce);
        Assert.False(await reduce.ReduceAsync(path)); // never over the original
        Assert.Equal(original, File.ReadAllBytes(path));

        reduce.SelectedPreset = reduce.Presets[2]; // 150 ppi
        string output = reduce.SuggestedOutputPath;
        Assert.EndsWith("report_reduced.pdf", output);
        Assert.True(await reduce.ReduceAsync(output), reduce.Summary);
        Assert.Contains("1 image(s) downsampled", reduce.Summary);
        Assert.True(new FileInfo(output).Length < original.Length / 3, $"{original.Length} -> {new FileInfo(output).Length}");

        using var copy = await Open(output);
        Assert.Single(copy.Pages);
    }

    /// <summary>The same page, encrypted with AES-256: "user" opens it, "owner" lifts the restrictions.</summary>
    private string EncryptedDocument(int? permissions = null)
    {
        var b = new VectorPdfBuilder();
        var rgb = new byte[600 * 600 * 3];
        new Random(3).NextBytes(rgb);
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 600 /Height 600 /ColorSpace /DeviceRGB /BitsPerComponent 8", rgb, flate: true);
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("q 144 0 0 144 100 100 cm /Im1 Do Q BT /F1 12 Tf 100 60 Td (Site photograph) Tj ET",
            $"<< /XObject << /Im1 {image} 0 R >> /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 400]");
        string path = Path.Combine(_dir, "secured.pdf");
        File.WriteAllBytes(path, b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner", permissions: permissions)));
        return path;
    }

    private static async Task<MainViewModel> OpenWith(string path, string password)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { }, RequestPasswordFunc = _ => Task.FromResult<string?>(password) };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task AnEncryptedDocument_IsReduced_AndKeepsItsPasswordsAndPermissions()
    {
        int changesAllowed = unchecked((int)0xFFFFF0CC); // print and change; no copying
        string path = EncryptedDocument(changesAllowed);
        using var vm = await OpenWith(path, "user");
        var reduce = vm.CreateReduceSizeViewModel()!;
        Assert.True(reduce.IsEncrypted);
        Assert.True(reduce.CanReduce);
        Assert.Contains("same passwords and permissions", reduce.Summary);

        reduce.SelectedPreset = reduce.Presets[2];
        string output = reduce.SuggestedOutputPath;
        Assert.True(await reduce.ReduceAsync(output), reduce.Summary);
        Assert.Contains("encrypted as the original was", reduce.Summary);
        Assert.True(new FileInfo(output).Length < new FileInfo(path).Length / 3);

        using (var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(output, password: "user"))
        {
            Assert.Equal(6, doc.Security!.Revision);
            Assert.Equal(changesAllowed, doc.Security.Permissions);
        }
        using (var owner = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(output, password: "owner"))
            Assert.True(owner.Security!.IsOwner);
        using var copy = await OpenWith(output, "user");
        Assert.Single(copy.Pages);
    }

    [Fact]
    public async Task ADocumentThatForbidsChanges_AsksForThePermissionsPassword()
    {
        string path = EncryptedDocument(); // print only
        using var vm = await OpenWith(path, "user");
        string output = Path.Combine(_dir, "secured_reduced.pdf");

        var cancelled = vm.CreateReduceSizeViewModel()!;
        int asked = 0;
        cancelled.RequestOwnerPassword = () => { asked++; return null; };
        Assert.False(await cancelled.ReduceAsync(output));
        Assert.Equal(1, asked);
        Assert.Contains("permissions password", cancelled.Summary);
        Assert.False(File.Exists(output));

        var wrong = vm.CreateReduceSizeViewModel()!;
        wrong.RequestOwnerPassword = () => "user"; // the open password is not the permissions password
        Assert.False(await wrong.ReduceAsync(output));
        Assert.Contains("permissions password is incorrect", wrong.Summary);
        Assert.DoesNotContain("user", wrong.Summary);

        vm.RequestOwnerPasswordFunc = name => { Assert.Equal("secured.pdf", name); return "owner"; };
        var reduce = vm.CreateReduceSizeViewModel()!;
        Assert.True(await reduce.ReduceAsync(output), reduce.Summary);
        using var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(output, password: "user");
        Assert.False(doc.Security!.IsOwner);
        Assert.Equal(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner").P, doc.Security.Permissions); // still restricted
    }
}
