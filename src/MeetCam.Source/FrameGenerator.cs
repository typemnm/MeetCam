using System;
using System.Runtime.InteropServices;
using DirectN;
using MeetCam.Source.Utilities;

namespace MeetCam.Source
{
    public class FrameGenerator : IDisposable
    {
        private bool _disposedValue;
        private uint _width;
        private uint _height;
        private ulong _frameCount;
        private IntPtr _deviceHandle;
        private IComObject<ID3D11Texture2D>? _texture;
        private IComObject<ID2D1RenderTarget>? _renderTarget;
        private IComObject<ID2D1SolidColorBrush>? _whiteBrush;
        private IComObject<IDWriteTextFormat>? _textFormat;
        private IComObject<IDWriteFactory>? _dwrite;
        private ComObject<IMFTransform>? _converter;
        private IComObject<IWICBitmap>? _bitmap;
        private ComObject<IMFDXGIDeviceManager>? _dxgiManager;
        private readonly byte[] _frameScratch = new byte[FramePipe.PixelBytes];
        private readonly FramePipe.Reader _frames = new();
        private bool _haveFrame;

        public bool HasD3DManager => _texture != null;
        public ulong FrameCount => _frameCount;

        // common to CPU & GPU
        private HRESULT CreateRenderTargetResources(uint width, uint height)
        {
            if (_renderTarget == null)
                return HRESULTS.E_FAIL;

            _whiteBrush = _renderTarget.CreateSolidColorBrush(new _D3DCOLORVALUE(1, 1, 1, 1));
            _dwrite = DWriteFunctions.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED);
            _textFormat = _dwrite.CreateTextFormat("Segoe UI", 40);
            _textFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            _textFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            _width = width;
            _height = height;
            return HRESULTS.S_OK;
        }

        private void SetConverterTypes(uint width, uint height)
        {
            Functions.MFCreateMediaType(out var inputType).ThrowOnError();
            inputType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            inputType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
            inputType.SetSize(MFConstants.MF_MT_FRAME_SIZE, width, height);
            _converter!.Object.SetInputType(0, inputType, 0).ThrowOnError();

            Functions.MFCreateMediaType(out var outputType).ThrowOnError();
            outputType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
            outputType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
            outputType.SetSize(MFConstants.MF_MT_FRAME_SIZE, width, height);
            _converter!.Object.SetOutputType(0, outputType, 0).ThrowOnError();
        }

        public HRESULT SetD3DManager(object manager, uint width, uint height)
        {
            if (manager == null)
                return HRESULTS.E_POINTER;

            if (width == 0 || height == 0)
                return HRESULTS.E_INVALIDARG;

            if (manager is not IMFDXGIDeviceManager dxgiManager)
                return HRESULTS.E_NOTIMPL;

            _dxgiManager = new ComObject<IMFDXGIDeviceManager>(dxgiManager);
            _dxgiManager.Object.OpenDeviceHandle(out _deviceHandle).ThrowOnError();
            _dxgiManager.Object.GetVideoService(_deviceHandle, typeof(ID3D11Device).GUID, out var obj).ThrowOnError();

            // create a texture/surface to write
            using var device = new ComObject<ID3D11Device>((ID3D11Device)obj);
            _texture = device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Width = width,
                Height = height,
                ArraySize = 1,
                MipLevels = 1,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET)
            });

            // create a D2D1 render target from 2D GPU surface
            var surface = new ComObject<IDXGISurface>((IDXGISurface)_texture.Object, false);
            using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
            _renderTarget = factory.CreateDxgiSurfaceRenderTarget(surface, new D2D1_RENDER_TARGET_PROPERTIES { pixelFormat = new D2D1_PIXEL_FORMAT { alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED } });

            CreateRenderTargetResources(width, height).ThrowOnError();

            // create GPU RGB => NV12 converter
            _converter = new ComObject<IMFTransform>((IMFTransform)System.Activator.CreateInstance(Type.GetTypeFromCLSID(MFConstants.CLSID_VideoProcessorMFT)!)!);
            SetConverterTypes(width, height);

            // make sure the video processor works on GPU
            ComObject.WithComPointer(manager, unk => _converter!.Object.ProcessMessage(_MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, unk));
            EventProvider.LogInfo("OK");
            return HRESULTS.S_OK;
        }

        public HRESULT EnsureRenderTarget(uint width, uint height)
        {
            try
            {
                if (!HasD3DManager)
                {
                    // create a D2D1 render target from WIC bitmap
                    using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
                    _bitmap = WICImagingFactory.CreateBitmap((int)width, (int)height, WICConstants.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand);
                    _renderTarget = factory.CreateWicBitmapRenderTarget(_bitmap, new D2D1_RENDER_TARGET_PROPERTIES
                    {
                        pixelFormat = new D2D1_PIXEL_FORMAT
                        {
                            alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                            format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
                        }
                    });

                    CreateRenderTargetResources(width, height).ThrowOnError();

                    // create CPU RGB => NV12 converter
                    _converter = new ComObject<IMFTransform>((IMFTransform)System.Activator.CreateInstance(Type.GetTypeFromCLSID(MFConstants.CLSID_CColorConvertDMO)!)!);
                    SetConverterTypes(width, height);
                }

                _frameCount = 0;
                return HRESULTS.S_OK;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public IComObject<IMFSample> Generate(IComObject<IMFSample> sample, Guid format)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(sample);
                IComObject<IMFSample>? outSample;

                if (!PaintSharedFrame())
                {
                    PaintStandby();
                }

                //EventProvider.LogInfo("format: " + format + " generated 3D:" + HasD3DManager);

                if (HasD3DManager)
                {
                    sample.RemoveAllBuffers(); // or create a new one?

                    // create a buffer from texture and add to sample
                    using var mediaBuffer = MFFunctions.MFCreateDXGISurfaceBuffer(_texture);
                    sample.Object.AddBuffer(mediaBuffer.Object).ThrowOnError();

                    // if we're on GPU & format is not RGB, convert using GPU (VideoProcessorMFT)
                    if (format == MFConstants.MFVideoFormat_NV12)
                    {
                        _converter!.Object.ProcessInput(0, sample.Object, 0).ThrowOnError();

                        // let converter build the sample for us, note it works because we gave it the D3DManager
                        var buffers = new _MFT_OUTPUT_DATA_BUFFER[1];
                        _converter.Object.ProcessOutput(0, (uint)buffers.Length, buffers, out var status).ThrowOnError();
                        outSample = ComObject.From<IMFSample>(buffers[0].pSample);
                        Marshal.Release(buffers[0].pSample);
                    }
                    else
                    {
                        outSample = sample; // nothing to do
                    }

                    _frameCount++;
                    return outSample;
                }

                // lock WIC bitmap to write to sample
                using var locked = _bitmap.Lock(WICBitmapLockFlags.WICBitmapLockRead);
                locked.Object.GetSize(out var w, out var h).ThrowOnError();
                locked.Object.GetStride(out var wicStride).ThrowOnError();
                locked.Object.GetDataPointer(out var wicSize, out var wicPointer).ThrowOnError();

                // if we're on CPU & format is NOT RGB, convert using CPU (CColorConvertDMO)
                if (format == MFConstants.MFVideoFormat_NV12)
                {
                    // create temp RGB sample for WIC bitmap
                    using var wicSample = MFFunctions.MFCreateSample();
                    using var wicBuffer = MFFunctions.MFCreateMemoryBuffer(wicSize);
                    wicSample.AddBuffer(wicBuffer);
                    wicBuffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                    wicBuffer.SetCurrentLength(wicSize);

                    _converter!.Object.ProcessInput(0, wicSample.Object, 0).ThrowOnError();

                    // convert RGB sample to NV12 sample
                    sample.WithComPointer(outSamplePtr =>
                    {
                        var buffers = new _MFT_OUTPUT_DATA_BUFFER[1];
                        buffers[0].pSample = outSamplePtr;
                        _converter.Object.ProcessOutput(0, 1, buffers, out var status).ThrowOnError();
                    });
                }
                else
                {
                    // sample is already for RGB
                    using var buffer = sample.GetBufferByIndex(0);
                    buffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                    EventProvider.LogInfo("format: " + format + " max: " + buffer.GetMaxLength() + " wicSize:" + wicSize);
                    buffer.SetCurrentLength(wicSize);
                }

                _frameCount++;
                return sample;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        private bool PaintSharedFrame()
        {
            if (_bitmap == null)
                return false;
            if (!_frames.TryCopyLatest(_frameScratch))
                return _haveFrame;

            using var locked = _bitmap.Lock(WICBitmapLockFlags.WICBitmapLockWrite);
            locked.Object.GetSize(out var width, out var height).ThrowOnError();
            locked.Object.GetStride(out var stride).ThrowOnError();
            locked.Object.GetDataPointer(out _, out var pointer).ThrowOnError();
            var srcStride = (int)width * 4;
            for (var y = 0; y < (int)height; y++)
            {
                Marshal.Copy(_frameScratch, y * srcStride, pointer + (y * (int)stride), srcStride);
            }
            _haveFrame = true;
            return true;
        }

        private void PaintStandby()
        {
            if (_renderTarget == null || _textFormat == null || _dwrite == null || _whiteBrush == null)
                return;

            _renderTarget.BeginDraw();
            _renderTarget.Clear(new _D3DCOLORVALUE(0.09f, 0.10f, 0.12f, 1));
            const string text = "MeetCam\n영상 대기 중";
            using var layout = _dwrite.CreateTextLayout(_textFormat, text, text.Length, _width, _height);
            _renderTarget.DrawTextLayout(new D2D_POINT_2F(0, 0), layout, _whiteBrush);
            _renderTarget.EndDraw();
        }

        protected virtual void Dispose(bool disposing)
        {
            EventProvider.LogInfo();
            try
            {
                if (!_disposedValue)
                {
                    if (disposing)
                    {
                        // dispose managed state (managed objects)
                        var dxgiManager = _dxgiManager;
                        if (_deviceHandle != IntPtr.Zero && dxgiManager != null)
                        {
                            dxgiManager.Object.CloseDeviceHandle(_deviceHandle);
                        }

                        _whiteBrush.SafeDispose();
                        _frames.Dispose();
                        _bitmap.SafeDispose();
                        _texture.SafeDispose();
                        _textFormat.SafeDispose();
                        _dwrite.SafeDispose();
                        _renderTarget.SafeDispose();
                        _converter.SafeDispose();
                    }

                    // free unmanaged resources (unmanaged objects) and override finalizer
                    // set large fields to null
                    _disposedValue = true;
                    EventProvider.LogInfo("Disposed");
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        // // override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~FrameGenerator()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
