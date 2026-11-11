using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeetCam;

internal static class Program
{
    private const string SingleInstanceName = @"Local\MeetCam.SingleInstance";
    private static Mutex? _instance;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--register", StringComparison.OrdinalIgnoreCase)))
            return Registrar.RegisterInPlace();

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        if (args.Any(arg => arg.Equals("--camera-test", StringComparison.OrdinalIgnoreCase)))
            return CameraProbe.Run();

        if (!AcquireInstance())
            return 0;

        try
        {
            ApplicationConfiguration.Initialize();
            var video = args.FirstOrDefault(arg => !arg.StartsWith('-'));
            Application.Run(new MainForm(video));
            return 0;
        }
        finally
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    private static bool AcquireInstance()
    {
        _instance = new Mutex(false, SingleInstanceName);
        try
        {
            if (_instance.WaitOne(0))
                return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        FocusExistingWindow();
        _instance.Dispose();
        _instance = null;
        return false;
    }

    private static void FocusExistingWindow()
    {
        var current = Environment.ProcessId;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("MeetCam"))
            {
                if (process.Id == current)
                    continue;
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    continue;
                if (IsIconic(handle))
                    ShowWindow(handle, 9);
                SetForegroundWindow(handle);
                return;
            }

            Thread.Sleep(100);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
