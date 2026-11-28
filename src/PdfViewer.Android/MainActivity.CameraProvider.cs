using System.Text.RegularExpressions;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Serves the photos of "PDF from pictures → Take photos" to the camera app: a content URI for a file
/// in the app's cache (cache/camera) that the camera app writes the photo to (EXTRA_OUTPUT), with
/// write access granted for that one URI by the intent. It is not exported, so no other app can
/// reach it without such a grant; it serves only files named as the app names them, in that one
/// folder. The app needs no permission for this (the camera app holds the camera permission), and
/// the photos are deleted once the PDF is made or the reader stops.
/// </summary>
[ContentProvider([Authority], Name = "io.github.ramanacr.pdfviewer.CameraPhotoProvider", Exported = false, GrantUriPermissions = true)]
public sealed partial class CameraPhotoProvider : ContentProvider
{
    /// <summary>The provider's authority: the application id and "camera".</summary>
    public const string Authority = "io.github.ramanacr.pdfviewer.camera";

    /// <summary>The folder in the app's cache that holds the photos while they are taken.</summary>
    public const string Folder = "camera";

    public CameraPhotoProvider() { }

    private CameraPhotoProvider(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    /// <summary>The content URI of a photo file in <see cref="Folder"/>.</summary>
    public static Uri UriFor(string fileName) => Uri.Parse($"content://{Authority}/{Folder}/{fileName}")!;

    [GeneratedRegex(@"^photo-[0-9]{1,20}-[0-9]{1,4}\.jpg$")]
    private static partial Regex PhotoName();

    /// <summary>The file a URI names: only a photo name in the camera folder of this app's cache; null otherwise.</summary>
    private Java.IO.File? FileFor(Uri? uri)
    {
        var segments = uri?.PathSegments;
        if (Context?.CacheDir is not { } cache || segments is not { Count: 2 } || segments[0] != Folder || segments[1] is not { } name || !PhotoName().IsMatch(name))
            return null;
        return new Java.IO.File(new Java.IO.File(cache, Folder), name);
    }

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Uri uri, string mode)
    {
        var file = FileFor(uri) ?? throw new Java.IO.FileNotFoundException("No such photo.");
        return ParcelFileDescriptor.Open(file, ParcelFileDescriptor.ParseMode(mode));
    }

    /// <summary>The name and size of a photo, for an app that asks (OpenableColumns).</summary>
    public override ICursor? Query(Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        if (FileFor(uri) is not { } file) return null;
        var columns = projection ?? [IOpenableColumns.DisplayName, IOpenableColumns.Size];
        var cursor = new MatrixCursor(columns, 1);
        var row = new Java.Lang.Object[columns.Length];
        for (int i = 0; i < columns.Length; i++)
            row[i] = columns[i] switch
            {
                IOpenableColumns.DisplayName => new Java.Lang.String(file.Name),
                IOpenableColumns.Size => Java.Lang.Long.ValueOf(file.Length()),
                _ => null!,
            };
        cursor.AddRow(row);
        return cursor;
    }

    public override string? GetType(Uri uri) => FileFor(uri) != null ? "image/jpeg" : null;

    public override Uri? Insert(Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();

    public override int Delete(Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();

    public override int Update(Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
}
