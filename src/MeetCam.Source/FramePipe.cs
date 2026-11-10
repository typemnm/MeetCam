using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeetCam.Source
{
    // File-backed frame the camera source (Frame Server, session 0) and the
    // player (the signed-in user) can both see. A named Local\ mapping would
    // not cross those sessions.
    public static class FramePipe
    {
        public const int HeaderSize = 32;
        public const int SlotCount = 3;
        public const uint Magic = 0x56434D32; // 'VCM2'

        // MEETCAM_FRAME is only for tests. The installed camera and DirectShow
        // filter both default to C:\Users\Public\MeetCam\frame.bin.
        public static string FilePath
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("MEETCAM_FRAME");
                if (!string.IsNullOrWhiteSpace(overridePath))
                    return overridePath;
                return Path.Combine(Shared.InstallDir, Shared.FrameFileName);
            }
        }
        public static int PixelBytes => Shared.FrameWidth * Shared.FrameHeight * 4;
        public static int TotalBytes => HeaderSize + (SlotCount * PixelBytes);

        public static void EnsureDirectory()
        {
            Directory.CreateDirectory(Shared.InstallDir);
        }

        public sealed class Writer : IDisposable
        {
            private readonly FileStream _stream;
            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _view;
            private int _slot;
            private bool _disposed;

            public Writer()
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                _stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                _stream.SetLength(TotalBytes);
                _mmf = MemoryMappedFile.CreateFromFile(_stream, null, TotalBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                _view = _mmf.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
                WriteHeader();
            }

            public unsafe void Publish(ReadOnlySpan<byte> bgra)
            {
                if (bgra.Length < PixelBytes)
                    throw new ArgumentException("frame is smaller than 1280x720 BGRA", nameof(bgra));

                // Write a slot the camera is not currently reading, then publish
                // its index. A single shared buffer made the camera give up and
                // show black whenever it sampled during the copy.
                int slot = _slot;
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    fixed (byte* src = bgra)
                    {
                        Buffer.MemoryCopy(src, pointer + HeaderSize + (slot * PixelBytes), PixelBytes, PixelBytes);
                    }
                    Volatile.Write(ref *(int*)(pointer + 4), Shared.FrameWidth);
                    Volatile.Write(ref *(int*)(pointer + 8), Shared.FrameHeight);
                    Volatile.Write(ref *(int*)pointer, unchecked((int)Magic));
                    Volatile.Write(ref *(int*)(pointer + 16), slot);
                }
                finally
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                _slot = (slot + 1) % SlotCount;
            }

            private unsafe void WriteHeader()
            {
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    *(int*)pointer = unchecked((int)Magic);
                    *(int*)(pointer + 4) = Shared.FrameWidth;
                    *(int*)(pointer + 8) = Shared.FrameHeight;
                    *(int*)(pointer + 12) = Shared.FrameWidth * 4;
                    Volatile.Write(ref *(int*)(pointer + 16), -1);
                }
                finally
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _view.Dispose();
                _mmf.Dispose();
                _stream.Dispose();
            }
        }

        public sealed class Reader : IDisposable
        {
            private FileStream? _stream;
            private MemoryMappedFile? _mmf;
            private MemoryMappedViewAccessor? _view;
            private long _mappedLength;

            public unsafe bool TryCopyLatest(byte[] destination)
            {
                if (destination.Length < PixelBytes)
                    return false;
                if (!EnsureMapped())
                    return false;

                byte* pointer = null;
                _view!.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    if ((uint)*(int*)pointer != Magic)
                        return false;
                    if (*(int*)(pointer + 4) != Shared.FrameWidth || *(int*)(pointer + 8) != Shared.FrameHeight)
                        return false;

                    fixed (byte* dst = destination)
                    {
                        // Copy the published slot. If the writer laps that slot while
                        // the copy is in progress, the index changes and the finished
                        // slot is copied instead. A torn slot is not returned.
                        for (var attempt = 0; attempt < SlotCount; attempt++)
                        {
                            int slot = Volatile.Read(ref *(int*)(pointer + 16));
                            if ((uint)slot >= SlotCount)
                                return false;
                            Buffer.MemoryCopy(pointer + HeaderSize + (slot * PixelBytes), dst, PixelBytes, PixelBytes);
                            if (Volatile.Read(ref *(int*)(pointer + 16)) == slot)
                                return true;
                        }
                    }
                    return false;
                }
                finally
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            private bool EnsureMapped()
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return false;

                    var length = new FileInfo(FilePath).Length;
                    if (length < TotalBytes)
                        return false;
                    if (_view != null && _mappedLength == length)
                        return true;

                    DisposeMapping();
                    _stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    _view = _mmf.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.Read);
                    _mappedLength = length;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DisposeMapping();
                    return false;
                }
            }

            private void DisposeMapping()
            {
                _view?.Dispose();
                _mmf?.Dispose();
                _stream?.Dispose();
                _view = null;
                _mmf = null;
                _stream = null;
                _mappedLength = 0;
            }

            public void Dispose() => DisposeMapping();
        }
    }
}
