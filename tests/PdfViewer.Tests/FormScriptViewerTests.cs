using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Form scripts in the viewer: a commit recalculates the fields that depend on it (all their
/// widgets update, in the same revision), a refused value is explained and not written, and
/// the editor keeps letters out of a number field.
/// </summary>
public class FormScriptViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FormScriptViewer_" + Guid.NewGuid().ToString("N"));
    public FormScriptViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(MainViewModel Vm, string Path)> Open()
    {
        string path = Path.Combine(_dir, "order.pdf");
        File.WriteAllBytes(path, ScriptedFormFixture.Build());
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        return (vm, path);
    }

    private static FormFieldViewModel F(MainViewModel vm, string name) => vm.AllFormFields.Single(f => f.FullName == name);

    [Fact]
    public async Task Commit_RecalculatesDependentFields()
    {
        var (vm, path) = await Open();
        Assert.True(await vm.CommitFormFieldAsync(F(vm, "qty"), new PdfFieldChange("qty", Text: "3")));
        Assert.True(await vm.CommitFormFieldAsync(F(vm, "price"), new PdfFieldChange("price", Text: "$19.99")));
        Assert.Equal("19.99", F(vm, "price").Value);
        Assert.Equal("59.97", F(vm, "total").Value);
        Assert.Equal("69.97", F(vm, "subtotal").Value);

        await vm.SaveAsync();
        var reopened = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await reopened.LoadDocumentAsync(path);
        Assert.Equal("59.97", F(reopened, "total").Value);
    }

    [Fact]
    public async Task RefusedValue_IsExplained_AndNotWritten()
    {
        var (vm, _) = await Open();
        string? alert = null;
        vm.ShowMessageBoxAction = (m, _, _, _) => alert = m;
        bool reverted = false;
        var price = F(vm, "price");
        price.RevertRequested = () => reverted = true;
        Assert.False(await vm.CommitFormFieldAsync(price, new PdfFieldChange("price", Text: "5000")));
        Assert.Equal("Invalid value: must be greater than or equal to 0 and less than or equal to 1000.", alert);
        Assert.True(reverted);
        Assert.Equal(string.Empty, price.Value);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task NumberEditor_KeepsLettersOut()
    {
        var (vm, _) = await Open();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new FormFieldsLayer { Page = vm.Pages[0], Width = 800, Height = 800 };
                var qty = layer.Children.OfType<Border>().Select(b => b.Child).OfType<TextBox>()
                    .Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "qty");
                var letter = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice,
                    new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, qty, "x"))
                { RoutedEvent = System.Windows.UIElement.PreviewTextInputEvent };
                qty.RaiseEvent(letter);
                Assert.True(letter.Handled, "a letter is refused");
                var digit = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice,
                    new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, qty, "7"))
                { RoutedEvent = System.Windows.UIElement.PreviewTextInputEvent };
                qty.RaiseEvent(digit);
                Assert.False(digit.Handled, "a digit is accepted");
                var total = layer.Children.OfType<Border>().Select(b => b.Child).OfType<TextBox>()
                    .Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "total");
                Assert.Equal("Calculated", System.Windows.Automation.AutomationProperties.GetItemStatus(total));
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
