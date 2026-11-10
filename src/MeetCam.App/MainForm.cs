using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MeetCam.Source;

namespace MeetCam;

internal sealed class MainForm : Form
{
    private readonly TextBox _path = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly Button _browse = new() { Text = "찾기", AutoSize = true };
    private readonly Button _start = new() { Text = "송출 시작", AutoSize = true };
    private readonly Button _stop = new() { Text = "중지", AutoSize = true, Enabled = false };
    private readonly PictureBox _preview = new()
    {
        BackColor = Color.Black,
        SizeMode = PictureBoxSizeMode.Zoom,
        Dock = DockStyle.Fill,
    };
    private readonly Label _status = new() { AutoSize = true, Text = "대기 중" };
    private readonly Label _detail = new() { AutoSize = true, ForeColor = Color.FromArgb(70, 78, 86) };
    private readonly VideoPump _pump = new();
    private readonly CameraHost _camera = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly byte[] _previewBuffer = new byte[FramePipe.PixelBytes];
    private bool _running;
    private bool _reportedError;

    public MainForm(string? initialVideo)
    {
        Text = "MeetCam";
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(246, 247, 249);
        MinimumSize = new Size(680, 620);
        ClientSize = new Size(760, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var title = new Label
        {
            Text = "MeetCam",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 18),
        };
        var hint = new Label
        {
            Text = "녹화해 둔 영상을 Google Meet 카메라로 보냅니다. 영상이 끝나면 처음부터 다시 재생합니다.",
            AutoSize = false,
            Size = new Size(700, 24),
            Location = new Point(26, 56),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        var fileLabel = new Label { Text = "영상 파일", AutoSize = true, Location = new Point(26, 96) };
        _path.Location = new Point(26, 120);
        _path.Width = 600;
        _path.PlaceholderText = "mp4, mkv, avi, mov, webm";
        _browse.Location = new Point(636, 118);
        _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browse.Click += (_, _) => Browse();

        var previewHost = new Panel
        {
            Location = new Point(26, 164),
            Size = new Size(708, 360),
            BackColor = Color.Black,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
        };
        previewHost.Controls.Add(_preview);

        _start.Location = new Point(26, 540);
        _start.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _start.FlatStyle = FlatStyle.Flat;
        _start.FlatAppearance.BorderSize = 0;
        _start.BackColor = Color.FromArgb(24, 110, 78);
        _start.ForeColor = Color.White;
        _start.Padding = new Padding(12, 6, 12, 6);
        _start.Click += (_, _) => StartPlayback();

        _stop.Location = new Point(150, 540);
        _stop.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _stop.FlatStyle = FlatStyle.Flat;
        _stop.Padding = new Padding(12, 6, 12, 6);
        _stop.Click += (_, _) => StopPlayback();

        _status.Location = new Point(26, 592);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _detail.Location = new Point(26, 616);
        _detail.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _detail.Text = "Meet 카메라 목록에서 MeetCam을 고르세요. Firefox도 같은 이름입니다. 앱을 켜 둔 동안 영상이 반복됩니다.";

        Controls.AddRange([title, hint, fileLabel, _path, _browse, previewHost, _start, _stop, _status, _detail]);

        _path.Text = initialVideo ?? LoadLastVideo();
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _path.Text = files[0];
        };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        FormClosing += (_, _) => StopPlayback();
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "영상 파일|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.m4v|모든 파일|*.*",
            Title = "송출할 영상 선택",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _path.Text = dialog.FileName;
    }

    private void StartPlayback()
    {
        if (_running)
            return;

        var path = _path.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "영상 파일을 찾을 수 없습니다.", "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var mediaFoundationReady = Registrar.IsRegistered();
        if ((!mediaFoundationReady || !Registrar.IsDirectShowRegistered()) && !AskToRegister())
        {
            if (!mediaFoundationReady)
                return;
        }

        try
        {
            _camera.Start();
            _pump.Start(path);
            _running = true;
            _reportedError = false;
            SaveLastVideo(path);
            _start.Enabled = false;
            _stop.Enabled = true;
            _status.Text = "송출 중";
        }
        catch (Exception ex)
        {
            _pump.Stop();
            _camera.Stop();
            _running = false;
            MessageBox.Show(this, "송출을 시작하지 못했습니다.\n\n" + ex.Message, "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool AskToRegister()
    {
        var answer = MessageBox.Show(
            this,
            "Chrome과 Firefox에 보이려면 MeetCam 카메라를 한 번 등록해야 합니다.\n관리자 권한 요청이 뜨면 허용해 주세요.",
            "MeetCam",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (answer != DialogResult.OK)
            return false;
        if (Registrar.TryElevateAndRegister())
            return true;

        MessageBox.Show(this, "카메라 등록이 끝나지 않았습니다.", "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void StopPlayback()
    {
        if (!_running && _pump.FramesPublished == 0)
        {
            _camera.Stop();
            return;
        }

        _running = false;
        _pump.Stop();
        _camera.Stop();
        _start.Enabled = true;
        _stop.Enabled = false;
        _status.Text = "중지됨";
    }

    private void RefreshStatus()
    {
        if (_pump.Error is { } error && !_reportedError)
        {
            _reportedError = true;
            StopPlayback();
            MessageBox.Show(this, error.Message, "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!_running)
            return;

        var loops = _pump.LoopCount;
        var length = _pump.DurationSeconds > 0 ? $"{_pump.DurationSeconds:0.#}초" : "길이 알 수 없음";
        _status.Text = loops == 0
            ? $"송출 중  ·  {length}  ·  {_pump.SourceFps:0.#}fps"
            : $"반복 재생 중  ·  {loops}회  ·  {length}";

        if (!_pump.TryCopyPreview(_previewBuffer))
            return;

        var pin = GCHandle.Alloc(_previewBuffer, GCHandleType.Pinned);
        try
        {
            using var wrapped = new Bitmap(
                Shared.FrameWidth,
                Shared.FrameHeight,
                Shared.FrameWidth * 4,
                PixelFormat.Format32bppArgb,
                pin.AddrOfPinnedObject());
            var copy = new Bitmap(wrapped);
            var previous = _preview.Image;
            _preview.Image = copy;
            previous?.Dispose();
        }
        finally
        {
            pin.Free();
        }
    }

    private static string LoadLastVideo()
    {
        try
        {
            var path = SettingsFile();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void SaveLastVideo(string video)
    {
        try
        {
            var path = SettingsFile();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, video);
        }
        catch (IOException)
        {
        }
    }

    private static string SettingsFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetCam", "last-video.txt");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _pump.Dispose();
            _camera.Dispose();
        }
        base.Dispose(disposing);
    }
}
