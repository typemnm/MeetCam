using MeetCam.Source;
using OpenCvSharp;

namespace MeetCam;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            // Keep the test off the live camera file. A second writer there
            // races the app that is already broadcasting.
            Environment.SetEnvironmentVariable(
                "MEETCAM_FRAME",
                Path.Combine(Path.GetTempPath(), "meetcam-self-test-frame.bin"));
            MeetPage.SelfCheck();
            TestFrameRoundtrip();
            TestReaderSurvivesPublishing();
            TestVideoLoops();
            Console.WriteLine("SELF_TEST_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELF_TEST_FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void TestFrameRoundtrip()
    {
        var buffer = new byte[FramePipe.PixelBytes];
        buffer[0] = 10;
        buffer[1] = 20;
        buffer[2] = 30;
        buffer[3] = 255;
        buffer[^4] = 7;
        var writer = new FramePipe.Writer();
        writer.Publish(buffer);
        writer.Dispose();
        writer.Publish(buffer);

        var read = new byte[FramePipe.PixelBytes];
        using var reader = new FramePipe.Reader();
        if (!reader.TryCopyLatest(read))
            throw new InvalidOperationException("프레임 파일을 다시 읽지 못했습니다.");
        if (read[0] != 10 || read[1] != 20 || read[2] != 30 || read[^4] != 7)
            throw new InvalidOperationException("프레임 내용이 맞지 않습니다.");
    }

    private static void TestReaderSurvivesPublishing()
    {
        var buffer = new byte[FramePipe.PixelBytes];
        var read = new byte[FramePipe.PixelBytes];
        using var writer = new FramePipe.Writer();
        using var reader = new FramePipe.Reader();
        writer.Publish(buffer);
        var torn = 0;
        var writerThread = new Thread(() =>
        {
            for (var n = 0; n < 40; n++)
            {
                buffer[0] = buffer[^2] = (byte)(n + 1);
                buffer[1] = buffer[^1] = (byte)n;
                writer.Publish(buffer);
            }
        })
        { IsBackground = true };
        writerThread.Start();
        while (writerThread.IsAlive)
        {
            // A miss here means the copy was torn, so the camera repeats the
            // previous picture. A successful copy must still be one whole frame.
            if (!reader.TryCopyLatest(read))
                continue;
            if (read[0] != read[^2] || read[1] != read[^1])
                Interlocked.Increment(ref torn);
        }
        writerThread.Join();
        if (torn != 0)
            throw new InvalidOperationException($"송출 중 프레임이 섞여 읽힘 {torn}회");

        var misses = 0;
        for (var n = 0; n < 20; n++)
        {
            buffer[0] = buffer[^2] = (byte)(n + 1);
            buffer[1] = buffer[^1] = (byte)n;
            writer.Publish(buffer);
            Thread.Sleep(15);
            if (!reader.TryCopyLatest(read) || read[0] != buffer[0] || read[^1] != buffer[^1])
                Interlocked.Increment(ref misses);
        }
        if (misses != 0)
            throw new InvalidOperationException($"송출 중 프레임 읽기 실패 {misses}회");
    }

    private static void TestVideoLoops()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetcam-self-test.avi");
        using (var writer = new VideoWriter(path, FourCC.MJPG, 20, new OpenCvSharp.Size(640, 360)))
        {
            if (!writer.IsOpened())
                throw new InvalidOperationException("테스트 영상을 만들지 못했습니다.");
            using var frame = new Mat(360, 640, MatType.CV_8UC3);
            frame.SetTo(new Scalar(40, 220, 30));
            for (var i = 0; i < 6; i++)
                writer.Write(frame);
        }

        using var pump = new VideoPump();
        pump.Start(path);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && pump.LoopCount < 1 && pump.Error == null)
            Thread.Sleep(50);
        var loops = pump.LoopCount;
        var frames = pump.FramesPublished;
        var error = pump.Error;
        pump.Stop();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }

        if (error != null)
            throw error;
        if (frames < 6)
            throw new InvalidOperationException($"프레임이 너무 적습니다: {frames}");
        if (loops < 1)
            throw new InvalidOperationException("영상이 끝나도 반복 재생되지 않았습니다.");

        var read = new byte[FramePipe.PixelBytes];
        using var reader = new FramePipe.Reader();
        if (!reader.TryCopyLatest(read))
            throw new InvalidOperationException("송출 프레임이 없습니다.");

        var center = ((Shared.FrameHeight / 2) * Shared.FrameWidth + (Shared.FrameWidth / 2)) * 4;
        var blue = read[center];
        var green = read[center + 1];
        var red = read[center + 2];
        if (green < 150 || red > 120 || blue > 120)
            throw new InvalidOperationException($"가운데 픽셀이 테스트 색이 아닙니다: B{blue} G{green} R{red}");
    }
}
