using System.Windows;
using System.Windows.Input;

namespace CellEditor.Cells;

public partial class CellSearchWindow : Window
{
    readonly CellBrowser cells;

    public CellSearchWindow(CellBrowser cells)
    {
        this.cells = cells;
        InitializeComponent();
        Loaded += (_, _) => Query.Focus();
    }

    public CellEntry? Chosen { get; private set; }

    void Query_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Hits.ItemsSource = cells.Search(Query.Text, 40);
    }

    void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Hits.Items.Count > 0)
        {
            Hits.Focus();
            Hits.SelectedIndex = 0;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            Accept(Hits.SelectedItem as CellEntry ?? (Hits.Items.Count > 0 ? Hits.Items[0] as CellEntry : null));
        }
    }

    void Hits_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept(Hits.SelectedItem as CellEntry);
        }
    }

    void Hits_Open(object sender, MouseButtonEventArgs e)
    {
        Accept(Hits.SelectedItem as CellEntry);
    }

    void Accept(CellEntry? entry)
    {
        if (entry == null)
        {
            return;
        }

        Chosen = entry;
        DialogResult = true;
    }
}
