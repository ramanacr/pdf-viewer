using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;

namespace PdfViewer.Services;

/// <summary>A custom stamp in the library: a picture (PNG or JPEG) or one page of a PDF, with its shape.</summary>
public sealed record CustomStamp(string Id, string Name, bool IsPdf, string FileName, double Width, double Height)
{
    /// <summary>The usual size it is placed at, in points: about 2 inches across the longer side.</summary>
    public (double Width, double Height) PlacedSize()
    {
        double longest = Math.Max(Width, Height);
        double scale = longest > 0 ? 144 / longest : 1;
        return (Math.Max(8, Width * scale), Math.Max(8, Height * scale));
    }
}

/// <summary>
/// The user's stamp settings and custom stamps, kept on this computer under
/// %LOCALAPPDATA%\PdfViewerNative (stamps.json and the Stamps folder) and never sent anywhere:
/// the name dynamic stamps show, and the pictures and PDF pages made into stamps.
/// </summary>
public static class StampLibrary
{
    /// <summary>Pictures and PDFs larger than this are refused (a stamp is a small thing).</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private sealed class Settings
    {
        public string? Identity { get; set; }
        public List<CustomStamp> Stamps { get; set; } = new();
    }

    private static string SettingsPath => Path.Combine(SigningSettings.Directory, "stamps.json");

    /// <summary>Where the custom stamps' files are.</summary>
    public static string Folder => Path.Combine(SigningSettings.Directory, "Stamps");

    private static Settings Read()
    {
        try
        {
            return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings() : new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    private static void Write(Settings settings)
    {
        Directory.CreateDirectory(SigningSettings.Directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The name dynamic stamps show, or null until the user enters one.</summary>
    public static string? Identity => Read().Identity is { Length: > 0 } s ? s : null;

    public static void SetIdentity(string name)
    {
        var settings = Read();
        settings.Identity = name.Trim();
        try { Write(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<CustomStamp> CustomStamps => Read().Stamps.Where(s => File.Exists(Path.Combine(Folder, s.FileName))).ToList();

    /// <summary>The file of a custom stamp (a picture or a one-page PDF).</summary>
    public static byte[] Load(CustomStamp stamp) => File.ReadAllBytes(Path.Combine(Folder, Path.GetFileName(stamp.FileName)));

    /// <summary>Adds a picture (PNG or JPEG) as a custom stamp, kept as the file it is.</summary>
    public static CustomStamp AddImage(string path, string name)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new NotSupportedException("The picture is larger than 20 MB.");
        byte[] bytes = File.ReadAllBytes(path);
        string ext = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : bytes.Length > 7 && bytes[0] == 0x89 && bytes[1] == (byte)'P' ? ".png" : string.Empty;
        if (ext.Length == 0) throw new NotSupportedException("Choose a PNG or JPEG picture.");
        var image = EditImageLoader.Load(bytes); // decodes it, and refuses one too large to place
        return Add(name, isPdf: false, ext, bytes, image.Width, image.Height);
    }

    /// <summary>Adds page <paramref name="pageNumber"/> of a PDF as a custom stamp, kept as a one-page PDF of that page alone.</summary>
    public static CustomStamp AddPdfPage(string path, int pageNumber, string name)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new NotSupportedException("The PDF is larger than 20 MB.");
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = PdfVectorDocument.Open(new PdfMemoryByteSource(bytes));
        if (doc.IsEncrypted) throw new NotSupportedException("The PDF is encrypted; use an unencrypted copy.");
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber), $"The PDF has {doc.PageCount} page(s).");
        // Only the page and what it draws: no outline, form or other pages.
        byte[] one = PdfPageAssembler.Extract(doc, new[] { pageNumber }, new PdfAssemblyOptions { KeepOutlines = false, KeepForms = false });
        var node = doc.PageTree.Pages[pageNumber - 1];
        var crop = node.CropBox;
        bool turned = node.RotationDegrees is 90 or 270;
        return Add(name, isPdf: true, ".pdf", one, turned ? crop.Height : crop.Width, turned ? crop.Width : crop.Height);
    }

    private static CustomStamp Add(string name, bool isPdf, string ext, byte[] bytes, double width, double height)
    {
        string id = Guid.NewGuid().ToString("N");
        var stamp = new CustomStamp(id, string.IsNullOrWhiteSpace(name) ? "Custom stamp" : name.Trim(), isPdf, id + ext, width, height);
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, stamp.FileName), bytes);
        var settings = Read();
        settings.Stamps.Add(stamp);
        Write(settings);
        return stamp;
    }

    public static void Remove(CustomStamp stamp)
    {
        var settings = Read();
        settings.Stamps.RemoveAll(s => s.Id == stamp.Id);
        try
        {
            Write(settings);
            string file = Path.Combine(Folder, Path.GetFileName(stamp.FileName));
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A small picture of a custom stamp for the menu (null for a PDF page, or when it cannot be read).</summary>
    public static BitmapSource? Thumbnail(CustomStamp stamp)
    {
        if (stamp.IsPdf) return null;
        try
        {
            using var ms = new MemoryStream(Load(stamp), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = 32;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
