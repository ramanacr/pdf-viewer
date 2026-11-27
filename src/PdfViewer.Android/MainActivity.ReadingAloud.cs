using System.Text;
using Android.App;
using Android.OS;
using Android.Speech.Tts;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Read aloud: the device's text-to-speech voice (Android's TextToSpeech, which needs no
/// permission and runs on the device) reads the document from the page in view onward, page by
/// page, in pieces of a few sentences (under TextToSpeech.MaxSpeechInputLength), and the viewer
/// follows to the page being read. A slim bar at the bottom of the pages pauses, resumes and stops
/// it. Pages without a text layer are skipped and counted. Reading stops when another document
/// is opened or the document is closed, and the voice is released when the activity is destroyed
/// (seen through the application's activity callbacks). As on Windows, it needs the document's
/// permission to extract text for accessibility.
/// </summary>
public sealed partial class MainActivity
{
    private TextToSpeech? _tts;
    private bool _ttsReady;
    private Action<bool>? _ttsPending;
    private ReadAloudSession? _reading;
    private LinearLayout? _readBar;
    private TextView? _readBarLabel, _readPauseButton;

    /// <summary>The document whose accessibility permission was checked last, and the answer.</summary>
    private (ViewerDocument? Doc, bool Allowed) _accessibilityChecked;

    /// <summary>Read aloud is speaking (the menu offers to stop it).</summary>
    private bool IsReadingAloud => _reading != null;

    private sealed class ReadAloudSession(ViewerDocument doc, int page)
    {
        public ViewerDocument Doc { get; } = doc;
        public int StartPage { get; } = page;
        public int Page = page;
        public List<string> Chunks = new();
        public int Chunk;
        public bool Paused;

        /// <summary>Changes at every utterance, pause and stop, so that a late "done" of an older one is ignored.</summary>
        public int Generation;

        public int SilentPages;
        public CancellationTokenSource Cts { get; } = new();
    }

    private async void ToggleReadAloud()
    {
        if (_reading != null)
        {
            StopReadingAloud(null);
            return;
        }
        if (!NeedDocument()) return;
        var doc = _doc!;
        bool allowed;
        try
        {
            allowed = await AllowsAccessibilityAsync(doc);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFailure(ex);
            return;
        }
        if (!ReferenceEquals(doc, _doc) || _reading != null) return;
        if (!allowed)
        {
            Toast.MakeText(this, NoAccessibilityMessage, ToastLength.Long)?.Show();
            return;
        }
        var session = new ReadAloudSession(doc, Math.Clamp(_pages.CurrentPage, 1, Math.Max(1, doc.PageCount)));
        _reading = session;
        ShowReadBar(session);
        EnsureVoice(ok =>
        {
            if (_reading != session) return;
            if (!ok)
            {
                StopReadingAloud("This device has no text-to-speech voice. Install or turn one on in the system settings (Text-to-speech output).");
                return;
            }
            _ = ReadPageAloudAsync(session);
        });
    }

    /// <summary>The document lets assistive technology extract its text: always when it allows copying; otherwise its permissions are read.</summary>
    private async Task<bool> AllowsAccessibilityAsync(ViewerDocument doc)
    {
        if (doc.CanCopy) return true;
        if (ReferenceEquals(_accessibilityChecked.Doc, doc)) return _accessibilityChecked.Allowed;
        if (CurrentBytes() is not { } bytes) return false;
        bool allowed = await Task.Run(() =>
        {
            using var engine = OpenForTool(bytes);
            return AllowsAccessibility(engine);
        });
        _accessibilityChecked = (doc, allowed);
        return allowed;
    }

    /// <summary>Starts the device's voice once; <paramref name="ready"/> runs on the UI thread with whether it works.</summary>
    private void EnsureVoice(Action<bool> ready)
    {
        if (_tts != null && _ttsReady)
        {
            ready(true);
            return;
        }
        _ttsPending = ready;
        if (_tts != null) return; // starting: the pending callback runs when it is ready
        _tts = new TextToSpeech(this, new VoiceInit(status => RunOnUiThread(() =>
        {
            if (_tts == null) return;
            _ttsReady = status == OperationResult.Success;
            if (_ttsReady)
            {
                _tts.SetOnUtteranceProgressListener(new ReadAloudProgress(this));
            }
            else
            {
                _tts.Shutdown();
                _tts = null;
            }
            var pending = _ttsPending;
            _ttsPending = null;
            pending?.Invoke(_ttsReady);
        })));
    }

    /// <summary>Reads the session's page; pages without text are skipped. Speaking starts with its first piece.</summary>
    private async Task ReadPageAloudAsync(ReadAloudSession session)
    {
        while (true)
        {
            if (_reading != session) return;
            if (!ReferenceEquals(session.Doc, _doc))
            {
                StopReadingAloud(null);
                return;
            }
            if (session.Page > session.Doc.PageCount)
            {
                int pages = session.Doc.PageCount - session.StartPage + 1;
                StopReadingAloud(session.SilentPages >= pages
                    ? "Nothing to read: these pages have no text layer (they may be scanned pictures of text)."
                    : session.SilentPages > 0
                        ? $"Finished reading. {session.SilentPages} page(s) without text were skipped."
                        : "Finished reading.");
                return;
            }
            string text;
            try
            {
                int page = session.Page;
                text = await Task.Run(() => session.Doc.GetPageTextAsync(page, session.Cts.Token));
            }
            catch (Exception ex) when (ex is System.OperationCanceledException or ObjectDisposedException)
            {
                if (_reading == session) StopReadingAloud(null);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StopReadingAloud($"Reading stopped: page {session.Page} could not be read ({ex.GetType().Name}).");
                return;
            }
            if (_reading != session) return;
            var chunks = SpeechChunks(text);
            if (chunks.Count == 0)
            {
                session.SilentPages++;
                session.Page++;
                continue;
            }
            session.Chunks = chunks;
            session.Chunk = 0;
            if (ReferenceEquals(session.Doc, _doc) && _pages.CurrentPage != session.Page) _pages.GoToPage(session.Page);
            UpdateReadBar(session);
            SpeakChunk(session);
            return;
        }
    }

    private void SpeakChunk(ReadAloudSession session)
    {
        if (_reading != session || session.Paused || _tts == null || session.Chunk >= session.Chunks.Count) return;
        if (!ReferenceEquals(session.Doc, _doc))
        {
            StopReadingAloud(null);
            return;
        }
        session.Generation++;
        var result = _tts.Speak(session.Chunks[session.Chunk], QueueMode.Flush, null, session.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (result == OperationResult.Error) StopReadingAloud("Reading stopped: the voice could not speak.");
    }

    /// <summary>A piece was spoken (on the UI thread): the next piece, or the next page.</summary>
    private void SpokenChunk(string? utterance, bool failed)
    {
        if (_reading is not { } session || session.Paused) return;
        if (utterance != session.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
        if (failed)
        {
            StopReadingAloud("Reading stopped: the voice reported an error.");
            return;
        }
        session.Chunk++;
        if (session.Chunk < session.Chunks.Count)
        {
            SpeakChunk(session);
            return;
        }
        session.Page++;
        _ = ReadPageAloudAsync(session);
    }

    private void PauseOrResumeReading()
    {
        if (_reading is not { } session) return;
        session.Paused = !session.Paused;
        if (session.Paused)
        {
            session.Generation++;
            _tts?.Stop();
        }
        else
        {
            // The piece that was cut off is read again from its start.
            SpeakChunk(session);
        }
        UpdateReadBar(session);
    }

    /// <summary>Stops reading; <paramref name="message"/> (never document text) is told when given.</summary>
    private void StopReadingAloud(string? message)
    {
        if (_reading is not { } session) return;
        _reading = null;
        session.Generation++;
        session.Cts.Cancel();
        _tts?.Stop();
        if (_readBar != null)
        {
            _frame.RemoveView(_readBar);
            _readBar = null;
        }
        if (message != null) Toast.MakeText(this, message, ToastLength.Long)?.Show();
    }

    /// <summary>The activity is destroyed: the voice is stopped and released.</summary>
    private void ShutDownVoice()
    {
        if (_reading is { } session)
        {
            _reading = null;
            session.Cts.Cancel();
        }
        _ttsPending = null;
        _tts?.Stop();
        _tts?.Shutdown();
        _tts = null;
        _ttsReady = false;
    }

    /// <summary>The read aloud bar at the bottom of the pages: what is read, Pause or Resume, and Stop.</summary>
    private void ShowReadBar(ReadAloudSession session)
    {
        if (_readBar != null) _frame.RemoveView(_readBar);
        var bar = Bar();
        bar.SetBackgroundColor(IsDark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3"));
        // Above the pages and a tool screen.
        bar.Elevation = Dp(10);
        var label = new TextView(this) { Text = "Starting the voice…" };
        label.SetTextColor(TextColor);
        label.SetPadding((int)Dp(12), 0, (int)Dp(8), 0);
        label.SetSingleLine(true);
        label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        var pause = ToolButton("Pause", "Pause reading aloud", (_, _) => PauseOrResumeReading());
        var stop = ToolButton("Stop", "Stop reading aloud", (_, _) => StopReadingAloud(null));
        pause.SetTextColor(AccentColor);
        stop.SetTextColor(AccentColor);
        bar.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bar.AddView(pause);
        bar.AddView(stop);
        _frame.AddView(bar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        _readBar = bar;
        _readBarLabel = label;
        _readPauseButton = pause;
    }

    private void UpdateReadBar(ReadAloudSession session)
    {
        if (_readBarLabel == null || _readPauseButton == null) return;
        _readBarLabel.Text = (session.Paused ? "Paused on page " : "Reading page ") + $"{session.Page} of {session.Doc.PageCount}";
        _readPauseButton.Text = session.Paused ? "Resume" : "Pause";
        _readPauseButton.ContentDescription = session.Paused ? "Resume reading aloud" : "Pause reading aloud";
    }

    /// <summary>
    /// A page's text in pieces for the voice: whole sentences, about 500 characters each, so that
    /// pausing and following are prompt, and never more than the voice takes at once.
    /// </summary>
    internal static List<string> SpeechChunks(string text)
    {
        const int Target = 500;
        int max = Math.Max(100, Math.Min(TextToSpeech.MaxSpeechInputLength, 4000) - 1);
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var chunks = new List<string>();
        var current = new StringBuilder();
        int start = 0;
        for (int i = 0; i <= flat.Length; i++)
        {
            bool end = i == flat.Length || (flat[i] == ' ' && i > 0 && flat[i - 1] is '.' or '!' or '?' or '…' or ';' or ':');
            if (!end) continue;
            string sentence = flat[start..i].Trim();
            start = i;
            if (sentence.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + sentence.Length > Target)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            // A "sentence" longer than the voice takes is cut at spaces.
            while (sentence.Length > max)
            {
                int cut = sentence.LastIndexOf(' ', max);
                if (cut <= 0) cut = max;
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                chunks.Add(sentence[..cut].Trim());
                sentence = sentence[cut..].Trim();
            }
            if (sentence.Length == 0) continue;
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private sealed class VoiceInit(Action<OperationResult> done) : Java.Lang.Object, TextToSpeech.IOnInitListener
    {
        public void OnInit(OperationResult status) => done(status);
    }

    /// <summary>The voice's progress, on its own thread: handed to the UI thread.</summary>
    private sealed class ReadAloudProgress(MainActivity activity) : UtteranceProgressListener
    {
        public override void OnStart(string? utteranceId)
        {
        }

        public override void OnDone(string? utteranceId) => activity.RunOnUiThread(() => activity.SpokenChunk(utteranceId, false));

        [Obsolete("Android calls OnError(string, int) from API 21; this one remains abstract.")]
        public override void OnError(string? utteranceId) => activity.RunOnUiThread(() => activity.SpokenChunk(utteranceId, true));

        public override void OnError(string? utteranceId, [global::Android.Runtime.GeneratedEnum] TextToSpeechError errorCode) =>
            activity.RunOnUiThread(() => activity.SpokenChunk(utteranceId, true));
    }
}
