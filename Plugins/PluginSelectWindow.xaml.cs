using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CellEditor.Plugins;

public partial class PluginSelectWindow : Window
{
    readonly IReadOnlyList<PluginRow> rows;
    readonly Dictionary<string, PluginRow> byName;
    readonly ICollectionView? view;
    bool cascade;

    public PluginSelectWindow(IReadOnlyList<PluginRow> rows)
    {
        this.rows = rows;
        byName = new Dictionary<string, PluginRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            byName[row.FileName] = row;
        }

        cascade = true;
        foreach (var row in rows)
        {
            if (row.IsChecked)
            {
                CheckMasters(row);
            }
        }

        cascade = false;

        InitializeComponent();
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = Matches;
        PluginList.ItemsSource = view;
        foreach (var row in rows)
        {
            row.PropertyChanged += RowChanged;
        }

        UpdateCount();
    }

    public IReadOnlyList<PluginRow> Selected => rows.Where(row => row.IsChecked && !row.Missing).ToArray();

    void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginRow.IsChecked) || sender is not PluginRow row || cascade)
        {
            return;
        }

        cascade = true;
        try
        {
            if (row.IsChecked)
            {
                CheckMasters(row);
            }
            else
            {
                UncheckDependents(row);
            }
        }
        finally
        {
            cascade = false;
            UpdateCount();
        }
    }

    void CheckMasters(PluginRow row)
    {
        foreach (var name in row.Masters)
        {
            if (!byName.TryGetValue(name, out var master) || master.Missing || master.IsChecked)
            {
                continue;
            }

            master.IsChecked = true;
            CheckMasters(master);
        }
    }

    void UncheckDependents(PluginRow row)
    {
        foreach (var other in rows)
        {
            if (!other.IsChecked || !other.Masters.Contains(row.FileName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            other.IsChecked = false;
            UncheckDependents(other);
        }
    }

    bool Matches(object item)
    {
        if (item is not PluginRow row)
        {
            return false;
        }

        var filter = FilterBox?.Text;
        return string.IsNullOrWhiteSpace(filter)
            || row.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        view?.Refresh();
    }

    void None_Click(object sender, RoutedEventArgs e)
    {
        cascade = true;
        foreach (var row in rows)
        {
            row.IsChecked = false;
        }

        cascade = false;
        UpdateCount();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    void UpdateCount()
    {
        var checkedCount = rows.Count(row => row.IsChecked);
        CountText.Text = checkedCount + " / " + rows.Count;
    }
}
