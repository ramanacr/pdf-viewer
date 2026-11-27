using Android.Widget;

namespace PdfViewer.Android;

/// <summary>Reading, accessibility and inspection (stub; to be implemented).</summary>
public sealed partial class MainActivity
{
    /// <summary>Read aloud is speaking (the menu offers to stop it).</summary>
    private bool IsReadingAloud => false;

    private void ShowReadMode() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();

    private void ToggleReadAloud() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();

    private void AccessibilityCheck() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();

    private void MakeAccessible() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();

    private void CompareDocuments() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();

    private void RunPreflight() => Toast.MakeText(this, "Coming soon.", ToastLength.Short)?.Show();
}
