using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Several documents at once, the Android way: where the Windows app has tabs, each document here
/// has its own window, a task of its own (<c>FLAG_ACTIVITY_NEW_DOCUMENT | FLAG_ACTIVITY_MULTIPLE_TASK</c>),
/// so Recents shows each document by its name (the task's <see cref="ActivityManager.TaskDescription"/>),
/// split screen shows two side by side, and a tablet's or Chromebook's free-form windows each hold
/// one. Each window is a full viewer with its own saved state, so a restore after Android ended the
/// app reopens each window's own document; the recent list is one for the whole process.
/// <para>
/// A location is open in one window at most: opening it again (from any window, from the picker,
/// the recent list or another app) brings the window that shows it forward through
/// <see cref="ActivityManager.AppTask.MoveToFront"/>, which needs no permission for the app's own
/// tasks. The windows this process runs are in <see cref="s_windows"/>; which document each task
/// shows is also kept in <c>files/windows.json</c> (task id, location, file name: no document text),
/// so that after Android ended the process the windows not yet recreated are still known.
/// <b>⋮ → Documents</b> lists the windows (the current one marked), switches to one, or closes one:
/// a window with unsaved edits asks in that window (it alone holds them), through its usual
/// save-or-discard question.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>An extra of the intent "Open in new window" sends for a recent file: open it at its last page.</summary>
    private const string ExtraFromRecent = "pdfviewer.window.fromRecent";

    private const string NoDocument = "No document";

    /// <summary>One recent list for every window of the process (each window used to load its own copy).</summary>
    private static RecentDocuments? s_recent;

    /// <summary>The windows (activities) this process runs; UI thread only.</summary>
    private static readonly List<MainActivity> s_windows = new();

    /// <summary>The document each task of this app shows, by task id, as in <c>files/windows.json</c>; UI thread only.</summary>
    private static Dictionary<int, (string Location, string Name)>? s_shown;

    /// <summary>Tasks the Documents list asked to close while their window was not running: they close once reopened.</summary>
    private static readonly HashSet<int> s_closeAsked = new();

    /// <summary>Writes of <c>files/windows.json</c>, one after another.</summary>
    private static Task s_shownIo = Task.CompletedTask;
    private static readonly object ShownIoGate = new();

    /// <summary>The saved state's document is being reopened (a close asked meanwhile waits for it).</summary>
    private bool _reopening;

    private string WindowsPath => System.IO.Path.Combine(FilesDir!.AbsolutePath, "windows.json");

    /// <summary>The name the window is known by: in Recents, in the Documents list.</summary>
    private string WindowName => _doc == null ? NoDocument : _fileName ?? _doc.Title ?? "Document";

    // ------------------------------------------------------------------ the window's life

    /// <summary>At create: the shared recent list, the window joins the process's list, and the task is labelled.</summary>
    private void JoinWindows()
    {
        _recent = s_recent ??= new RecentDocuments(System.IO.Path.Combine(FilesDir!.AbsolutePath, "recent.json"));
        s_windows.Add(this);
        LoadShown();
        SetTaskLabel(null);
    }

    /// <summary>At destroy: a window that ends (not one recreated) no longer shows its document.</summary>
    private void LeaveWindows()
    {
        s_windows.Remove(this);
        if (IsFinishing && Shown().Remove(TaskId)) SaveShown();
    }

    /// <summary>Another window may have changed the recent list; and a close asked while this window was not running.</summary>
    protected override void OnResume()
    {
        base.OnResume();
        if (_doc == null) ShowRecent();
        CloseIfAsked();
    }

    /// <summary>After a document opened in this window: Recents and the Documents list show its name.</summary>
    private void DocumentShown()
    {
        if (_uri == null || _doc == null) return;
        Shown()[TaskId] = (_uri.ToString()!, WindowName);
        SaveShown();
        SetTaskLabel(WindowName);
    }

    /// <summary>The task's label in Recents and on a free-form window's caption: the document's file name.</summary>
    private void SetTaskLabel(string? name)
    {
        string label = name ?? "PDF Viewer";
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            SetTaskDescription(new ActivityManager.TaskDescription.Builder().SetLabel(label)!.Build());
            return;
        }
#pragma warning disable CA1422 // the label-only constructor is the one before Android 13
        SetTaskDescription(new ActivityManager.TaskDescription(label));
#pragma warning restore CA1422
    }

    // ------------------------------------------------------------------ the app's tasks

    /// <summary>This app's tasks, most recent first (the system's own list, which survives the process); null when it cannot be read.</summary>
    private List<(int Id, ActivityManager.AppTask Task, string? Label)>? AppTasks()
    {
        try
        {
            var manager = GetSystemService(ActivityService)?.JavaCast<ActivityManager>();
            if (manager?.AppTasks is not { } tasks) return null;
            var list = new List<(int, ActivityManager.AppTask, string?)>();
            foreach (var task in tasks)
            {
                try
                {
                    if (task.TaskInfo is { } info) list.Add((TaskIdOf(info), task, info.TaskDescription?.Label));
                }
                catch (Java.Lang.IllegalArgumentException)
                {
                    // The task ended meanwhile.
                }
            }
            return list;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    private static int TaskIdOf(ActivityManager.RecentTaskInfo info)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return info.TaskId;
#pragma warning disable CA1422, CS0618 // persistentId is the task id before Android 10
        return info.PersistentId;
#pragma warning restore CA1422, CS0618
    }

    /// <summary>This window's task is one of the app's own (not the task of the app that opened a file here with "Open with").</summary>
    private bool OwnsTask() => AppTasks()?.Any(t => t.Id == TaskId) == true;

    private Dictionary<int, (string Location, string Name)> Shown()
    {
        LoadShown();
        return s_shown!;
    }

    /// <summary>Reads <c>files/windows.json</c> once per process, without the tasks that ended meanwhile.</summary>
    private void LoadShown()
    {
        if (s_shown != null) return;
        s_shown = new Dictionary<int, (string, string)>();
        try
        {
            var file = new FileInfo(WindowsPath);
            if (file.Exists && file.Length <= 256 * 1024)
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                foreach (var entry in json.RootElement.EnumerateArray())
                {
                    if (entry.TryGetProperty("task", out var task) && task.TryGetInt32(out int id)
                        && entry.TryGetProperty("location", out var location) && location.GetString() is { Length: > 0 } where
                        && entry.TryGetProperty("name", out var name))
                        s_shown[id] = (where, name.GetString() ?? "Document");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            s_shown.Clear();
        }
        if (AppTasks() is { } tasks)
            foreach (int id in s_shown.Keys.Where(id => tasks.All(t => t.Id != id)).ToList())
                s_shown.Remove(id);
    }

    /// <summary>Writes <c>files/windows.json</c> on a worker thread (task ids, locations, file names only).</summary>
    private void SaveShown()
    {
        var entries = Shown().Select(p => (Task: p.Key, p.Value.Location, p.Value.Name)).ToList();
        string path = WindowsPath;
        lock (ShownIoGate)
        {
            s_shownIo = s_shownIo.ContinueWith(_ =>
            {
                try
                {
                    using (var stream = File.Create(path + ".new"))
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        writer.WriteStartArray();
                        foreach (var e in entries)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("task", e.Task);
                            writer.WriteString("location", e.Location);
                            writer.WriteString("name", e.Name);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    File.Move(path + ".new", path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ one window per document

    /// <summary>Another running window shows <paramref name="location"/>.</summary>
    private MainActivity? LiveWindowShowing(string location) =>
        s_windows.FirstOrDefault(w => !ReferenceEquals(w, this) && !w.IsFinishing && w._uri?.ToString() == location);

    /// <summary>No other running window shows <paramref name="location"/>, so this window's kept edits for it are its own.</summary>
    private bool OwnsLocation(string location) => LiveWindowShowing(location) == null;

    /// <summary>Another window (running, or a task not recreated yet) shows <paramref name="location"/>.</summary>
    private bool IsOpenInAnotherWindow(string location) =>
        LiveWindowShowing(location) != null || Shown().Any(p => p.Key != TaskId && p.Value.Location == location);

    /// <summary>The kept-edits keys (MainActivity.KeptEdits.cs) of the documents every window shows, this one included.</summary>
    private HashSet<string> ShownLocationKeys()
    {
        var keys = ShownElsewhereKeys();
        if (_uri != null) keys.Add(LocationKey(_uri.ToString()!));
        return keys;
    }

    /// <summary>The kept-edits keys of the documents the other windows show.</summary>
    private HashSet<string> ShownElsewhereKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in s_windows)
            if (!ReferenceEquals(w, this) && w._uri != null) keys.Add(LocationKey(w._uri.ToString()!));
        foreach (var p in Shown())
            if (p.Key != TaskId) keys.Add(LocationKey(p.Value.Location));
        return keys;
    }

    /// <summary>
    /// When another window shows the document, brings that window forward and answers true (the
    /// document is not opened twice: two windows would each keep edits for it).
    /// </summary>
    private bool SwitchToWindowShowing(Uri uri)
    {
        string location = uri.ToString()!;
        var live = LiveWindowShowing(location);
        int? task = live?.TaskId ?? Shown().Where(p => p.Key != TaskId && p.Value.Location == location).Select(p => (int?)p.Key).FirstOrDefault();
        if (task == null || task == TaskId) return false;
        if (AppTasks()?.FirstOrDefault(t => t.Id == task).Task is { } appTask)
        {
            try
            {
                appTask.MoveToFront();
                return true;
            }
            catch (Java.Lang.Exception)
            {
                // The task ended meanwhile.
            }
        }
        if (live != null)
        {
            // In the task of the app that opened it ("Open with"): this app cannot bring that task forward.
            Tell("This document is already open in another window, in the app it was opened from.");
            return true;
        }
        // A task that ended: its entry was out of date.
        Shown().Remove(task.Value);
        SaveShown();
        return false;
    }

    /// <summary>
    /// A document another app (or "Open in new window") hands to a new window: when another window
    /// shows it, that window comes forward and this empty one closes.
    /// </summary>
    private void OpenIncoming(Uri uri, Intent? intent)
    {
        if (SwitchToWindowShowing(uri))
        {
            CloseEmptyWindow();
            return;
        }
        if (intent?.GetBooleanExtra(ExtraFromRecent, false) == true && _recent.Find(uri.ToString()!) is { } entry) OpenRecent(entry);
        else _ = OpenUriAsync(uri, null);
    }

    private void CloseEmptyWindow()
    {
        if (OwnsTask()) FinishAndRemoveTask();
        else Finish();
    }

    /// <summary>
    /// Keeps the picker's grant across restarts (read, and write when the picker gave it), so the
    /// recent list, a new window and a restore after Android ended the process can open the file again.
    /// </summary>
    private void KeepGrant(Uri uri, Intent data)
    {
        var granted = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        try
        {
            ContentResolver?.TakePersistableUriPermission(uri, granted | ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
            try
            {
                ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException)
            {
                // Not persistable: the file opens now, but is not kept in the recent list.
            }
        }
    }

    // ------------------------------------------------------------------ open in new window

    /// <summary>⋮ → Open in new window: a PDF from the picker or the recent list, in a window of its own.</summary>
    private void OpenInNewWindow()
    {
        var recent = _recent.Items.ToList();
        if (recent.Count == 0)
        {
            PickForNewWindow();
            return;
        }
        var labels = new[] { "Choose a PDF…" }.Concat(recent.Select(d => $"{d.Name}  ·  page {d.Page}")).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle("Open in new window")!
            .SetItems(labels, (_, e) =>
            {
                if (e.Which == 0) PickForNewWindow();
                else if (Uri.Parse(recent[e.Which - 1].Location) is { } uri) OpenWindowFor(uri, fromRecent: true);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void PickForNewWindow()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/pdf");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartForResult(intent, (result, data) =>
        {
            if (result != Result.Ok || data?.Data is not { } uri) return;
            KeepGrant(uri, data);
            OpenWindowFor(uri, fromRecent: false);
        });
    }

    /// <summary>
    /// Opens <paramref name="uri"/> in a new task: ACTION_VIEW at this activity, with the read (and,
    /// when held, write) grant, beside this window in split screen. A window that shows it comes
    /// forward instead, and a window without a document takes it itself.
    /// </summary>
    private void OpenWindowFor(Uri uri, bool fromRecent)
    {
        string location = uri.ToString()!;
        if (_doc != null && _uri?.ToString() == location)
        {
            Tell("This document is already open in this window.");
            return;
        }
        if (SwitchToWindowShowing(uri)) return;
        if (_doc == null)
        {
            if (fromRecent && _recent.Find(location) is { } entry) OpenRecent(entry);
            else _ = OpenUriAsync(uri, null);
            return;
        }
        RememberPosition();
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetAction(Intent.ActionView);
        intent.SetDataAndType(uri, "application/pdf");
        if (fromRecent) intent.PutExtra(ExtraFromRecent, true);
        var flags = ActivityFlags.NewDocument | ActivityFlags.MultipleTask;
        if (CheckCallingOrSelfUriPermission(uri, ActivityFlags.GrantReadUriPermission) == Permission.Granted) flags |= ActivityFlags.GrantReadUriPermission;
        if (CheckCallingOrSelfUriPermission(uri, ActivityFlags.GrantWriteUriPermission) == Permission.Granted) flags |= ActivityFlags.GrantWriteUriPermission;
        // In split screen the new window takes the other half.
        if (IsInMultiWindowMode) flags |= ActivityFlags.LaunchAdjacent;
        intent.AddFlags(flags);
        try
        {
            StartActivity(intent);
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            Tell("A new window could not be opened for this file.");
        }
    }

    // ------------------------------------------------------------------ documents list

    private sealed record WindowRow(int Task, ActivityManager.AppTask? AppTask, MainActivity? Live, string? Location, string Name, bool Current);

    /// <summary>The app's windows, most recent first, this one included (also when it runs in another app's task).</summary>
    private List<WindowRow> WindowRows()
    {
        var rows = new List<WindowRow>();
        var shown = Shown();
        var tasks = AppTasks();
        if (tasks != null)
        {
            foreach (int stale in shown.Keys.Where(id => tasks.All(t => t.Id != id) && s_windows.All(w => w.TaskId != id)).ToList())
                shown.Remove(stale);
            foreach (var (id, task, label) in tasks)
            {
                var live = id == TaskId ? this : s_windows.FirstOrDefault(w => w.TaskId == id && !w.IsFinishing);
                bool known = shown.TryGetValue(id, out var entry);
                string? location = live != null ? live._uri?.ToString() : known ? entry.Location : null;
                string name = live != null ? live.WindowName
                    : known ? entry.Name
                    : string.IsNullOrEmpty(label) || label == "PDF Viewer" ? NoDocument : label;
                rows.Add(new WindowRow(id, task, live, location, name, id == TaskId));
            }
        }
        if (!rows.Any(r => r.Current)) rows.Insert(0, new WindowRow(TaskId, null, this, _uri?.ToString(), WindowName, true));
        return rows;
    }

    /// <summary>⋮ → Documents: the documents open in this app's windows; tap one to switch to it, ✕ closes its window.</summary>
    private void ShowDocuments()
    {
        var rows = WindowRows();
        var list = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        list.SetPadding((int)Dp(16), (int)Dp(8), (int)Dp(8), 0);
        AlertDialog? dialog = null;
        foreach (var row in rows)
        {
            var line = Bar();
            var name = new TextView(this)
            {
                Text = row.Current ? $"● {row.Name}  (this window)" : row.Name,
                Clickable = !row.Current,
                Focusable = true,
                ContentDescription = row.Current ? $"{row.Name}, this window" : $"Switch to {row.Name}",
                Gravity = GravityFlags.CenterVertical,
            };
            name.SetTextColor(row.Current ? TextColor : AccentColor);
            name.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
            name.SetSingleLine(true);
            name.Ellipsize = TextUtils.TruncateAt.Middle;
            name.SetMinHeight((int)Dp(48));
            if (!row.Current)
            {
                name.Click += (_, _) =>
                {
                    dialog?.Dismiss();
                    SwitchTo(row);
                };
            }
            var close = ToolButton("✕", $"Close the window of {row.Name}", (_, _) =>
            {
                dialog?.Dismiss();
                CloseWindowOf(row);
            });
            close.SetTextColor(AccentColor);
            line.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            line.AddView(close);
            list.AddView(line, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        var hint = new TextView(this)
        {
            Text = "Each document has its own window: switch here or in Recents, or show two side by side in split screen.",
        };
        hint.SetTextColor(TextColor);
        hint.SetPadding((int)Dp(4), (int)Dp(12), (int)Dp(8), (int)Dp(8));
        list.AddView(hint);
        var scroll = new ScrollView(this);
        scroll.AddView(list);
        dialog = new AlertDialog.Builder(this)
            .SetTitle("Documents")!
            .SetView(scroll)!
            .SetPositiveButton("Open in new window…", (_, _) => OpenInNewWindow())!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void SwitchTo(WindowRow row)
    {
        if (row.Current) return;
        if (row.AppTask != null)
        {
            try
            {
                row.AppTask.MoveToFront();
                return;
            }
            catch (Java.Lang.Exception)
            {
            }
        }
        Tell(row.Live != null
            ? "That window is in the app its document was opened from: switch to it there."
            : "That window is closed.");
    }

    /// <summary>
    /// Closes a window from the Documents list. Unsaved edits are only in that window (or its kept
    /// edits), so it is the one that asks: it comes forward and asks as Back would.
    /// </summary>
    private void CloseWindowOf(WindowRow row)
    {
        if (row.Current)
        {
            CloseWindow();
            return;
        }
        if (row.Live is { } live)
        {
            if (live._session?.IsDirty == true || live._savingBack)
            {
                try
                {
                    row.AppTask?.MoveToFront();
                }
                catch (Java.Lang.Exception)
                {
                }
            }
            live.CloseWindow();
            return;
        }
        if (row.AppTask is { } task) _ = CloseStoppedWindowAsync(task, row.Task, row.Location);
    }

    /// <summary>
    /// A window Android ended with the process: without kept edits its task is removed; with them it
    /// is reopened (they come back) and asks before it closes.
    /// </summary>
    private async Task CloseStoppedWindowAsync(ActivityManager.AppTask task, int id, string? location)
    {
        bool edits = location != null && await Task.Run(() => HasKeptEdits(location));
        try
        {
            if (edits)
            {
                s_closeAsked.Add(id);
                task.MoveToFront();
            }
            else
            {
                task.FinishAndRemoveTask();
                if (Shown().Remove(id)) SaveShown();
            }
        }
        catch (Java.Lang.Exception)
        {
            s_closeAsked.Remove(id);
        }
    }

    /// <summary>Edits are kept for <paramref name="location"/> (call on a worker thread).</summary>
    private bool HasKeptEdits(string location)
    {
        string folder = EditsFolder, key = LocationKey(location);
        lock (KeptFilesLock)
        {
            try
            {
                return KeptFiles(folder).Any(f => f.Key == key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>A close the Documents list asked while this window was not running, once its document is back.</summary>
    private void CloseIfAsked()
    {
        if (!_reopening && s_closeAsked.Remove(TaskId)) CloseWindow();
    }

    /// <summary>Closes this window and removes it from Recents, after its unsaved edits are saved or discarded.</summary>
    private void CloseWindow() => AfterDiscard(CloseEmptyWindow);
}
