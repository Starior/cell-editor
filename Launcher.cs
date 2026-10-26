using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    [STAThread]
    static void Main()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            var exe = Path.Combine(dir, "CellEditorApp.exe");
            Log("dir=" + dir);
            Log("exe=" + exe + " exists=" + File.Exists(exe));
            if (!File.Exists(exe))
            {
                MessageBox.Show("CellEditorApp.exe was not found next to the launcher.", "Cell Editor");
                return;
            }

            var wmi = new ManagementClass("Win32_Process");
            var input = wmi.GetMethodParameters("Create");
            input["CommandLine"] = "\"" + exe + "\"";
            input["CurrentDirectory"] = dir;
            var output = wmi.InvokeMethod("Create", input, null);
            var code = (uint)output["ReturnValue"];
            var pid = output["ProcessId"];
            Log("wmi=" + code + " pid=" + pid);
            if (code != 0)
            {
                MessageBox.Show("Windows did not start the editor. Code " + code + ".", "Cell Editor");
            }
            else if (pid != null)
            {
                var childPid = Convert.ToInt32(pid);
                AllowSetForegroundWindow(-1);
                if (childPid != 0)
                {
                    AllowSetForegroundWindow(childPid);
                }

                // Stay the foreground process long enough for the editor window to take focus.
                System.Threading.Thread.Sleep(2500);
            }
        }
        catch (Exception ex)
        {
            Log("error " + ex);
            MessageBox.Show(ex.Message, "Cell Editor");
        }
    }

    static void Log(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".";
            var logs = Path.Combine(dir, "logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "launch.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
