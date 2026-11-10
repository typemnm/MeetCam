using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetCam.Source;
using OpenCvSharp;

namespace MeetCam;

internal sealed class VideoPump : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Thread? _thread;
    private FramePipe.Writer? _writer;
    private byte[]? _preview;
    private bool _previewDirty;
    private int _loopCount;
    private int _framesPublished;
    private Exception? _error;

    public int LoopCount => Volatile.Read(ref _loopCount);
    public int FramesPublished => Volatile.Read(ref _framesPublished);
    public Exception? Error => Volatile.Read(ref _error);
    public double SourceFps { get; private set; }
    public double DurationSeconds { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }

    public void Start(string path)
    {
        Stop();
        Interlocked.Exchange(ref _loopCount, 0);
        Interlocked.Exchange(ref _framesPublished, 0);
        Volatile.Write(ref _error, null);
        SourceWidth = 0;
        SourceHeight = 0;
        SourceFps = 0;
        DurationSeconds = 0;

        var writer = new FramePipe.Writer();
        _writer = writer;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _thread = new Thread(() => Run(path, writer, token))
        {
            IsBackground = true,
            Name = "MeetCam.Video",
        };
        _thread.Start();
    }

    public void Stop()
    {
        var cancel = _cancel;
        var thread = _thread;
        var writer = _writer;
        _cancel = null;
        _thread = null;
        _writer = null;
        cancel?.Cancel();
        if (thread != null && !thread.Join(TimeSpan.FromSeconds(3)))
        {
            writer?.Dispose();
            var pending = thread;
            Task.Run(() =>
            {
                try
                {
                    pending.Join();
                }
                finally
                {
                    cancel?.Dispose();
                }
            });
            return;
        }

        writer?.Dispose();
        cancel?.Dispose();
    }

    public bool TryCopyPreview(byte[] destination)
    {
        lock (_gate)
        {
            if (!_previewDirty || _preview == null || destination.Length < _preview.Length)
                return false;
            Buffer.BlockCopy(_preview, 0, destination, 0, _preview.Length);
            _previewDirty = false;
            return true;
        }
    }

    private void Run(string path, FramePipe.Writer writer, CancellationToken token)
    {
        var buffer = new byte[FramePipe.PixelBytes];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var capture = new VideoCapture(path);
            using var raw = new Mat();
            using var oriented = new Mat();
            using var canvas = Mat.FromPixelData(
                Shared.FrameHeight,
                Shared.FrameWidth,
                MatType.CV_8UC4,
                pin.AddrOfPinnedObject(),
                Shared.FrameWidth * 4);

            if (!capture.IsOpened())
                throw new IOException("영상을 열 수 없습니다. mp4, mkv, avi, mov, webm 파일을 확인해 주세요.");

            var fps = SanitizeFps(capture.Fps);
            SourceWidth = capture.FrameWidth;
            SourceHeight = capture.FrameHeight;
            SourceFps = fps;
            var frameCount = capture.FrameCount;
            DurationSeconds = frameCount > 0 && fps > 0 ? frameCount / fps : 0;
            var orientation = (int)capture.Get((VideoCaptureProperties)48);
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            var clock = Stopwatch.StartNew();
            var index = 0L;

            while (!token.IsCancellationRequested)
            {
                if (!ReadFrame(capture, path, raw))
                    throw new IOException("영상에서 프레임을 읽지 못했습니다.");

                var source = ApplyOrientation(raw, oriented, orientation);
                Compose(source, canvas);
                writer.Publish(buffer);
                Interlocked.Increment(ref _framesPublished);
                StorePreview(buffer);

                index++;
                var wait = (interval * index) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    if (token.WaitHandle.WaitOne(wait))
                        break;
                }
                else if (wait < TimeSpan.FromMilliseconds(-500))
                {
                    clock.Restart();
                    index = 0;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
                Volatile.Write(ref _error, ex);
        }
        finally
        {
            pin.Free();
        }
    }

    private bool ReadFrame(VideoCapture capture, string path, Mat frame)
    {
        if (ReadOne(capture, frame))
            return true;
        // One extra read. A single miss is not the end of the file.
        if (ReadOne(capture, frame))
            return true;

        Interlocked.Increment(ref _loopCount);
        capture.Set(VideoCaptureProperties.PosFrames, 0);
        if (!ReadOne(capture, frame))
        {
            capture.Release();
            if (!capture.Open(path) || !ReadOne(capture, frame))
                return false;
        }

        // After a seek, some decoders emit one or two blank frames before the picture.
        for (var attempt = 0; attempt < 2 && IsNearlyBlack(frame); attempt++)
        {
            using var next = new Mat();
            if (!ReadOne(capture, next))
                break;
            next.CopyTo(frame);
        }
        return !frame.Empty();
    }

    private static bool ReadOne(VideoCapture capture, Mat frame) => capture.Read(frame) && !frame.Empty();

    private static bool IsNearlyBlack(Mat frame)
    {
        if (frame.Empty())
            return true;
        var mean = Cv2.Mean(frame);
        return mean.Val0 < 3 && mean.Val1 < 3 && mean.Val2 < 3;
    }

    private static Mat ApplyOrientation(Mat raw, Mat oriented, int orientation)
    {
        var flag = orientation switch
        {
            90 => RotateFlags.Rotate90Clockwise,
            180 => RotateFlags.Rotate180,
            270 => RotateFlags.Rotate90Counterclockwise,
            _ => (RotateFlags?)null,
        };
        if (flag == null)
            return raw;

        Cv2.Rotate(raw, oriented, flag.Value);
        return oriented;
    }

    private static void Compose(Mat source, Mat canvas)
    {
        canvas.SetTo(new Scalar(0, 0, 0, 255));
        if (source.Empty())
            return;

        var scale = Math.Min(canvas.Width / (double)source.Width, canvas.Height / (double)source.Height);
        var width = Math.Max(2, (int)Math.Round(source.Width * scale)) & ~1;
        var height = Math.Max(2, (int)Math.Round(source.Height * scale)) & ~1;
        using var resized = new Mat();
        using var bgra = new Mat();
        Cv2.Resize(source, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Linear);
        var conversion = resized.Channels() switch
        {
            1 => ColorConversionCodes.GRAY2BGRA,
            4 => (ColorConversionCodes?)null,
            _ => ColorConversionCodes.BGR2BGRA,
        };
        if (conversion == null)
            resized.CopyTo(bgra);
        else
            Cv2.CvtColor(resized, bgra, conversion.Value);
        var x = (canvas.Width - width) / 2;
        var y = (canvas.Height - height) / 2;
        using var roi = canvas[new Rect(x, y, width, height)];
        bgra.CopyTo(roi);
    }

    private void StorePreview(byte[] buffer)
    {
        lock (_gate)
        {
            _preview ??= new byte[buffer.Length];
            Buffer.BlockCopy(buffer, 0, _preview, 0, buffer.Length);
            _previewDirty = true;
        }
    }

    private static double SanitizeFps(double fps) => fps is >= 1 and <= 240 ? fps : 30;

    public void Dispose() => Stop();
}
