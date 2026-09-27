using System.Windows;
using System.Windows.Input;

namespace PdfViewer.Views.Dialogs;

public partial class PasswordDialog : Window
{
    public string Password { get; private set; } = string.Empty;

    public PasswordDialog(string fileName)
    {
        InitializeComponent();
        MessageTextBlock.Text = $"'{fileName}' is encrypted. Please enter the password to unlock:";
        Loaded += (s, e) => PasswordInputBox.Focus();
        Closed += (s, e) => PasswordInputBox.Clear();
    }

    /// <summary>The same prompt for another password, such as the permissions password.</summary>
    public PasswordDialog(string fileName, string heading, string message, string okText) : this(fileName)
    {
        Title = heading;
        HeadingTextBlock.Text = heading;
        MessageTextBlock.Text = message;
        OkButton.Content = okText;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordInputBox.Password;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OkButton_Click(sender, e);
        }
    }
}
