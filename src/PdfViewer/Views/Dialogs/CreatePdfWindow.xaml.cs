using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Create PDF from Images and Combine Files: the file list (drag to reorder, or drop files in), the options, progress and Cancel.</summary>
public partial class CreatePdfWindow : Window
{
    private const string ItemFormat = "PdfViewer.CreatePdfItem";
    private readonly CreatePdfViewModel _vm;
    private Point _dragStart;
    private bool _closeWhenStopped;

    public CreatePdfWindow(CreatePdfViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = vm.Title;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CreatePdfViewModel.IsBusy)) return;
            CancelButton.Content = vm.IsBusy ? "Stop" : "Cancel";
            if (!vm.IsBusy && _closeWhenStopped) Dispatcher.BeginInvoke(Close);
        };
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.CreateAndOpenAsync()) Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) _vm.Cancel();
        else Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing while it runs stops it; the window goes once the run has cleaned up.
        if (_vm.IsBusy)
        {
            _closeWhenStopped = true;
            _vm.Cancel();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add files", Filter = _vm.FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) _vm.AddFiles(dialog.FileNames);
    }

    private CreatePdfItem? Selected => FileList.SelectedItem as CreatePdfItem;

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) { _vm.MoveUp(item); FileList.SelectedItem = item; }
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) { _vm.MoveDown(item); FileList.SelectedItem = item; }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) _vm.Remove(item);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Selected is not { } item) return;
        if (e.Key == Key.Delete) { _vm.Remove(item); e.Handled = true; }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt) { _vm.MoveUp(item); FileList.SelectedItem = item; e.Handled = true; }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt) { _vm.MoveDown(item); FileList.SelectedItem = item; e.Handled = true; }
    }

    // Dragging a row moves it; dropping files from Explorer adds them where they land.

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(FileList);

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _vm.IsBusy) return;
        var delta = e.GetPosition(FileList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemAt(_dragStart) is not { } item) return;
        DragDrop.DoDragDrop(FileList, new DataObject(ItemFormat, item), DragDropEffects.Move);
    }

    private CreatePdfItem? ItemAt(Point point) =>
        (FileList.InputHitTest(point) as DependencyObject) is { } hit && ItemsControl.ContainerFromElement(FileList, hit) is ListBoxItem container
            ? container.DataContext as CreatePdfItem
            : null;

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ItemFormat) ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop) && !_vm.IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        var target = ItemAt(e.GetPosition(FileList));
        if (e.Data.GetData(ItemFormat) is CreatePdfItem moved)
        {
            int to = target != null ? _vm.Items.IndexOf(target) : _vm.Items.Count - 1;
            _vm.Move(_vm.Items.IndexOf(moved), to);
            FileList.SelectedItem = moved;
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && !_vm.IsBusy)
        {
            int at = target != null ? _vm.Items.IndexOf(target) : -1;
            int before = _vm.Items.Count;
            _vm.AddFiles(files.Where(File.Exists));
            if (at >= 0)
                for (int i = before; i < _vm.Items.Count; i++) _vm.Move(i, at + i - before);
        }
        e.Handled = true;
    }
}
