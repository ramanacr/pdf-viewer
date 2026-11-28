using Android.App;
using Android.Content;
using Android.Provider;
using Android.Widget;

namespace PdfViewer.Android;

/// <summary>
/// Take photos for "PDF from pictures" (the Windows app makes a PDF from a scanner; a phone's scanner is
/// its camera): the device's camera app takes each photo (ACTION_IMAGE_CAPTURE) and writes it to a
/// content URI this app serves from its cache (<see cref="CameraPhotoProvider"/>), with write access
/// granted for that URI only. After each photo the reader takes another or is done; the photos then go
/// through the same ordering and PDF making as picked pictures, and are deleted afterwards. The app asks
/// for no camera permission: the camera app has it.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The most photos one PDF takes in a row (each is a page).</summary>
    private const int MaxPhotos = 200;

    /// <summary>The photos of this run are named after it, so a name is never used twice.</summary>
    private readonly long _cameraRun = DateTime.UtcNow.Ticks;

    private int _photoNumber;

    /// <summary>
    /// Takes photos one after another with the camera app, asking after each "Take another / Done";
    /// <paramref name="done"/> gets the photos taken (none when the first was cancelled). A photo the
    /// camera app did not write is dropped.
    /// </summary>
    private void TakePhotos(List<PickedFile> taken, Action<List<PickedFile>> done)
    {
        var intent = new Intent(MediaStore.ActionImageCapture);
        if (PackageManager is not { } packages || intent.ResolveActivity(packages) == null)
        {
            new AlertDialog.Builder(this)
                .SetTitle("No camera app")!
                .SetMessage("There is no camera app on this device that takes photos for other apps. Choose pictures instead.")!
                .SetPositiveButton("OK", (_, _) => done(taken))!
                .SetCancelable(false)!
                .Show();
            return;
        }
        Java.IO.File file;
        string name = $"photo-{_cameraRun}-{++_photoNumber % 10000}.jpg";
        try
        {
            var folder = new Java.IO.File(CacheDir, CameraPhotoProvider.Folder);
            folder.Mkdirs();
            file = new Java.IO.File(folder, name);
            file.Delete();
        }
        catch (Java.Lang.Exception)
        {
            Toast.MakeText(this, "There is no room for a photo on this device.", ToastLength.Long)?.Show();
            done(taken);
            return;
        }
        var uri = CameraPhotoProvider.UriFor(name);
        intent.PutExtra(MediaStore.ExtraOutput, uri);
        // The grant goes with the clip data: the camera app may write this one file, nothing else.
        intent.ClipData = ClipData.NewRawUri("", uri);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        int number = taken.Count + 1;
        StartForResult(intent, (result, _) =>
        {
            RevokeUriPermission(uri, ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
            if (result != Result.Ok || !file.Exists() || file.Length() == 0)
            {
                file.Delete();
                done(taken);
                return;
            }
            taken.Add(new PickedFile(uri, $"Photo {number}.jpg", false));
            if (taken.Count >= MaxPhotos)
            {
                Toast.MakeText(this, $"{MaxPhotos} photos is the most for one PDF.", ToastLength.Long)?.Show();
                done(taken);
                return;
            }
            new AlertDialog.Builder(this)
                .SetTitle(taken.Count == 1 ? "1 photo taken" : $"{taken.Count} photos taken")!
                .SetMessage("Take another photo, or put the pages in order and make the PDF.")!
                .SetPositiveButton("Take another", (_, _) => TakePhotos(taken, done))!
                .SetNegativeButton("Done", (_, _) => done(taken))!
                .SetCancelable(false)!
                .Show();
        });
    }

    /// <summary>Deletes the photos taken for a PDF (made, or given up); they stay in the app's cache only while needed.</summary>
    private void DeleteCameraPhotos()
    {
        // The folder is shared by the app's windows: only this window's photos go, and others' only
        // when they are old enough to be left over from an app that was closed (a window takes photos for minutes, not hours).
        string mine = $"photo-{_cameraRun}-";
        long staleBefore = Java.Lang.JavaSystem.CurrentTimeMillis() - 6L * 60 * 60 * 1000;
        try
        {
            if (new Java.IO.File(CacheDir, CameraPhotoProvider.Folder).ListFiles() is { } files)
                foreach (var file in files)
                    if (file.Name.StartsWith(mine, StringComparison.Ordinal) || file.LastModified() < staleBefore)
                        file.Delete();
        }
        catch (Java.Lang.Exception)
        {
            // The cache is the system's to clear as well.
        }
    }
}
