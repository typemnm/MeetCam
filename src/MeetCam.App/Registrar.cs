using System.Diagnostics;
using Microsoft.Win32;
using MeetCam.Source;

namespace MeetCam;

internal static class Registrar
{
    public static bool IsRegistered()
    {
        var keyPath = $@"SOFTWARE\Classes\CLSID\{{{Shared.CLSID_MeetCam}}}\InprocServer32";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        var path = key?.GetValue(null) as string;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static bool IsDirectShowRegistered()
    {
        var keyPath = $@"SOFTWARE\Classes\CLSID\{{860BB310-5D01-11d0-BD3B-00A0C911CE86}}\Instance\{{{Shared.CLSID_MeetCamDirectShow}}}";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        var name = key?.GetValue("FriendlyName") as string;
        if (!string.Equals(name, Shared.CameraName, StringComparison.Ordinal))
            return false;

        var serverKey = $@"SOFTWARE\Classes\CLSID\{{{Shared.CLSID_MeetCamDirectShow}}}\InprocServer32";
        using var server = Registry.LocalMachine.OpenSubKey(serverKey);
        var path = server?.GetValue(null) as string;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static int RegisterInPlace()
    {
        var directory = AppContext.BaseDirectory;
        var comhost = Path.Combine(directory, "MeetCam.Source.comhost.dll");
        var directShow = Path.Combine(directory, "MeetCamDShow.dll");
        if (!File.Exists(comhost) || !File.Exists(directShow))
        {
            Console.Error.WriteLine("MeetCam.Source.comhost.dll 또는 MeetCamDShow.dll 이 실행 파일 옆에 없습니다.");
            return 2;
        }

        var mediaFoundation = RunRegsvr32(comhost);
        if (mediaFoundation != 0)
            return mediaFoundation;
        var directShowResult = RunRegsvr32(directShow);
        if (directShowResult != 0)
            return directShowResult;

        return IsRegistered() && IsDirectShowRegistered() ? 0 : 4;
    }

    private static int RunRegsvr32(string dll)
    {
        using var process = Process.Start(new ProcessStartInfo("regsvr32.exe", $"/s \"{dll}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process == null)
            return 3;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"regsvr32 {Path.GetFileName(dll)} 가 {process.ExitCode} 로 끝났습니다. 관리자 권한이 필요합니다.");
            return process.ExitCode;
        }

        return 0;
    }

    public static bool TryElevateAndRegister()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--register")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (process == null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0 && IsRegistered() && IsDirectShowRegistered();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}
