namespace MeetCam;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--register", StringComparison.OrdinalIgnoreCase)))
            return Registrar.RegisterInPlace();

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        if (args.Any(arg => arg.Equals("--camera-test", StringComparison.OrdinalIgnoreCase)))
            return CameraProbe.Run();

        ApplicationConfiguration.Initialize();
        var video = args.FirstOrDefault(arg => !arg.StartsWith('-'));
        Application.Run(new MainForm(video));
        return 0;
    }
}
