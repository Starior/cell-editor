using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Media3D;
using CellEditor.Cells;
using CellEditor.Nif;
using CellEditor.Plugins;
using CellEditor.Textures;
using HelixToolkit.Maths;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using DxCull = SharpDX.Direct3D11.CullMode;
using HxCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HxMesh = HelixToolkit.SharpDX.MeshGeometry3D;

namespace CellEditor;

public partial class MainWindow : Window
{
    readonly IEffectsManager effects = new DefaultEffectsManager();
    readonly EditorLog log = new();
    readonly EditorSession session = new();
    readonly CellBrowser cells = new();
    readonly CancellationTokenSource closing = new();
    readonly Dictionary<SceneObject, List<PartView>> shown = new();
    readonly Dictionary<string, TextureModel> textureCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TextureModel?> modTextureCache = new(StringComparer.OrdinalIgnoreCase);
    bool normalMapsOn = true;
    GridLength propertiesWidth = new(320);
    GridLength logHeight = new(180);
    SceneObject? selected;
    Point pressPoint;
    bool fillingMods;
    bool fillingVersions;
    bool fillingPosition;
    bool texturesOn = true;
    bool texturesReady;
    volatile int textureGeneration;
    AgentServer? agent;
    bool showMarkers;
    string objectQuery = "";
    int loadGeneration;
    readonly List<SceneObject> cellScenes = new();
    readonly List<PluginNode> cellGroups = new();
    readonly Dictionary<string, PluginNode> groupsByName = new(StringComparer.OrdinalIgnoreCase);
    CellLoad? currentLoad;
    Categories categories = Categories.Empty;
    readonly HashSet<string> hiddenCategories = new(StringComparer.OrdinalIgnoreCase);
    FlyCameraController? flyCamera;
    const double DefaultNearPlane = 0.5;
    const int CellDepthBias = 1;

    public MainWindow()
    {
        App.Write("main window");
        InitializeComponent();
        SourceInitialized += (_, _) => App.BringToFront(this);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, Open_Click));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,
            (sender, e) => CreatePatch_Click(sender, e),
            (_, e) => e.CanExecute = PatchMenuItem.IsEnabled));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));

        Viewport.EffectsManager = effects;
        Viewport.Camera = new HxCamera
        {
            Position = new Point3D(0, 80, 200),
            LookDirection = new Vector3D(0, -20, -200),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = DefaultNearPlane,
            FarPlaneDistance = 1_000_000,
        };

        flyCamera = new FlyCameraController(Viewport, () => Viewport.Camera as HxCamera, UpdateLights);
        UpdateLights();

        log.Added += OnLogLine;
        log.Info("Session log: " + (log.FilePath ?? "not written"));
        categories = LoadCategories(log.Info);
        Closed += (_, _) =>
        {
            closing.Cancel();
            agent?.Dispose();
            cells.Dispose();
            effects.Dispose();
        };
        ContentRendered += (_, _) =>
        {
            App.Write("window shown");
            ChoosePlugins();
        };

        try
        {
            agent = new AgentServer(session, log, OpenFromAgent);
            agent.Start();
            log.Info("Agent API at http://127.0.0.1:" + AgentServer.Port + "/");
        }
        catch (Exception ex)
        {
            log.Error("Agent API did not start: " + ex.Message);
        }

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var path = args[1];
            Loaded += (_, _) => LoadNif(path);
        }
    }

    EditorSnapshot OpenFromAgent(string path)
    {
        return Dispatcher.Invoke(() => LoadNif(path));
    }

    void Plugins_Click(object sender, RoutedEventArgs e)
    {
        ChoosePlugins();
    }

    bool EnsureMo2()
    {
        if (Mo2Location.Try() != null)
        {
            return true;
        }

        while (true)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the Mod Organizer folder",
            };
            if (picker.ShowDialog(this) != true)
            {
                log.Error("Mod Organizer folder was not selected.");
                return false;
            }

            if (!Mo2Location.IsInstance(picker.FolderName))
            {
                log.Error("That folder has no ModOrganizer.ini.");
                continue;
            }

            Mo2Location.Save(picker.FolderName);
            log.Info("Mod Organizer folder: " + Mo2Location.Require());
            return true;
        }
    }

    void ChoosePlugins()
    {
        App.Write("plugin dialog");
        if (!EnsureMo2())
        {
            return;
        }

        IReadOnlyList<PluginRow> rows;
        try
        {
            rows = PluginCatalog.Load();
        }
        catch (Exception ex)
        {
            log.Error("Could not read the MO2 plugin list: " + ex.Message);
            return;
        }

        var dialog = new PluginSelectWindow(rows) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            log.Info("Plugin selection cancelled.");
            return;
        }

        var selected = dialog.Selected;
        log.Info(selected.Count + " plugins selected. Indexing cells.");
        var roots = PluginCatalog.MeshRoots();
        var token = closing.Token;
        Task.Run(() =>
        {
            try
            {
                cells.Open(selected, message => Dispatcher.BeginInvoke(() => log.Info(message)), token);
                Dispatcher.BeginInvoke(() =>
                {
                    log.Info("Cell index ready. " + cells.CellCount + " named cells.");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => log.Error("Cell index failed: " + ex.Message));
            }
        });
    }

    void OpenCell_Click(object sender, RoutedEventArgs e)
    {
        if (cells.CellCount == 0)
        {
            log.Info("Pick plugins first. File → Plugins.");
            return;
        }

        var dialog = new CellSearchWindow(cells) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Chosen != null)
        {
            OpenCell(dialog.Chosen);
        }
    }

    void OpenCell(CellEntry entry)
    {
        var generation = ++loadGeneration;
        log.Info("Loading " + entry.Label);
        SetBusy("Loading " + entry.EditorId, "Reading references");
        var roots = PluginCatalog.MeshRoots();
        Task.Run(() =>
        {
            try
            {
                var load = cells.Load(entry, roots, text =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (generation == loadGeneration)
                        {
                            SetBusy("Loading " + entry.EditorId, text);
                        }
                    });
                });
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation != loadGeneration)
                    {
                        return;
                    }

                    ShowCell(load, generation);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation == loadGeneration)
                    {
                        ClearBusy();
                    }

                    log.Error(ex.Message);
                });
            }
        });
    }

    void SetBusy(string title, string detail)
    {
        BusyCard.Visibility = Visibility.Visible;
        BusyTitle.Text = title;
        BusyDetail.Text = detail;
        Cursor = Cursors.Wait;
    }

    void ClearBusy()
    {
        BusyCard.Visibility = Visibility.Collapsed;
        Cursor = null;
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Skyrim mesh (*.nif)|*.nif",
            Title = "Open NIF",
        };
        var meshes = PluginCatalog.TryMeshesDirectory();
        if (meshes != null)
        {
            dialog.InitialDirectory = meshes;
        }

        if (dialog.ShowDialog(this) == true)
        {
            LoadNif(dialog.FileName);
        }
    }

    void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    void DeleteMeshCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = MeshCache.DeleteUnused(PluginCatalog.MeshRoots());
            log.Info(removed == 0
                ? "Mesh cache already matches the enabled mods."
                : "Deleted mesh cache for " + removed + " mods that are not enabled.");
        }
        catch (Exception ex)
        {
            log.Error("Mesh cache: " + ex.Message);
        }
    }

    void LogsMenu_Checked(object sender, RoutedEventArgs e)
    {
        ShowLogs(true);
    }

    void LogsMenu_Unchecked(object sender, RoutedEventArgs e)
    {
        ShowLogs(false);
    }

    void ShowLogs(bool show)
    {
        if (LogPanel == null)
        {
            return;
        }

        if (!show && LogRow.ActualHeight > 0)
        {
            logHeight = LogRow.Height;
        }

        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogSplitter.Visibility = LogPanel.Visibility;
        LogRow.Height = show ? logHeight : new GridLength(0);
        LogSplitterRow.Height = new GridLength(show ? 4 : 0);
    }

    void SetPropertiesVisible(bool show)
    {
        if (show == (PropertiesPanel.Visibility == Visibility.Visible))
        {
            return;
        }

        if (!show)
        {
            propertiesWidth = PropertiesColumn.Width;
        }

        PropertiesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PropertiesSplitter.Visibility = PropertiesPanel.Visibility;
        PropertiesColumn.MinWidth = show ? 220 : 0;
        PropertiesColumn.Width = show ? propertiesWidth : new GridLength(0);
        PropertiesSplitterColumn.Width = new GridLength(show ? 4 : 0);
    }

    void CloseProperties_Click(object sender, RoutedEventArgs e)
    {
        if (selected != null)
        {
            selected.IsSelected = false;
            Paint(selected, false);
            selected = null;
        }

        ModelResults.Visibility = Visibility.Collapsed;
        SetPropertiesVisible(false);
    }

    void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var text = log.AllText();
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var nif = files.FirstOrDefault(file => file.EndsWith(".nif", StringComparison.OrdinalIgnoreCase));
        if (nif != null)
        {
            LoadNif(nif);
        }
    }

    EditorSnapshot LoadNif(string path)
    {
        log.Info("Opening " + path);
        try
        {
            var mesh = NifReader.Read(path);
            ShowMesh(mesh);
            var name = Path.GetFileName(path);
            var triangles = mesh.TriangleIndices.Count / 3;
            Title = name + " — Cell Editor";
            var snapshot = new EditorSnapshot(path, name, mesh.Positions.Count, triangles, mesh.ShapeCount, null);
            session.Set(snapshot);
            var shapeWord = mesh.ShapeCount == 1 ? "shape" : "shapes";
            log.Info(name + " — " + mesh.Positions.Count + " vertices, " + triangles + " triangles, " + mesh.ShapeCount + " " + shapeWord);
            return snapshot;
        }
        catch (Exception ex)
        {
            log.Error(ex.Message);
            var failed = session.Snapshot with { Error = ex.Message };
            session.Set(failed);
            return failed;
        }
    }

    void OnLogLine(LogLine line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogLine(line));
            return;
        }

        var stickToEnd = LogBox.SelectionLength == 0 && LogBox.CaretIndex >= LogBox.Text.Length;
        var start = LogBox.SelectionStart;
        var length = LogBox.SelectionLength;
        LogBox.AppendText(line.Display + Environment.NewLine);
        if (stickToEnd)
        {
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
        }
        else
        {
            LogBox.SelectionStart = start;
            LogBox.SelectionLength = length;
        }
    }

    void ShowMesh(NifMesh mesh)
    {
        for (var i = Viewport.Items.Count - 1; i >= 0; i--)
        {
            if (Viewport.Items[i] is MeshGeometryModel3D)
            {
                Viewport.Items.RemoveAt(i);
            }
        }

        var geometry = new HxMesh
        {
            Positions = new Vector3Collection(mesh.Positions),
            Indices = new IntCollection(mesh.TriangleIndices),
            Normals = new Vector3Collection(FaceNormals(mesh.Positions, mesh.TriangleIndices)),
        };

        Viewport.Items.Add(new MeshGeometryModel3D
        {
            Geometry = geometry,
            Material = PhongMaterials.Gray,
            CullMode = DxCull.None,
        });

        Frame(mesh.Positions);
    }

    void ShowCell(CellLoad load, int generation)
    {
        textureGeneration++;
        selected = null;
        shown.Clear();
        textureCache.Clear();
        modTextureCache.Clear();
        texturesReady = false;
        cellScenes.Clear();
        cellGroups.Clear();
        SetPropertiesVisible(false);
        PatchMenuItem.IsEnabled = false;
        RevealHiddenButton.Visibility = Visibility.Hidden;
        for (var i = Viewport.Items.Count - 1; i >= 0; i--)
        {
            if (Viewport.Items[i] is MeshGeometryModel3D)
            {
                Viewport.Items.RemoveAt(i);
            }
        }

        currentLoad = load;
        groupsByName.Clear();
        BuildPluginGroups(load);
        var byPlugin = groupsByName;
        var all = new List<Vector3>();
        var index = 0;
        SetBusy("Placing meshes", "0 / " + load.Meshes.Count);
        Dispatcher.BeginInvoke(PlaceBatch, DispatcherPriority.Background);

        void PlaceBatch()
        {
            if (generation != loadGeneration)
            {
                return;
            }

            var end = Math.Min(index + 40, load.Meshes.Count);
            for (; index < end; index++)
            {
                var mesh = load.Meshes[index];
                if (!byPlugin.TryGetValue(mesh.Plugin, out var group))
                {
                    group = new PluginNode { Name = mesh.Plugin, LoadIndex = int.MaxValue };
                    byPlugin[mesh.Plugin] = group;
                    cellGroups.Add(group);
                }

                var scene = new SceneObject
                {
                    Mesh = mesh,
                    Group = group,
                    ActiveVersion = mesh.Versions.Count > 0 ? mesh.Versions[0] : null,
                    Winner = mesh.Versions.Count > 0 ? mesh.Versions[0] : null,
                };
                AssignCategory(scene);
                cellScenes.Add(scene);
                if (IncludeInTree(scene))
                {
                    group.Objects.Add(scene);
                }

                ApplySceneViewport(scene, highlight: false);
                if (ShouldDrawInViewport(scene))
                {
                    foreach (var part in PartsForScene(scene))
                    {
                        all.AddRange(part.Positions);
                    }
                }
            }

            SetBusy("Placing meshes", index + " / " + load.Meshes.Count);
            if (index < load.Meshes.Count)
            {
                Dispatcher.BeginInvoke(PlaceBatch, DispatcherPriority.Background);
                return;
            }

            ObjectTree.ItemsSource = VisibleGroups();
            RefreshCategoryFilters();
            if (all.Count > 0)
            {
                Frame(all);
            }

            Title = load.Title + " — Cell Editor";
            var snapshot = new EditorSnapshot(null, load.Title, 0, 0, load.Drawn, null, load.Title);
            var vertices = load.Meshes.Sum(mesh => mesh.Parts.Sum(part => part.Positions.Count));
            var triangles = load.Meshes.Sum(mesh => mesh.Parts.Sum(part => part.TriangleIndices.Count / 3));
            session.Set(snapshot with { Vertices = vertices, Triangles = triangles });
            log.Info(load.Title + " — " + load.Drawn + " meshes drawn, " + load.Skipped + " disabled and not drawn, " + load.MissingMeshes + " meshes not found.");
            if (load.Missing.Count > 0)
            {
                log.Info("Not found: " + string.Join(", ", load.Missing.Take(12)));
            }

            if (load.Dropped.Count > 0)
            {
                var reasons = load.Dropped
                    .GroupBy(item => item.Reason.Split(':')[0])
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Count() + " " + group.Key);
                log.Info(load.Dropped.Count + " references not drawn: " + string.Join("; ", reasons) + ". Full list in " + log.FilePath);
                foreach (var item in load.Dropped)
                {
                    log.Detail("not drawn " + item.Key + " (last edit " + item.Plugin + "): " + item.Reason);
                }
            }

            if (texturesOn)
            {
                SetBusy("Loading textures", "0");
                LoadTextures();
            }
            else
            {
                ClearBusy();
            }
        }
    }

    bool IncludeInTree(SceneObject scene)
    {
        if (scene.PluginOff || hiddenCategories.Contains(scene.Category) || (!showMarkers && scene.Mesh.IsMarker))
        {
            return false;
        }

        return objectQuery.Length == 0
            || scene.Mesh.FormId.Contains(objectQuery, StringComparison.OrdinalIgnoreCase);
    }

    void ObjectSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var next = NormalizeFormQuery(ObjectSearch.Text);
        if (next == objectQuery)
        {
            return;
        }

        objectQuery = next;
        if (cellScenes.Count > 0)
        {
            RebuildObjectTree();
        }
    }

    static string NormalizeFormQuery(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (var c in text.Trim())
        {
            if (c == ' ')
            {
                continue;
            }

            chars.Add(c switch
            {
                // Cyrillic letters that look like hex digits. Stored as escapes so the source stays Latin.
                '\u0410' or '\u0430' => 'A',
                '\u0412' or '\u0432' => 'B',
                '\u0415' or '\u0435' => 'E',
                '\u041A' or '\u043A' => 'K',
                '\u041C' or '\u043C' => 'M',
                '\u041D' or '\u043D' => 'H',
                '\u041E' or '\u043E' => 'O',
                '\u0420' or '\u0440' => 'P',
                '\u0421' or '\u0441' => 'C',
                '\u0422' or '\u0442' => 'T',
                '\u0425' or '\u0445' => 'X',
                _ => c,
            });
        }

        return new string(chars.ToArray());
    }

    bool ShouldDrawInViewport(SceneObject scene)
    {
        return !scene.PluginOff && !hiddenCategories.Contains(scene.Category) && !scene.IsHidden && !scene.ShownDisabled
            && (showMarkers || !scene.Mesh.IsMarker);
    }

    IReadOnlyList<PlacedPart> PartsForScene(SceneObject scene)
    {
        if (!ShouldDrawInViewport(scene))
        {
            return [];
        }

        return FramePartsForScene(scene);
    }

    IReadOnlyList<PlacedPart> FramePartsForScene(SceneObject scene)
    {
        if (scene.SwapParts != null)
        {
            return scene.SwapParts;
        }

        if (scene.ActiveVersion is { Disabled: false } version)
        {
            return version.Parts;
        }

        return scene.Mesh.Parts;
    }

    void ApplySceneViewport(SceneObject scene, bool highlight)
    {
        ShowParts(scene, PartsForScene(scene), highlight);
    }

    void RebuildObjectTree()
    {
        foreach (var group in cellGroups)
        {
            group.Objects.Clear();
        }

        foreach (var scene in cellScenes)
        {
            if (IncludeInTree(scene))
            {
                scene.Group?.Objects.Add(scene);
            }
        }

        ObjectTree.ItemsSource = VisibleGroups();
    }

    /// <summary>Every plugin that touches the cell, so an unchecked one stays in the list to check again.</summary>
    List<PluginNode> VisibleGroups()
    {
        return cellGroups.Where(group => group.Objects.Count > 0 || objectQuery.Length == 0).ToList();
    }

    void BuildPluginGroups(CellLoad load)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < load.LoadOrder.Count; i++)
        {
            index.TryAdd(load.LoadOrder[i], i);
        }

        var names = load.Meshes
            .SelectMany(mesh => mesh.Versions.Select(version => version.Plugin).Append(mesh.Plugin).Append(OriginPlugin(mesh)))
            .Append(load.CellPlugin)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var locked = MastersClosure(load, [load.CellPlugin]);
        var nodes = names
            .Select(name => new PluginNode
            {
                Name = name,
                LoadIndex = index.TryGetValue(name, out var at) ? at : int.MaxValue,
                CanDisable = !locked.Contains(name),
            })
            .OrderBy(node => node.LoadIndex);
        foreach (var node in nodes)
        {
            groupsByName[node.Name] = node;
            cellGroups.Add(node);
        }
    }

    static string OriginPlugin(PlacedMesh mesh)
    {
        return mesh.Key.IsNull ? "" : mesh.Key.ModKey.FileName.String;
    }

    /// <summary>The given plugins and every master they need, directly or through another master.</summary>
    static HashSet<string> MastersClosure(CellLoad load, IEnumerable<string> start)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(start.Where(name => name.Length > 0));
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!found.Add(name) || !load.Masters.TryGetValue(name, out var masters))
            {
                continue;
            }

            foreach (var master in masters)
            {
                pending.Push(master);
            }
        }

        return found;
    }

    /// <summary>The given plugin and every plugin that needs it as a master, directly or through another plugin.</summary>
    static HashSet<string> DependentsClosure(CellLoad load, string plugin)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plugin };
        foreach (var name in load.LoadOrder)
        {
            if (load.Masters.TryGetValue(name, out var masters) && masters.Any(found.Contains))
            {
                found.Add(name);
            }
        }

        return found;
    }

    void PluginToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginNode node } || currentLoad == null)
        {
            return;
        }

        var affected = node.IsEnabled
            ? MastersClosure(currentLoad, [node.Name])
            : DependentsClosure(currentLoad, node.Name);
        foreach (var name in affected)
        {
            if (groupsByName.TryGetValue(name, out var group) && group.CanDisable)
            {
                group.IsEnabled = node.IsEnabled;
            }
        }

        ApplyPluginFilter();
        e.Handled = true;
    }

    static Categories LoadCategories(Action<string>? report)
    {
        try
        {
            var loaded = Categories.Load();
            report?.Invoke("Categories: " + loaded.File.Categories.Count + " rules, " + loaded.File.Overrides.Count + " overrides from " + loaded.Path);
            return loaded;
        }
        catch (Exception ex)
        {
            report?.Invoke("categories.json not read: " + ex.Message);
            return Categories.Empty;
        }
    }

    void AssignCategory(SceneObject scene)
    {
        scene.Category = categories.Of(scene.Mesh);
        scene.CategoryColor = categories.ColorOf(scene.Category);
    }

    /// <summary>One chip per category that has objects in the cell, in categories.json order.</summary>
    void RefreshCategoryFilters()
    {
        if (CategoryList == null)
        {
            return;
        }

        var counts = cellScenes
            .Where(scene => !scene.PluginOff && (showMarkers || !scene.Mesh.IsMarker))
            .GroupBy(scene => scene.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var order = categories.Names.ToList();
        var filters = counts.Keys
            .OrderBy(name => order.FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)) is var at && at < 0 ? int.MaxValue : at)
            .Select(name => new CategoryFilter
            {
                Name = name,
                Color = categories.ColorOf(name),
                Count = counts[name],
                IsOn = !hiddenCategories.Contains(name),
            })
            .ToList();
        CategoryList.ItemsSource = filters;
        CategoryBar.Visibility = filters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyCategoryFilter()
    {
        if (selected != null && hiddenCategories.Contains(selected.Category))
        {
            CloseProperties_Click(this, new RoutedEventArgs());
        }

        RebuildObjectTree();
        foreach (var scene in cellScenes)
        {
            ApplySceneViewport(scene, highlight: selected == scene);
        }

        RefreshCategoryFilters();
    }

    void CategoryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryFilter filter })
        {
            return;
        }

        if (filter.IsOn)
        {
            hiddenCategories.Remove(filter.Name);
        }
        else
        {
            hiddenCategories.Add(filter.Name);
        }

        ApplyCategoryFilter();
    }

    void CategoryOnly_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CategoryFilter filter })
        {
            return;
        }

        hiddenCategories.Clear();
        foreach (var name in categories.Names.Concat(cellScenes.Select(scene => scene.Category)))
        {
            if (!name.Equals(filter.Name, StringComparison.OrdinalIgnoreCase))
            {
                hiddenCategories.Add(name);
            }
        }

        ApplyCategoryFilter();
        e.Handled = true;
    }

    void CategoryAll_Click(object sender, RoutedEventArgs e)
    {
        hiddenCategories.Clear();
        ApplyCategoryFilter();
    }

    void ReloadCategories_Click(object sender, RoutedEventArgs e)
    {
        categories = LoadCategories(log.Info);
        foreach (var scene in cellScenes)
        {
            AssignCategory(scene);
        }

        ApplyCategoryFilter();
    }

    void OpenCategories_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(categories.Path))
        {
            log.Error("Not found: " + categories.Path);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(categories.Path) { UseShellExecute = true });
    }

    void ApplyPluginFilter()
    {
        var off = cellGroups.Where(group => !group.IsEnabled).Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in cellScenes)
        {
            var winner = scene.Mesh.Versions.FirstOrDefault(version => !off.Contains(version.Plugin));
            var followsWinner = scene.ActiveVersion == null || ReferenceEquals(scene.ActiveVersion, scene.Winner);
            scene.PluginOff = winner == null || off.Contains(OriginPlugin(scene.Mesh));
            scene.Winner = winner;
            if (followsWinner || off.Contains(scene.ActiveVersion!.Plugin))
            {
                scene.ActiveVersion = winner;
            }

            if (groupsByName.TryGetValue(winner?.Plugin ?? scene.Mesh.Plugin, out var group))
            {
                scene.Group = group;
            }

            ApplySceneViewport(scene, highlight: selected == scene);
        }

        if (selected is { PluginOff: true })
        {
            CloseProperties_Click(this, new RoutedEventArgs());
        }
        else if (selected != null)
        {
            FillProperties(selected);
        }

        RebuildObjectTree();
        RefreshCategoryFilters();
        UpdateRevealHiddenButton();
        UpdatePatchMenu();
        var disabled = off.Count == 0 ? "all plugins on" : off.Count + " off: " + string.Join(", ", off);
        log.Info("Cell plugins — " + disabled);
    }

    void RefreshMarkerVisibility()
    {
        RebuildObjectTree();
        RefreshCategoryFilters();
        foreach (var scene in cellScenes)
        {
            ApplySceneViewport(scene, highlight: selected == scene);
        }
    }

    void UpdateRevealHiddenButton()
    {
        var any = cellScenes.Any(scene => scene.IsHidden && !scene.PluginOff && !scene.BaseDisabled);
        RevealHiddenButton.Visibility = any ? Visibility.Visible : Visibility.Hidden;
    }

    void Objects_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SceneObject scene)
        {
            Select(scene);
        }
    }

    void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.BringIntoView();
        }
    }

    void TreeItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: SceneObject scene })
        {
            return;
        }

        FrameScene(scene);
        e.Handled = true;
    }

    void MarkersMenu_Checked(object sender, RoutedEventArgs e)
    {
        SetShowMarkers(true);
    }

    void MarkersMenu_Unchecked(object sender, RoutedEventArgs e)
    {
        SetShowMarkers(false);
    }

    void SetShowMarkers(bool on)
    {
        showMarkers = on;
        if (MarkersMenuItem != null)
        {
            MarkersMenuItem.IsChecked = on;
        }

        RefreshMarkerVisibility();
    }

    void CameraMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (flyCamera == null || CameraModeBox == null)
        {
            return;
        }

        flyCamera.FlyMode = CameraModeBox.SelectedIndex == 1;
    }

    void FlySpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (flyCamera != null)
        {
            flyCamera.FlySpeed = FlySpeedSlider.Value;
        }
    }

    void Clip_Changed(object sender, RoutedEventArgs e)
    {
        ApplyClipPlane();
    }

    void Clip_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyClipPlane();
    }

    void ApplyClipPlane()
    {
        if (Viewport.Camera is not HxCamera camera || ClipToggle == null)
        {
            return;
        }

        if (ClipSlider != null)
        {
            ClipSlider.IsEnabled = ClipToggle.IsChecked == true;
        }

        camera.NearPlaneDistance = ClipToggle.IsChecked == true && ClipSlider != null
            ? ClipSlider.Value
            : DefaultNearPlane;
    }

    void Viewport_CameraChanged(object? sender, RoutedEventArgs e)
    {
        UpdateLights();
    }

    void UpdateLights()
    {
        if (Viewport.Camera is not HxCamera camera)
        {
            return;
        }

        var look = camera.LookDirection;
        if (look.LengthSquared < 1e-12)
        {
            return;
        }

        look.Normalize();
        HeadLight.Direction = look;
        var fill = new Vector3D(-look.X * 0.55 + 0.15, -look.Y * 0.55 + 0.35, -look.Z * 0.55);
        if (fill.LengthSquared > 1e-12)
        {
            fill.Normalize();
            FillLight.Direction = fill;
        }
    }

    void ObjectVisibility_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SceneObject scene })
        {
            return;
        }

        scene.IsHidden = !scene.IsHidden;
        ApplySceneViewport(scene, highlight: selected == scene);
        UpdateRevealHiddenButton();
        e.Handled = true;
    }

    void RevealHidden_Click(object sender, RoutedEventArgs e)
    {
        foreach (var scene in cellScenes)
        {
            scene.IsHidden = false;
            ApplySceneViewport(scene, highlight: selected == scene);
        }

        UpdateRevealHiddenButton();
    }

    void Viewport_Down(object sender, MouseButtonEventArgs e)
    {
        pressPoint = e.GetPosition(Viewport);
    }

    void Viewport_Up(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Viewport);
        if (Math.Abs(point.X - pressPoint.X) > 4 || Math.Abs(point.Y - pressPoint.Y) > 4)
        {
            return;
        }

        var hit = Viewport.FindHits(point).FirstOrDefault();
        var scene = (hit?.ModelHit as MeshGeometryModel3D)?.Tag as SceneObject;
        if (scene != null)
        {
            Select(scene);
        }
    }

    void Select(SceneObject scene)
    {
        if (selected == scene)
        {
            return;
        }

        if (selected != null)
        {
            selected.IsSelected = false;
            Paint(selected, false);
        }

        selected = scene;
        scene.Group?.IsExpanded = true;
        scene.IsSelected = true;
        Paint(scene, true);
        FillProperties(scene);
        TreeViewHelper.BringSceneIntoView(ObjectTree, scene);
    }

    void Paint(SceneObject scene, bool on)
    {
        if (!shown.TryGetValue(scene, out var parts))
        {
            return;
        }

        foreach (var part in parts)
        {
            part.Material.EmissiveColor = on ? new Color4(0.45f, 0.32f, 0.05f, 1f) : new Color4(0, 0, 0, 1);
        }
    }

    void ShowParts(SceneObject scene, IReadOnlyList<PlacedPart> parts, bool highlight)
    {
        if (shown.TryGetValue(scene, out var previous))
        {
            foreach (var part in previous)
            {
                Viewport.Items.Remove(part.Model);
            }
        }

        var created = new List<PartView>(parts.Count);
        foreach (var part in parts)
        {
            var display = PosedPart(scene, part);
            var material = new PhongMaterial
            {
                DiffuseColor = new Color4(1f, 1f, 1f, 1f),
                AmbientColor = new Color4(0.55f, 0.55f, 0.55f, 1f),
                SpecularColor = new Color4(0.18f, 0.18f, 0.18f, 1f),
                SpecularShininess = 24,
                EnableAutoTangent = true,
            };
            var model = new MeshGeometryModel3D
            {
                Geometry = new HxMesh
                {
                    Positions = new Vector3Collection(display.Positions),
                    Indices = new IntCollection(display.TriangleIndices),
                    Normals = new Vector3Collection(SurfaceNormals(display)),
                    TextureCoordinates = new Vector2Collection(display.Uvs),
                },
                Material = material,
                CullMode = DxCull.None,
                DepthBias = CellDepthBias + FormBias(scene),
                SlopeScaledDepthBias = FormBias(scene),
                Tag = scene,
            };
            Viewport.Items.Add(model);
            created.Add(new PartView { Model = model, Material = material, Diffuse = part.Diffuse, Normal = part.NormalMap });
        }

        shown[scene] = created;
        if (highlight)
        {
            Paint(scene, true);
        }

        foreach (var part in created)
        {
            part.Texture = TextureFor(part.Diffuse, scene.TextureMod);
            part.NormalTexture = TextureFor(part.Normal, scene.TextureMod);
            ApplyMaps(part);
        }
    }

    /// <summary>Texture from the picked mod, or the load-order winner when no mod is picked or that mod lacks it.</summary>
    TextureModel? TextureFor(string? path, string? mod)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (mod != null)
        {
            var key = mod + "|" + path;
            if (!modTextureCache.TryGetValue(key, out var picked))
            {
                var source = AssetIndex.Find(path).FirstOrDefault(hit => hit.ModName == mod);
                var bytes = source?.Read();
                picked = bytes == null ? null : DdsImage.Load(bytes);
                modTextureCache[key] = picked;
            }

            if (picked != null)
            {
                return picked;
            }
        }

        return textureCache.TryGetValue(path, out var texture) ? texture : null;
    }

    void ApplyMaps(PartView part)
    {
        part.Material.DiffuseMap = texturesOn ? part.Texture : null;
        part.Material.RenderDiffuseMap = texturesOn && part.Texture != null;
        var normal = texturesOn && normalMapsOn ? part.NormalTexture : null;
        part.Material.NormalMap = normal;
        part.Material.RenderNormalMap = normal != null;
    }

    void RefreshMaps()
    {
        foreach (var (scene, parts) in shown)
        {
            foreach (var part in parts)
            {
                part.Texture = TextureFor(part.Diffuse, scene.TextureMod);
                part.NormalTexture = TextureFor(part.Normal, scene.TextureMod);
                ApplyMaps(part);
            }
        }
    }

    void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (fillingVersions || selected == null || VersionBox.SelectedItem is not PlacedVersion version)
        {
            return;
        }

        if (ReferenceEquals(selected.ActiveVersion, version))
        {
            return;
        }

        selected.ActiveVersion = version;
        selected.EditedPosition = null;
        selected.EditedAngles = null;
        if (selected.Swap != null)
        {
            PlaceSwap(selected, selected.Swap);
        }

        ApplySceneViewport(selected, highlight: true);
        ShowModel(selected);
        ShowPlacement(selected);
        FillTextureMods(FramePartsForScene(selected));
    }

    void ShowModel(SceneObject scene)
    {
        var path = ActiveModelPath(scene);
        PropModel.Text = path.Length > 0 ? path : scene.Mesh.ModelName;
        if (scene.Swap == null)
        {
            PropSwap.Visibility = Visibility.Collapsed;
            return;
        }

        PropSwap.Text = "Preview uses " + scene.Swap.EditorId + "  (" + scene.Swap.FormId + ", " + scene.Swap.Plugin + ")";
        PropSwap.Visibility = Visibility.Visible;
    }

    static string ActiveModelPath(SceneObject scene)
    {
        if (scene.Swap != null)
        {
            return "meshes\\" + scene.Swap.ModelPath;
        }

        var path = scene.ActiveVersion is { ModelPath.Length: > 0 } version ? version.ModelPath : scene.Mesh.ModelPath;
        return path.Length == 0 ? "" : "meshes\\" + path;
    }

    void ModelSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var text = ModelSearch.Text.Trim();
        if (text.Length < 2)
        {
            ModelResults.ItemsSource = null;
            ModelResults.Visibility = Visibility.Collapsed;
            return;
        }

        if (cells.ModelCount == 0)
        {
            ModelResults.ItemsSource = new[] { "Model index is not ready. Pick plugins first." };
            ModelResults.Visibility = Visibility.Visible;
            return;
        }

        var hits = cells.SearchModels(text, 80);
        ModelResults.ItemsSource = hits;
        ModelResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ModelSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (ModelResults.Visibility != Visibility.Visible || ModelResults.Items.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            ModelResults.SelectedIndex = 0;
            (ModelResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ModelResults.Items[0] is ModelEntry first)
        {
            PickSwap(first);
            e.Handled = true;
        }
    }

    void ModelResults_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ModelResults, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is ModelEntry entry)
        {
            PickSwap(entry);
            e.Handled = true;
        }
    }

    void ModelResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ModelResults.SelectedItem is ModelEntry entry)
        {
            PickSwap(entry);
            e.Handled = true;
        }
    }

    void PickSwap(ModelEntry entry)
    {
        if (selected == null)
        {
            log.Error("Model swap: no object is selected.");
            return;
        }

        log.Info(selected.Mesh.FormId + " swap requested → " + entry.EditorId + " (" + entry.Key + ")  meshes\\" + entry.ModelPath);

        if (!PlaceSwap(selected, entry))
        {
            return;
        }

        ModelResults.Visibility = Visibility.Collapsed;
        ApplySceneViewport(selected, highlight: true);
        ShowModel(selected);
        FillTextureMods(FramePartsForScene(selected));
        UpdateResetButton(selected);
        LoadMissingTextures(selected.SwapParts!);
        log.Info(selected.Mesh.FormId + " preview model → " + entry.EditorId + "  meshes\\" + entry.ModelPath);
    }

    bool PlaceSwap(SceneObject scene, ModelEntry entry)
    {
        if (Baseline(scene) is not PlacementView pose)
        {
            log.Error(scene.Mesh.FormId + " has no placement to put another model at.");
            return false;
        }

        var parts = CellBrowser.PlaceModel(entry.ModelPath, pose, PluginCatalog.MeshRoots(), out var error);
        if (parts == null)
        {
            log.Error(error);
            return false;
        }

        scene.Swap = entry;
        scene.SwapParts = parts;
        return true;
    }

    void LoadMissingTextures(IEnumerable<PlacedPart> parts)
    {
        if (!texturesReady)
        {
            return;
        }

        var paths = MapPaths(parts)
            .Where(path => !textureCache.ContainsKey(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var generation = textureGeneration;
        Task.Run(() =>
        {
            var decoded = new Dictionary<string, DdsImage.Decoded?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var bytes = AssetIndex.Find(path).FirstOrDefault()?.Read();
                decoded[path] = bytes == null ? null : DdsImage.Decode(bytes);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (generation != textureGeneration)
                {
                    return;
                }

                foreach (var pair in decoded)
                {
                    if (pair.Value != null)
                    {
                        textureCache[pair.Key] = DdsImage.ToModel(pair.Value.Value);
                    }
                }

                RefreshMaps();
            });
        });
    }

    void UpdateResetButton(SceneObject scene)
    {
        ResetObjectButton.IsEnabled = scene.EditedPosition != null
            || scene.EditedAngles != null
            || scene.Swap != null
            || scene.EditedDisabled
            || scene.TextureMod != null
            || !ReferenceEquals(scene.ActiveVersion, scene.Winner);
        UpdatePatchMenu();
    }

    void UpdatePatchMenu()
    {
        PatchMenuItem.IsEnabled = cellScenes.Any(HasPatchChange);
    }

    void DisabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || selected.BaseDisabled)
        {
            return;
        }

        selected.EditedDisabled = DisabledToggle.IsChecked == true;
        ApplySceneViewport(selected, highlight: true);
        UpdateResetButton(selected);
    }

    /// <summary>Pose the object has now: typed numbers over the active version's plugin pose.</summary>
    static PlacementView? CurrentPose(SceneObject scene)
    {
        if (Baseline(scene) is not PlacementView baseline)
        {
            return null;
        }

        var position = scene.EditedPosition ?? new Vector3(baseline.X, baseline.Y, baseline.Z);
        var angles = scene.EditedAngles ?? new Vector3(baseline.Rx, baseline.Ry, baseline.Rz);
        return new PlacementView(position.X, position.Y, position.Z, angles.X, angles.Y, angles.Z, baseline.Scale);
    }

    static bool PoseChanged(SceneObject scene)
    {
        var winner = scene.Winner?.Placement ?? scene.Mesh.Placement;
        if (CurrentPose(scene) is not PlacementView now || winner is not PlacementView was)
        {
            return false;
        }

        return !Nearly(now.X, was.X) || !Nearly(now.Y, was.Y) || !Nearly(now.Z, was.Z)
            || MathF.Abs(now.Rx - was.Rx) > 1e-4f || MathF.Abs(now.Ry - was.Ry) > 1e-4f || MathF.Abs(now.Rz - was.Rz) > 1e-4f;
    }

    /// <summary>Changes a plugin can hold: pose, base swap, disable. Texture mod and preview plugin are view-only.</summary>
    static bool HasPatchChange(SceneObject scene)
    {
        return !scene.PluginOff && (scene.Swap != null || scene.EditedDisabled || PoseChanged(scene));
    }

    void CreatePatch_Click(object sender, RoutedEventArgs e)
    {
        var changed = cellScenes.Where(HasPatchChange).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        var dialog = new PatchWindow(changed.Count) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var edits = changed
            .Select(scene => new PatchEdit(
                scene.Mesh.Key,
                CurrentPose(scene) ?? default,
                PoseChanged(scene),
                scene.Swap?.Key,
                scene.EditedDisabled))
            .ToList();
        var path = dialog.TargetPath;
        var isNew = dialog.IsNew;
        var folder = dialog.ModFolder;
        SetBusy("Writing patch", Path.GetFileName(path));
        Task.Run(() =>
        {
            try
            {
                var count = cells.WritePatch(path, isNew, edits);
                if (isNew)
                {
                    PluginCatalog.EnableNewMod(folder, Path.GetFileName(path));
                }

                var skipped = cells.SkippedSources.ToList();
                Dispatcher.BeginInvoke(() =>
                {
                    ClearBusy();
                    log.Info(count + " references written to " + path);
                    if (isNew)
                    {
                        log.Info("Turned on " + folder + " in MO2. Press F5 in MO2 to see it.");
                    }

                    if (skipped.Count > 0)
                    {
                        log.Info("Not readable, copied from an earlier plugin instead: " + string.Join(", ", skipped));
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ClearBusy();
                    log.Error("Patch not written: " + ex.Message);
                });
            }
        });
    }

    void ResetObject_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null)
        {
            return;
        }

        selected.EditedPosition = null;
        selected.EditedAngles = null;
        selected.Swap = null;
        selected.SwapParts = null;
        selected.EditedDisabled = false;
        selected.TextureMod = null;
        selected.ActiveVersion = selected.Winner;
        ModelSearch.Text = "";
        ApplySceneViewport(selected, highlight: true);
        FillProperties(selected);
    }

    void FillProperties(SceneObject scene)
    {
        SetPropertiesVisible(true);
        PropForm.Text = scene.Mesh.FormId + "   #" + scene.Mesh.Number;
        PropName.Text = scene.Mesh.BaseName;
        var state = scene.BaseDisabled ? "Off at game start" : "";
        PropPlugin.Text = string.Join(".  ", new[] { state, scene.Mesh.EnableParent }.Where(text => text.Length > 0));
        DisabledToggle.IsChecked = scene.ShownDisabled;
        DisabledToggle.IsEnabled = !scene.BaseDisabled && Baseline(scene) != null;
        ShowModel(scene);
        ModelResults.Visibility = Visibility.Collapsed;
        ShowPlacement(scene);
        fillingVersions = true;
        VersionBox.ItemsSource = scene.Mesh.Versions;
        VersionBox.SelectedItem = scene.ActiveVersion ?? scene.Winner;
        fillingVersions = false;
        FillTextureMods(FramePartsForScene(scene));
        UpdateResetButton(scene);
    }

    void ShowPlacement(SceneObject scene)
    {
        var place = Baseline(scene);
        fillingPosition = true;
        if (place == null)
        {
            PosX.Text = "";
            PosY.Text = "";
            PosZ.Text = "";
            AngX.Text = "";
            AngY.Text = "";
            AngZ.Text = "";
            PosX.IsEnabled = false;
            PosY.IsEnabled = false;
            PosZ.IsEnabled = false;
            AngX.IsEnabled = false;
            AngY.IsEnabled = false;
            AngZ.IsEnabled = false;
            PropScale.Visibility = Visibility.Collapsed;
            fillingPosition = false;
            return;
        }

        var value = place.Value;
        var edited = scene.EditedPosition;
        var angles = scene.EditedAngles;
        var culture = CultureInfo.InvariantCulture;
        PosX.IsEnabled = true;
        PosY.IsEnabled = true;
        PosZ.IsEnabled = true;
        AngX.IsEnabled = true;
        AngY.IsEnabled = true;
        AngZ.IsEnabled = true;
        PosX.Text = (edited?.X ?? value.X).ToString("0.00", culture);
        PosY.Text = (edited?.Y ?? value.Y).ToString("0.00", culture);
        PosZ.Text = (edited?.Z ?? value.Z).ToString("0.00", culture);
        AngX.Text = DegreesNumber(angles?.X ?? value.Rx);
        AngY.Text = DegreesNumber(angles?.Y ?? value.Ry);
        AngZ.Text = DegreesNumber(angles?.Z ?? value.Rz);
        if (Math.Abs(value.Scale - 1f) > 0.001f)
        {
            PropScale.Text = "Scale  " + value.Scale.ToString("0.00", culture);
            PropScale.Visibility = Visibility.Visible;
        }
        else
        {
            PropScale.Visibility = Visibility.Collapsed;
        }

        UpdateResetButton(scene);
        fillingPosition = false;
    }

    void Position_Changed(object sender, TextChangedEventArgs e)
    {
        if (fillingPosition || selected == null)
        {
            return;
        }

        if (!TryReadPosition(out var position) || !TryReadAngles(out var degrees))
        {
            return;
        }

        var baseline = Baseline(selected);
        if (baseline == null)
        {
            return;
        }

        var samePosition = Nearly(position.X, baseline.Value.X)
            && Nearly(position.Y, baseline.Value.Y)
            && Nearly(position.Z, baseline.Value.Z);
        var sameAngles = SameAngle(baseline.Value.Rx, degrees.X)
            && SameAngle(baseline.Value.Ry, degrees.Y)
            && SameAngle(baseline.Value.Rz, degrees.Z);
        var nextPosition = samePosition ? (Vector3?)null : position;
        var nextAngles = sameAngles ? (Vector3?)null : new Vector3(Radians(degrees.X), Radians(degrees.Y), Radians(degrees.Z));
        if (Nullable.Equals(selected.EditedPosition, nextPosition) && Nullable.Equals(selected.EditedAngles, nextAngles))
        {
            return;
        }

        selected.EditedPosition = nextPosition;
        selected.EditedAngles = nextAngles;
        UpdateResetButton(selected);
        ApplySceneViewport(selected, highlight: true);
    }

    void Position_LostFocus(object sender, RoutedEventArgs e)
    {
        if (fillingPosition || sender is not TextBox box || !TryParseFloat(box.Text, out var value))
        {
            return;
        }

        fillingPosition = true;
        box.Text = value.ToString(box.Name.StartsWith("Ang", StringComparison.Ordinal) ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        fillingPosition = false;
    }

    void PropForm_Click(object sender, MouseButtonEventArgs e)
    {
        if (selected == null)
        {
            return;
        }

        var mesh = selected.Mesh;
        var baseKey = selected.Swap?.Key ?? mesh.BaseKey;
        var name = selected.Swap?.Label.Split("   ")[0] ?? mesh.BaseName;
        var text = "REF " + mesh.Key + "  BASE " + (baseKey.IsNull ? "?" : baseKey.ToString()) + "  " + name;
        Clipboard.SetText(text);
        log.Info("Copied: " + text);
    }

    void CopyObject_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null)
        {
            return;
        }

        var place = Baseline(selected);
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string> { selected.Mesh.FormId };
        if (!string.IsNullOrWhiteSpace(selected.Mesh.BaseName))
        {
            lines[0] += "  " + selected.Mesh.BaseName;
        }

        var plugin = selected.ActiveVersion?.Plugin ?? selected.Mesh.Plugin;
        lines.Add("Plugin " + plugin);
        lines.Add("Model " + ActiveModelPath(selected));
        if (selected.Swap != null)
        {
            lines.Add("Swapped from " + selected.Swap.EditorId + " " + selected.Swap.FormId + " (" + selected.Swap.Plugin + ")");
            var original = selected.ActiveVersion is { ModelPath.Length: > 0 } version ? version.ModelPath : selected.Mesh.ModelPath;
            if (original.Length > 0)
            {
                lines.Add("Plugin model meshes\\" + original);
            }
        }

        if (place == null)
        {
            lines.Add("no placement");
        }
        else
        {
            var value = place.Value;
            var position = selected.EditedPosition ?? new Vector3(value.X, value.Y, value.Z);
            var angles = selected.EditedAngles ?? new Vector3(value.Rx, value.Ry, value.Rz);
            lines.Add("X " + position.X.ToString("0.00", culture));
            lines.Add("Y " + position.Y.ToString("0.00", culture));
            lines.Add("Z " + position.Z.ToString("0.00", culture));
            lines.Add("Angle X " + Degrees(angles.X));
            lines.Add("Angle Y " + Degrees(angles.Y));
            lines.Add("Angle Z " + Degrees(angles.Z));
            if (selected.EditedPosition != null || selected.EditedAngles != null)
            {
                lines.Add("Plugin X " + value.X.ToString("0.00", culture)
                    + "  Y " + value.Y.ToString("0.00", culture)
                    + "  Z " + value.Z.ToString("0.00", culture)
                    + "  Angle " + Degrees(value.Rx) + " " + Degrees(value.Ry) + " " + Degrees(value.Rz));
            }
        }

        var maps = MapPaths(FramePartsForScene(selected)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (maps.Count > 0)
        {
            lines.Add("Textures");
            foreach (var path in maps)
            {
                var hits = AssetIndex.Find(path);
                var from = selected.TextureMod != null && hits.Any(hit => hit.ModName == selected.TextureMod)
                    ? selected.TextureMod
                    : hits.FirstOrDefault()?.ModName;
                lines.Add("  " + path + (from == null ? "   (not found)" : "   [" + from + "]"));
            }
        }

        Clipboard.SetText(string.Join("\n", lines));
    }

    bool TryReadPosition(out Vector3 position)
    {
        position = default;
        if (!TryParseFloat(PosX.Text, out var x) || !TryParseFloat(PosY.Text, out var y) || !TryParseFloat(PosZ.Text, out var z))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        return true;
    }

    static bool TryParseFloat(string text, out float value)
    {
        return float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    bool TryReadAngles(out Vector3 degrees)
    {
        degrees = default;
        if (!TryParseFloat(AngX.Text, out var x) || !TryParseFloat(AngY.Text, out var y) || !TryParseFloat(AngZ.Text, out var z))
        {
            return false;
        }

        degrees = new Vector3(x, y, z);
        return true;
    }

    static bool Nearly(float left, float right)
    {
        return Math.Abs(left - right) < 0.001f;
    }

    static bool SameAngle(float radians, float degrees)
    {
        return Math.Abs(radians * (180f / MathF.PI) - degrees) < 0.05f;
    }

    static float Radians(float degrees)
    {
        return degrees * (MathF.PI / 180f);
    }

    static string DegreesNumber(float radians)
    {
        return (radians * (180f / MathF.PI)).ToString("0.0", CultureInfo.InvariantCulture);
    }

    static PlacementView? Baseline(SceneObject scene)
    {
        return scene.ActiveVersion?.Placement ?? scene.Mesh.Placement;
    }

    static PlacedPart PosedPart(SceneObject scene, PlacedPart part)
    {
        if (scene.EditedPosition == null && scene.EditedAngles == null)
        {
            return part;
        }

        return new PlacedPart
        {
            Positions = part.Positions.Select(point => DisplayPoint(scene, point)).ToList(),
            TriangleIndices = part.TriangleIndices,
            Uvs = part.Uvs,
            Normals = part.Normals.Select(normal => DisplayNormal(scene, normal)).ToList(),
            DoubleSided = part.DoubleSided,
            Diffuse = part.Diffuse,
            NormalMap = part.NormalMap,
        };
    }

    static Vector3 DisplayPoint(SceneObject scene, Vector3 viewPoint)
    {
        if (Baseline(scene) is not PlacementView baseline)
        {
            return viewPoint;
        }

        var skyrim = NifReader.FromView(viewPoint);
        var origin = new Vector3(baseline.X, baseline.Y, baseline.Z);
        var local = Vector3.Transform(skyrim - origin, NifReader.PlacementRotationInverse(baseline.Rx, baseline.Ry, baseline.Rz));
        var angles = scene.EditedAngles ?? new Vector3(baseline.Rx, baseline.Ry, baseline.Rz);
        var position = scene.EditedPosition ?? origin;
        return NifReader.ToView(Vector3.Transform(local, NifReader.PlacementRotation(angles.X, angles.Y, angles.Z)) + position);
    }

    static Vector3 DisplayNormal(SceneObject scene, Vector3 viewNormal)
    {
        if (scene.EditedAngles is not Vector3 angles || Baseline(scene) is not PlacementView baseline)
        {
            return viewNormal;
        }

        var local = Vector3.Transform(NifReader.FromView(viewNormal), NifReader.PlacementRotationInverse(baseline.Rx, baseline.Ry, baseline.Rz));
        var turned = Vector3.Transform(local, NifReader.PlacementRotation(angles.X, angles.Y, angles.Z));
        var view = NifReader.ToView(turned);
        return view.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(view);
    }

    static string Degrees(float radians)
    {
        return (radians * (180f / MathF.PI)).ToString("0.0", CultureInfo.InvariantCulture) + "°";
    }

    void FillTextureMods(IReadOnlyList<PlacedPart> parts)
    {
        var diffuse = parts.Select(part => part.Diffuse).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        fillingMods = true;
        TextureMod.ItemsSource = null;
        var choices = diffuse == null ? null : AssetIndex.Find(diffuse).Select(hit => hit.ModName).Distinct().ToList();
        TextureMod.ItemsSource = choices;
        if (TextureMod.Items.Count > 0)
        {
            var picked = selected?.TextureMod == null ? -1 : choices!.IndexOf(selected.TextureMod);
            TextureMod.SelectedIndex = picked < 0 ? 0 : picked;
        }

        fillingMods = false;
    }

    void TexturesMenu_Checked(object sender, RoutedEventArgs e)
    {
        SetTextures(true);
    }

    void TexturesMenu_Unchecked(object sender, RoutedEventArgs e)
    {
        SetTextures(false);
    }

    void TextureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TextureToggle.IsChecked != texturesOn)
        {
            SetTextures(TextureToggle.IsChecked == true);
        }
    }

    void SetTextures(bool on)
    {
        texturesOn = on;
        if (TexturesMenuItem != null)
        {
            TexturesMenuItem.IsChecked = on;
        }

        if (TextureToggle != null && TextureToggle.IsChecked != on)
        {
            TextureToggle.IsChecked = on;
        }

        if (on && !texturesReady && shown.Count > 0)
        {
            LoadTextures();
            return;
        }

        foreach (var parts in shown.Values)
        {
            foreach (var part in parts)
            {
                ApplyMaps(part);
            }
        }
    }

    void NormalMapsMenu_Click(object sender, RoutedEventArgs e)
    {
        normalMapsOn = NormalMapsMenuItem.IsChecked;
        foreach (var parts in shown.Values)
        {
            foreach (var part in parts)
            {
                ApplyMaps(part);
            }
        }
    }

    void OcclusionMenu_Click(object sender, RoutedEventArgs e)
    {
        Viewport.EnableSSAO = OcclusionMenuItem.IsChecked;
    }

    void TextureMod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (fillingMods || selected == null || TextureMod.SelectedItem is not string mod || !shown.TryGetValue(selected, out var parts))
        {
            return;
        }

        selected.TextureMod = TextureMod.SelectedIndex <= 0 ? null : mod;
        foreach (var part in parts)
        {
            part.Texture = TextureFor(part.Diffuse, selected.TextureMod);
            part.NormalTexture = TextureFor(part.Normal, selected.TextureMod);
            ApplyMaps(part);
        }

        UpdateResetButton(selected);
    }

    static IEnumerable<string> MapPaths(IEnumerable<PlacedPart> parts)
    {
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part.Diffuse))
            {
                yield return part.Diffuse;
            }

            if (!string.IsNullOrWhiteSpace(part.NormalMap))
            {
                yield return part.NormalMap;
            }
        }
    }

    void LoadTextures()
    {
        var generation = ++textureGeneration;
        var roots = PluginCatalog.MeshRoots();
        var paths = MapPaths(shown.Keys
                .SelectMany(scene => scene.Mesh.Versions.SelectMany(version => version.Parts)
                    .Concat(scene.SwapParts ?? [])))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        log.Info("Loading " + paths.Length + " textures.");
        Task.Run(() =>
        {
            try
            {
                AssetIndex.Ensure(roots);
                Dispatcher.BeginInvoke(() => log.Info("Texture index ready."));
                var decoded = new Dictionary<string, DdsImage.Decoded?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < paths.Length; i++)
                {
                    if (generation != textureGeneration)
                    {
                        return;
                    }

                    if (i % 20 == 0)
                    {
                        var done = i;
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (generation == textureGeneration)
                            {
                                SetBusy("Loading textures", done + " / " + paths.Length);
                            }
                        });
                    }

                    var path = paths[i];
                    var hit = AssetIndex.Find(path).FirstOrDefault();
                    var bytes = hit?.Read();
                    decoded[path] = bytes == null ? null : DdsImage.Decode(bytes);
                }

                Dispatcher.BeginInvoke(() => ApplyTextures(generation, decoded));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation == textureGeneration)
                    {
                        ClearBusy();
                    }

                    log.Error("Textures failed: " + ex.Message);
                });
            }
        });
    }

    void ApplyTextures(int generation, Dictionary<string, DdsImage.Decoded?> decoded)
    {
        if (generation != textureGeneration)
        {
            return;
        }

        var applied = 0;
        var missing = 0;
        foreach (var pair in decoded)
        {
            if (pair.Value == null)
            {
                missing++;
                continue;
            }

            var texture = DdsImage.ToModel(pair.Value.Value);
            textureCache[pair.Key] = texture;
            applied++;
        }

        RefreshMaps();
        texturesReady = true;
        if (selected != null)
        {
            FillProperties(selected);
        }

        log.Info("Textures — " + applied + " loaded, " + missing + " missing.");
        ClearBusy();
    }

    static int FormBias(SceneObject scene)
    {
        var id = scene.Mesh.FormId;
        if (string.IsNullOrEmpty(id))
        {
            return 0;
        }

        return id[^1] % 8;
    }

    static List<Vector3> SurfaceNormals(PlacedPart part)
    {
        var face = FaceNormals(part.Positions, part.TriangleIndices);
        if (part.Normals.Count != part.Positions.Count)
        {
            return face;
        }

        var result = new List<Vector3>(part.Normals.Count);
        for (var i = 0; i < part.Normals.Count; i++)
        {
            var normal = part.Normals[i];
            result.Add(normal.LengthSquared() < 1e-8f ? face[i] : normal);
        }

        return result;
    }

    static List<Vector3> FaceNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var normals = new Vector3[positions.Count];
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = positions[indices[i]];
            var b = positions[indices[i + 1]];
            var c = positions[indices[i + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            normals[indices[i]] += normal;
            normals[indices[i + 1]] += normal;
            normals[indices[i + 2]] += normal;
        }

        var result = new List<Vector3>(normals.Length);
        foreach (var normal in normals)
        {
            result.Add(normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal));
        }

        return result;
    }

    void FrameScene(SceneObject scene)
    {
        var positions = new List<Vector3>();
        foreach (var part in FramePartsForScene(scene))
        {
            positions.AddRange(part.Positions.Select(point => DisplayPoint(scene, point)));
        }

        if (positions.Count > 0)
        {
            Frame(positions);
        }
    }

    void Frame(IReadOnlyList<Vector3> positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var center = (min + max) * 0.5f;
        var radius = Math.Max(1f, (max - min).Length() * 0.5f);
        var eye = center + new Vector3(radius, radius * 0.45f, radius);
        if (Viewport.Camera is not HxCamera camera)
        {
            return;
        }

        camera.Position = new Point3D(eye.X, eye.Y, eye.Z);
        camera.LookDirection = new Vector3D(center.X - eye.X, center.Y - eye.Y, center.Z - eye.Z);
        camera.UpDirection = new Vector3D(0, 1, 0);
    }
}

sealed class PartView
{
    public required MeshGeometryModel3D Model { get; init; }

    public required PhongMaterial Material { get; init; }

    public TextureModel? Texture { get; set; }

    public TextureModel? NormalTexture { get; set; }

    public string? Diffuse { get; init; }

    public string? Normal { get; init; }
}
