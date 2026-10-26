using System.Windows;
using System.Windows.Controls;
using CellEditor.Cells;

namespace CellEditor;

static class TreeViewHelper
{
    public static TreeViewItem? FindItem(ItemsControl parent, object data)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(data) is TreeViewItem direct)
        {
            return direct;
        }

        for (var i = 0; i < parent.Items.Count; i++)
        {
            var item = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
            if (item == null)
            {
                continue;
            }

            if (ReferenceEquals(item.DataContext, data))
            {
                return item;
            }

            var nested = FindItem(item, data);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    public static void BringSceneIntoView(TreeView tree, SceneObject scene)
    {
        tree.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            () =>
            {
                tree.UpdateLayout();
                var item = FindItem(tree, scene);
                item?.BringIntoView();
                tree.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    () =>
                    {
                        tree.UpdateLayout();
                        FindItem(tree, scene)?.BringIntoView();
                    });
            });
    }
}
