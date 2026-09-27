using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Filling forms in the viewer: fields found on open and placed on their pages, commits applied
/// as incremental revisions, Save writing the revision as it is (the original bytes untouched,
/// so earlier signatures stay valid), and the page layer's editors.
/// </summary>
public class FormFillingViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FormFillingViewer_" + Guid.NewGuid().ToString("N"));

    public FormFillingViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteForm()
    {
        string path = Path.Combine(_dir, "form.pdf");
        File.WriteAllBytes(path, FormPdfFixture.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task OpeningAForm_PlacesEveryWidgetOnItsPage()
    {
        var vm = await Open(WriteForm());
        Assert.True(vm.HasForm);
        Assert.Null(vm.FormReadOnlyReason);
        var fields = vm.AllFormFields;
        Assert.Equal(8, fields.Count); // 6 fields + a radio group with two widgets
        var name = fields.Single(f => f.FullName == "person.name");
        Assert.Equal(PdfFormFieldKind.Text, name.Kind);
        // Rect [20 360 280 380] on a 300 x 400 page: top-left origin, normalized.
        Assert.Equal(20 / 300.0, name.Bounds.X, 3);
        Assert.Equal(1 - 380 / 400.0, name.Bounds.Y, 3);
        Assert.Equal(2, fields.Count(f => f.FullName == "colour"));
        Assert.All(fields, f => Assert.Equal(1, f.PageNumber));
    }

    [Fact]
    public async Task Commits_AreRevisions_AndSaveWritesThemIncrementally()
    {
        string path = WriteForm();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        var name = vm.AllFormFields.Single(f => f.FullName == "person.name");
        Assert.True(await vm.CommitFormFieldAsync(name, new PdfFieldChange("person.name", Text: "Grace Hopper")));
        Assert.Equal("Grace Hopper", name.Value);
        Assert.True(vm.HasUnsavedChanges);

        var blue = vm.AllFormFields.Single(f => f.FullName == "colour" && f.OnState == "Blue");
        Assert.True(await vm.CommitFormFieldAsync(blue, new PdfFieldChange("colour", Choice: "Blue")));
        Assert.True(blue.IsOn);
        Assert.False(vm.AllFormFields.Single(f => f.FullName == "colour" && f.OnState == "Red").IsOn);

        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges);
        byte[] saved = File.ReadAllBytes(path);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the original revision is untouched");
        Assert.True(saved.Length > original.Length);

        var reopened = await Open(path);
        Assert.Equal("Grace Hopper", reopened.AllFormFields.Single(f => f.FullName == "person.name").Value);
        Assert.Equal("Blue", reopened.AllFormFields.First(f => f.FullName == "colour").Value);
    }

    [Fact]
    public async Task FormLayer_CreatesNativeEditorsWithAccessibleNames()
    {
        var vm = await Open(WriteForm());
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new FormFieldsLayer { Page = vm.Pages[0], Width = 600, Height = 800 };
                var editors = layer.Children.OfType<System.Windows.Controls.Border>().Select(b => b.Child).ToList();
                Assert.Equal(8, editors.Count);
                Assert.Contains(editors, e => e is TextBox t && t.AcceptsReturn);         // multi-line notes
                Assert.Contains(editors, e => e is TextBox t && t.MaxLength == 5);         // comb zip
                Assert.Equal(3, editors.Count(e => e is ToggleButton));                    // check box + two radios
                Assert.Contains(editors, e => e is ComboBox c && c.Items.Count == 2);
                Assert.Contains(editors, e => e is ListBox l && l.Items.Count == 3);
                Assert.All(editors, e => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(e))));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
    }
}
