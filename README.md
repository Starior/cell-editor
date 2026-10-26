# Cell editor

Out-of-game Skyrim SE cell viewer and patcher.

Copyright (C) 2026 Starior

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3 only. The license text is [LICENSE](LICENSE). This program comes with no warranty.

Third-party libraries are NuGet packages. Their notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build

Windows, the .NET 10 SDK (the project targets `net10.0-windows` and uses WPF), and the .NET Framework compiler that ships with Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). The first build downloads NuGet packages.

From this folder:

```
dotnet build CellEditor.csproj
```

The output is `bin\Debug\net10.0-windows\`. `CellEditor.exe` is the launcher Mod Organizer can start. It runs `CellEditorApp.exe` beside it. `categories.json` is read from this folder while the program runs from that build output. Copy `categories.json` next to the exe if you move the program out of the repository.

Skyrim Special Edition and Mod Organizer 2 are needed to open a load order. The first launch asks for the Mod Organizer folder (the one that contains `ModOrganizer.ini`) and saves it in `mo2.txt` next to the program. Logs go to `logs\`, extracted meshes to `mesh-cache\`, both next to the program.

How record, mesh, and patch winners are chosen is [HOW-IT-WORKS.md](HOW-IT-WORKS.md).
