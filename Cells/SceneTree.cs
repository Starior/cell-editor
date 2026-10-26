using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CellEditor.Cells;

public sealed class PluginNode : INotifyPropertyChanged
{
    public required string Name { get; init; }

    public PluginNode()
    {
        Objects.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
    }

    public ObservableCollection<SceneObject> Objects { get; } = new();

    public string Header => Name + "   (" + Objects.Count + ")";

    public int LoadIndex { get; init; }

    /// <summary>False for the cell's own plugin and its masters.</summary>
    public bool CanDisable { get; init; } = true;

    bool isEnabled = true;

    public bool IsEnabled
    {
        get => isEnabled;
        set => Set(ref isEnabled, value);
    }

    bool isExpanded;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class CategoryFilter : INotifyPropertyChanged
{
    public required string Name { get; init; }

    public required string Color { get; init; }

    public int Count { get; init; }

    public string Label => Name + " " + Count;

    bool isOn = true;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (isOn == value)
            {
                return;
            }

            isOn = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SceneObject : INotifyPropertyChanged
{
    public required PlacedMesh Mesh { get; init; }

    public PluginNode? Group { get; set; }

    public PlacedVersion? ActiveVersion { get; set; }

    PlacedVersion? winner;

    /// <summary>Newest version from a plugin that is checked in the tree. Null when none is.</summary>
    public PlacedVersion? Winner
    {
        get => winner;
        set
        {
            winner = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Winner)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownDisabled)));
        }
    }

    string category = Categories.Other;

    public string Category
    {
        get => category;
        set
        {
            category = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Category)));
        }
    }

    string categoryColor = "#6A6A6A";

    public string CategoryColor
    {
        get => categoryColor;
        set
        {
            categoryColor = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryColor)));
        }
    }

    /// <summary>The plugin that adds the reference, or every plugin that edits it, is unchecked.</summary>
    public bool PluginOff { get; set; }

    public bool BaseDisabled => Winner?.Disabled ?? Mesh.Disabled;

    /// <summary>Skyrim-space position typed in the properties panel. Null uses the plugin placement.</summary>
    public Vector3? EditedPosition { get; set; }

    /// <summary>Euler angles in radians typed in the properties panel. Null uses the plugin placement.</summary>
    public Vector3? EditedAngles { get; set; }

    /// <summary>Base object whose model replaces this reference's model in the preview. Null keeps the plugin model.</summary>
    public ModelEntry? Swap { get; set; }

    /// <summary>The swapped model placed at the plugin pose of the active version.</summary>
    public IReadOnlyList<PlacedPart>? SwapParts { get; set; }

    /// <summary>Texture mod picked in the properties panel. Null is the load-order winner.</summary>
    public string? TextureMod { get; set; }

    bool editedDisabled;

    /// <summary>Disabled in the editor. Saved to a patch as Initially Disabled with the player as opposite enable parent.</summary>
    public bool EditedDisabled
    {
        get => editedDisabled;
        set
        {
            if (editedDisabled == value)
            {
                return;
            }

            editedDisabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedDisabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownDisabled)));
        }
    }

    public bool ShownDisabled => BaseDisabled || EditedDisabled;

    public string Label => Mesh.Label;

    public bool IsExpanded
    {
        get => false;
        set { }
    }

    bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    bool isHidden;

    public bool IsHidden
    {
        get => isHidden;
        set
        {
            if (isHidden == value)
            {
                return;
            }

            isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
