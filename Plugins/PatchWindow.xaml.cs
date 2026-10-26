using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CellEditor.Plugins;

public partial class PatchWindow : Window
{
    public PatchWindow(int changed)
    {
        InitializeComponent();
        SummaryText.Text = changed == 1
            ? "1 changed object will be written."
            : changed + " changed objects will be written.";
        var own = PluginCatalog.OwnPlugins();
        ExistingBox.ItemsSource = own.ToList();
        if (own.Count > 0)
        {
            ExistingBox.SelectedIndex = 0;
        }

        UpdateNewPath();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Full path of the plugin to write.</summary>
    public string TargetPath { get; private set; } = "";

    /// <summary>True when a new plugin and its "[MY] ..." mod folder are created.</summary>
    public bool IsNew { get; private set; }

    /// <summary>Mod folder name of a new plugin, for enabling it in MO2.</summary>
    public string ModFolder { get; private set; } = "";

    string? browsed;

    void Target_Changed(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (NewRadio != null)
        {
            NewRadio.IsChecked = true;
        }

        UpdateNewPath();
    }

    void Existing_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingBox.SelectedItem is PluginRow row && ExistingRadio != null)
        {
            ExistingRadio.IsChecked = true;
            ExistingPathText.Text = row.FullPath;
        }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Plugin (*.esp;*.esm;*.esl)|*.esp;*.esm;*.esl",
            Title = "Existing patch",
            InitialDirectory = PluginCatalog.ModsDir,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        browsed = dialog.FileName;
        ExistingRadio.IsChecked = true;
        ExistingBox.SelectedItem = null;
        ExistingPathText.Text = browsed;
    }

    string CleanName()
    {
        var name = NameBox.Text.Trim();
        foreach (var ext in new[] { ".esp", ".esm", ".esl" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length].TrimEnd();
            }
        }

        return name;
    }

    void UpdateNewPath()
    {
        if (NewPathText == null)
        {
            return;
        }

        var name = CleanName();
        NewPathText.Text = name.Length == 0
            ? ""
            : Path.Combine(PluginCatalog.ModsDir, "[MY] " + name, name + ".esp") + "   ESP-FE, turned on in MO2";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (NewRadio.IsChecked == true)
        {
            var name = CleanName();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Fail("Type a plugin name without \\ / : * ? \" < > |.");
                return;
            }

            var folder = "[MY] " + name;
            var path = Path.Combine(PluginCatalog.ModsDir, folder, name + ".esp");
            if (File.Exists(path) || PluginCatalog.Load().Any(row => row.FileName.Equals(name + ".esp", StringComparison.OrdinalIgnoreCase)))
            {
                Fail(name + ".esp already exists. Pick it under Existing plugin.");
                return;
            }

            TargetPath = path;
            IsNew = true;
            ModFolder = folder;
        }
        else
        {
            var path = ExistingBox.SelectedItem is PluginRow row ? row.FullPath : browsed;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Fail("Pick an existing plugin.");
                return;
            }

            TargetPath = path;
            IsNew = false;
        }

        DialogResult = true;
    }

    void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
