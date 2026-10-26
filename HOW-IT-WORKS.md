# How the cell editor works

Out-of-game viewer for one Skyrim SE cell, plus a small patch writer. It does not run the game. A restart is required before a code change shows up.

Read this before changing load order, enable state, meshes, or patches. The short rules for agents are in `AGENTS.md`.

## Start

`CellEditor.csproj` builds `CellEditorApp.exe` (WPF, Helix Toolkit, Mutagen 0.54.4). The same build compiles `Launcher.cs` into `CellEditor.exe`, which MO2 can start. The launcher only spawns `CellEditorApp.exe` beside it.

On startup the window opens the plugin picker (`PluginCatalog.Load`), then indexes named cells from the plugins the user left checked (`CellBrowser.Open`). File → Open Cell searches that index by editor id or name.

Logs go to the window and to `logs\session-*.log` next to the program (30 files kept). `logs\launch.log` is the startup log. While the window is open, `http://127.0.0.1:8932/` answers `GET /api/status`, `GET /api/logs`, and `POST /api/open` with a nif path.

## Where a plugin file comes from

The Mod Organizer folder is `mo2.txt` next to the program, or a parent folder when the program sits inside the instance. The first launch asks for the folder that contains `ModOrganizer.ini` and saves that choice. The ini names the selected profile and the game path. Plugin order is `loadorder.txt`. A plugin is active when `plugins.txt` marks it with `*`. Those two lists match for the active plugins.

The file on disk is the copy in the first **enabled** mod. `modlist.txt` is stored bottom-up: its first line is the highest priority. Only lines starting with `+` count. A disabled mod is not in the game, so its copy must not win. If no enabled mod has the file, the editor uses the game `Data` folder. This is `PluginCatalog.Load`.

## Who last edits a record

Plugins are opened in load order and kept in that order (`CellBrowser.mods`). For one reference, `CellBrowser.Load` walks every open plugin, and each plugin that contains the cell appends its override. The **last** entry in that list is the record winner: the later plugin in the load order.

The tree groups objects by the winning plugin, in that same order. Unchecking a plugin hides the objects it won and the changes it applied. Its dependents uncheck with it. Checking it checks the masters it needs. The plugin that owns the cell, and that plugin's masters, stay locked (`MastersClosure`, `DependentsClosure`).

Pose is not always taken from the record winner. `WinningPose` walks backwards and uses the last enabled override that stored a placement. A later plugin that only changes the base keeps the earlier coordinates. Scale is taken from the latest enabled override that has one.

The version list on an object is newest first. Picking an older version in the properties panel is a preview. It is not written to a patch.

## On or off at game start

`StartsDisabled` is the state the game uses when the cell loads:

- Deleted, or placed at Z ≤ −29000, is off.
- If the reference has an enable parent, the parent's state wins. `SetEnableStateToOppositeOfParent` flips it. The reference's own Initially Disabled flag is ignored.
- The player `000014:Skyrim.esm` is always on.
- Otherwise Initially Disabled is the state.

The properties panel shows "Off at game start" and the parent line (`EnableParentText`).

## What gets drawn

Each reference needs a base that implements `IModeledGetter` and a model path. The path is resolved through `MeshCache.Locate` → `AssetIndex`. The nif is parsed by `NifReader`. A reference is listed in `CellLoad.Dropped` with a reason when the base is missing, the mesh is missing, or the mesh has no readable geometry. Lights, markers, and triggers often have no mesh and land there. The count is written to the log when the cell opens.

A base that keeps its mesh somewhere other than `IModeledGetter.Model` is not drawn. Armor world models are the known case.

## Which mesh and texture win

`AssetIndex.Find` returns hits best-first. `hits[0]` is what the game would load. `MeshCache` and the texture loader both use that.

1. Every loose file beats every BSA. Loose files follow mod priority: the first enabled mod in `modlist.txt` wins. `MeshRoots` lists enabled mods in that order, then the game `Data` folder.
2. BSAs are ranked by `PluginCatalog.ArchiveRanks`. `Skyrim.ini` `sResourceArchiveList` and `sResourceArchiveList2` are the lowest. Then each active plugin, in load order, owns `Name.bsa` and `Name - *.bsa`. A higher rank is loaded later and wins.
3. A BSA whose name matches no ini entry and no active plugin is not opened.

Extracted BSA meshes are cached under `mesh-cache\<mod>\` next to the program. File → Delete mesh cache removes folders for mods that are no longer in the list. Loose files are read from the mod folder and are not copied.

## How a mesh is placed

Skyrim is Z-up. The viewport is Y-up: `ToView` maps `(x, y, z)` to `(x, z, -y)`.

The game replaces the nif root node's transform with the reference placement, so `NifReader` treats the root as identity. Child node transforms still apply. A `Matrix33` in the nif is column-major and is read straight into the row-vector matrix.

Reference rotation is clockwise. `PlacementRotation` is `Rz(-z) * Ry(-y) * Rx(-x)`, then scale and translation. The result is in `TransformPose`.

Readable nifs are stream 100 (SE) and stream 83 (LE `NiTriShape` / `NiTriShapeData`). An SE skinned `BSTriShape` has `dataSize` 0; its vertices and triangles are in the `NiSkinPartition`.

## Categories

`categories.json` next to the csproj. Rules run top to bottom; the first match wins. Inside one rule, every field that is set must match, and any value inside a field may match. Fields: `types` (base signature), `path` (text in the model path), `editorId`, `keywords`, `marker`. `overrides` maps a reference form key to a name and wins over the rules. Unmatched objects are `Other`.

The bar above the tree is a filter. A click hides that category. A right-click shows only that category. View → Reload categories rereads the file without a restart. There is no per-object category edit.

## Edits and the patch

The properties panel can change position (degrees in the boxes, radians in the record), swap the base model, and mark the object disabled. Ctrl+S / File → Create patch writes those objects (`CellBrowser.WritePatch`).

A new plugin is an `.esp` with the ESL flag `0x200` and HEDR 1.71. It is written with Mutagen `BeginWrite`, the open load order, and known masters, to a temp file, then moved into place. The source override is `GetOrAddAsOverride` from the newest plugin Mutagen can read. A plugin it cannot parse is skipped and named in the log; an earlier copy is used.

Disable writes Z −30000, Initially Disabled, and an enable parent on the player with the opposite-state flag. A new mod folder is named `[MY] ...`, inserted at the top of `modlist.txt` (highest priority), and the plugin is appended to `plugins.txt` and `loadorder.txt`. Those files are CRLF with no BOM. MO2 shows the mod after F5.

Texture choice and the plugin checkboxes are view-only. They are not written.
