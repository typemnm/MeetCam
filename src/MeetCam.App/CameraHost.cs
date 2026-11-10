using DirectN;
using MeetCam.Source;

namespace MeetCam;

internal sealed class CameraHost : IDisposable
{
    private IComObject<IMFVirtualCamera>? _camera;
    private bool _mediaFoundationStarted;

    public void Start()
    {
        Stop();
        if (!_mediaFoundationStarted)
        {
            MFFunctions.MFStartup();
            _mediaFoundationStarted = true;
        }

        var hr = Functions.MFCreateVirtualCamera(
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
            Shared.CameraName,
            "{" + Shared.CLSID_MeetCam + "}",
            null,
            0,
            out var camera);
        hr.ThrowOnError();

        _camera = new ComObject<IMFVirtualCamera>(camera);
        _camera.Object.Start(null).ThrowOnError();
    }

    public void Stop()
    {
        var camera = _camera;
        _camera = null;
        if (camera == null)
            return;

        try
        {
            camera.Object.Remove();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            camera.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        if (_mediaFoundationStarted)
        {
            MFFunctions.MFShutdown();
            _mediaFoundationStarted = false;
        }
    }
}
