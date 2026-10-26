using System.IO;
using CellEditor.Plugins;
using CellEditor.Textures;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CellEditor;

public partial class App : Application
{
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly IntPtr HWND_NOTOPMOST = new(-2);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_SHOWWINDOW = 0x0040;
    const int SW_RESTORE = 9;

    public App()
    {
        Write("app ctor session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        DispatcherUnhandledException += (_, e) =>
        {
            Write("ui " + e.Exception);
            e.Handled = true;
            MessageBox.Show(e.Exception.Message, "Cell Editor");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("crash " + e.ExceptionObject);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Write("startup");
        base.OnStartup(e);
        if (MainWindow != null)
        {
            MainWindow.SourceInitialized += (_, _) => BringToFront(MainWindow);
            MainWindow.Loaded += (_, _) => BringToFront(MainWindow);
        }

        BringToFront(MainWindow);
        Write("startup done");
        Exit += (_, args) =>
        {
            try
            {
                var removed = MeshCache.DeleteUnused(PluginCatalog.MeshRoots());
                Write("mesh cache removed " + removed);
            }
            catch (Exception ex)
            {
                Write("mesh cache " + ex.Message);
            }

            Write("exit " + args.ApplicationExitCode);
        };
    }

    public static void BringToFront(Window? window)
    {
        if (window == null)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
            window.Topmost = true;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                ForceForeground(handle);
            }

            window.Dispatcher.BeginInvoke(
                () =>
                {
                    window.Topmost = false;
                    window.Activate();
                },
                DispatcherPriority.ApplicationIdle);
        });
    }

    static void ForceForeground(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = false;
        if (foregroundThread != 0 && foregroundThread != thisThread)
        {
            attached = AttachThreadInput(foregroundThread, thisThread, true);
        }

        ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        if (attached)
        {
            AttachThreadInput(foregroundThread, thisThread, false);
        }
    }

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(AppHome.Logs);
            File.AppendAllText(AppHome.LaunchLog, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch
        {
        }
    }
}

