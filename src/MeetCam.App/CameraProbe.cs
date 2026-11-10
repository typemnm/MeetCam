using System.Runtime.InteropServices;
using DirectN;
using MeetCam.Source;

namespace MeetCam;

internal static class CameraProbe
{
    public static int Run()
    {
        if (!Registrar.IsRegistered())
        {
            Console.Error.WriteLine("CAMERA_TEST_FAIL not registered");
            return 1;
        }

        var magenta = new byte[FramePipe.PixelBytes];
        for (var i = 0; i < magenta.Length; i += 4)
        {
            magenta[i] = 255;
            magenta[i + 1] = 0;
            magenta[i + 2] = 255;
            magenta[i + 3] = 255;
        }

        using var frames = new FramePipe.Writer();
        frames.Publish(magenta);
        using var camera = new CameraHost();
        try
        {
            camera.Start();
            var pixel = ReadCenterPixel();
            Console.WriteLine($"CENTER B{pixel.B} G{pixel.G} R{pixel.R} A{pixel.A}");
            if (pixel.B < 200 || pixel.R < 200 || pixel.G > 40)
                throw new InvalidOperationException("카메라가 보낸 색이 테스트 프레임과 다릅니다.");

            Console.WriteLine("CAMERA_TEST_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CAMERA_TEST_FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            camera.Stop();
        }
    }

    private static (byte B, byte G, byte R, byte A) ReadCenterPixel()
    {
        var attributes = MFFunctions.MFCreateAttributes(1);
        attributes.Object.SetGUID(
            MFConstants.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
            MFConstants.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID).ThrowOnError();

        nint array;
        uint count;
        Functions.MFEnumDeviceSources(attributes.Object, out array, out count).ThrowOnError();
        IMFMediaSource? source = null;
        var names = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var unknown = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(unknown);
                var name = activate.GetString(MFConstants.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                if (string.IsNullOrWhiteSpace(name))
                    name = "(unnamed)";
                names.Add(name);
                if (source == null && name.Contains(Shared.CameraName, StringComparison.OrdinalIgnoreCase))
                {
                    activate.ActivateObject(typeof(IMFMediaSource).GUID, out var activated);
                    source = (IMFMediaSource)activated;
                }
                else
                {
                    Marshal.Release(unknown);
                }
            }
        }
        finally
        {
            if (array != IntPtr.Zero)
                Marshal.FreeCoTaskMem(array);
        }

        Console.WriteLine("CAMERAS " + string.Join(" | ", names));
        if (source == null)
            throw new InvalidOperationException("MeetCam 이 카메라 목록에 없습니다.");

        Functions.MFCreateSourceReaderFromMediaSource(source, null!, out var reader).ThrowOnError();

        var deadline = DateTime.UtcNow.AddSeconds(8);
        var indexPtr = Marshal.AllocHGlobal(4);
        var flagsPtr = Marshal.AllocHGlobal(4);
        var timePtr = Marshal.AllocHGlobal(8);
        try
        {
        while (DateTime.UtcNow < deadline)
        {
            var hr = reader.ReadSample(0, 0, indexPtr, flagsPtr, timePtr, out var sample);
            if (hr.IsError || sample == null)
            {
                Thread.Sleep(30);
                continue;
            }

            using var com = new ComObject<IMFSample>(sample);
            using var buffer = com.GetBufferByIndex(0);
            (byte B, byte G, byte R, byte A)? pixel = null;
            buffer.WithLock((scan, length, _) =>
            {
                if (length < 4)
                    return;
                var center = (int)Math.Min(length - 4, (uint)((Shared.FrameHeight / 2) * Shared.FrameWidth + (Shared.FrameWidth / 2)) * 4);
                pixel = (Marshal.ReadByte(scan, center), Marshal.ReadByte(scan, center + 1), Marshal.ReadByte(scan, center + 2), Marshal.ReadByte(scan, center + 3));
            });
            if (pixel != null)
                return pixel.Value;
        }

        throw new TimeoutException("카메라에서 프레임을 받지 못했습니다.");
        }
        finally
        {
            Marshal.FreeHGlobal(indexPtr);
            Marshal.FreeHGlobal(flagsPtr);
            Marshal.FreeHGlobal(timePtr);
        }
    }
}
