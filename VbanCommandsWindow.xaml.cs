using System.IO;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace VoicemeeterDelay;

public partial class VbanCommandsWindow : Window
{
    private const string CommandsFileName = "VBAN_COMMANDS.md";

    public VbanCommandsWindow()
    {
        InitializeComponent();
        CommandsTextBox.Text = LoadCommandsText();
    }

    private static string LoadCommandsText()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, CommandsFileName),
            Path.Combine(AppContext.BaseDirectory, "outputs", "VoicemeeterDelay", CommandsFileName)
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        return "VBAN command reference file was not found." + Environment.NewLine
            + "Expected file: " + CommandsFileName;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CommandsTextBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
