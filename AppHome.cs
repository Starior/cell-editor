using System.IO;

namespace CellEditor;

/// <summary>Files the program writes sit next to the executable, whatever folder it was installed in.</summary>
public static class AppHome
{
    public static string Directory => AppContext.BaseDirectory;

    public static string Logs => Path.Combine(Directory, "logs");

    public static string LaunchLog => Path.Combine(Logs, "launch.log");

    public static string MeshCache => Path.Combine(Directory, "mesh-cache");

    public static string Mo2File => Path.Combine(Directory, "mo2.txt");
}
