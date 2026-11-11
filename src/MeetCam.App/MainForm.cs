using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MeetCam.Source;

namespace MeetCam;

internal sealed class MainForm : Form
{
    private static readonly Color Paper = Color.FromArgb(255, 246, 241);

    private readonly MarkBadge _mark = new();
    private readonly Label _title = new() { AutoSize = true, Text = "MeetCam", ForeColor = Color.FromArgb(42, 33, 28) };
    private readonly Label _subtitle = new()
    {
        AutoSize = false,
        ForeColor = Color.FromArgb(122, 96, 86),
        Text = "녹화해 둔 영상을 카메라로 보냅니다. 끝나면 처음부터 다시 재생됩니다.",
    };
    private readonly FieldShell _field = new();
    private readonly TextBox _path = new() { BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly BounceButton _browse = new() { Text = "찾기", Fill = Color.FromArgb(255, 214, 196), Fore = Color.FromArgb(92, 42, 28) };
    private readonly PreviewCard _preview = new();
    private readonly BounceButton _start = new() { Text = "송출 시작" };
    private readonly BounceButton _stop = new() { Text = "중지", Fill = Color.FromArgb(255, 244, 238), Fore = Color.FromArgb(92, 42, 28), Enabled = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(92, 64, 56), Text = "대기 중" };
    private readonly ToolTip _pathTip = new();
    private readonly VideoPump _pump = new();
    private readonly CameraHost _camera = new();
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _frames = new() { Interval = 80 };
    private readonly byte[] _previewBuffer = new byte[FramePipe.PixelBytes];
    private Bitmap? _previewBitmap;
    private bool _running;
    private bool _busy;
    private bool _reportedError;
    private bool _closeAfterStop;
    private bool _layingOut;
    private int _motionClock;

    public MainForm(string? initialVideo)
    {
        Text = "MeetCam";
        Font = new Font("Segoe UI", 10f);
        BackColor = Paper;
        ForeColor = Color.FromArgb(42, 33, 28);
        MinimumSize = new Size(680, 460);
        ClientSize = new Size(760, 520);
        StartPosition = FormStartPosition.Manual;

        AllowDrop = true;
        KeyPreview = true;
        DoubleBuffered = true;

        _title.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        _subtitle.Font = new Font("Segoe UI", 10f);
        _path.Font = new Font("Segoe UI", 11f);
        _path.AutoSize = false;
        _path.PlaceholderText = "mp4, mkv, avi, mov, webm";
        _status.Font = new Font("Segoe UI", 10f);
        _status.AutoSize = false;
        _status.AutoEllipsis = true;

        _field.Controls.Add(_path);
        Controls.AddRange([_preview, _mark, _title, _subtitle, _field, _browse, _start, _stop, _status]);

        _browse.Click += (_, _) => Browse();
        _start.Click += (_, _) => StartPlayback();
        _stop.Click += (_, _) => _ = StopPlaybackAsync();
        SetVideoPath(initialVideo ?? LoadLastVideo());
        if (_path.Text.Length > 0 && !File.Exists(_path.Text))
            _status.Text = "이전에 골랐던 영상을 찾지 못했습니다.";

        DragEnter += (_, e) => OnDragEntered(e);
        DragDrop += (_, e) => AcceptDrop(e);
        DragLeave += (_, _) => ClearDrag();
        _preview.DragEnter += (_, e) => OnDragEntered(e);
        _preview.DragDrop += (_, e) => AcceptDrop(e);
        _preview.DragLeave += (_, _) => ClearDrag();
        _field.DragEnter += (_, e) => OnDragEntered(e);
        _field.DragDrop += (_, e) => AcceptDrop(e);
        KeyDown += OnFormKeyDown;
        _motion.Tick += (_, _) => StepMotion();
        _frames.Tick += (_, _) => RefreshPreview();
        _motion.Start();
        _frames.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.FromControl(this).WorkingArea;
        var width = Math.Min(Width, Math.Max(MinimumSize.Width, area.Width - 32));
        var height = Math.Min(Height, Math.Max(MinimumSize.Height, area.Height - 32));
        Bounds = new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
        LayoutContent();
        RevealPathEnd();
        BeginInvoke(RevealPathEnd);
        _motionClock = 0;
        _browse.PopIn();
        _start.PopIn();
        _preview.Pop();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutContent();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Paper);
    }

    private void LayoutContent()
    {
        if (_layingOut || ClientSize.Width < 100 || ClientSize.Height < 100)
            return;
        _layingOut = true;
        try
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            var textLeft = 104;
            var textWidth = Math.Max(80, width - textLeft - 24);
            _mark.SetBounds(20, 8, 76, 84);
            _title.Location = new Point(textLeft, 12);
            var titleHeight = Math.Max(_title.Height, TextRenderer.MeasureText(_title.Text, _title.Font).Height);
            var subtitleHeight = WrappedTextHeight(_subtitle.Text, _subtitle.Font, textWidth);
            _subtitle.SetBounds(textLeft, 12 + titleHeight + 8, textWidth, subtitleHeight);

            var fieldTop = Math.Max(_mark.Bottom, _subtitle.Bottom) + 14;
            var pathHeight = PathBoxHeight();
            var fieldHeight = pathHeight + 20;
            var fieldWidth = Math.Max(120, width - 156);
            _field.SetBounds(20, fieldTop, fieldWidth, fieldHeight);
            _browse.SetBounds(_field.Right + 12, fieldTop - 2, 104, fieldHeight + 4);
            var pathTop = Math.Max(2, (fieldHeight - pathHeight) / 2);
            _path.SetBounds(16, pathTop, Math.Max(40, _field.Width - 32), pathHeight);

            const int buttonHeight = 58;
            var buttonsTop = height - 72;
            var previewTop = _field.Bottom + 12;
            var previewHeight = Math.Max(80, buttonsTop - previewTop - 8);
            _preview.SetBounds(12, previewTop, Math.Max(80, width - 24), previewHeight);
            _start.SetBounds(20, buttonsTop, 168, buttonHeight);
            _stop.SetBounds(200, buttonsTop, 116, buttonHeight);
            var statusHeight = _status.Font.Height + 8;
            var statusTop = buttonsTop + Math.Max(0, (buttonHeight - statusHeight) / 2);
            _status.SetBounds(332, statusTop, Math.Max(40, width - 352), statusHeight);
            _start.BringToFront();
            _stop.BringToFront();
            _browse.BringToFront();
            if (!_path.Focused)
                RevealPathEnd();
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void StepMotion()
    {
        const float dt = 0.016f;
        _motionClock++;
        _mark.Tick(dt);
        _field.Tick(dt);
        _browse.Tick(dt);
        _preview.Tick(dt);
        _start.Tick(dt);
        _stop.Tick(dt);
        if (_motionClock == 8)
            _stop.PopIn();
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !_path.Focused)
        {
            StartPlayback();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _path.Focused)
        {
            StartPlayback();
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && (_running || _busy))
        {
            _ = StopPlaybackAsync();
            e.Handled = true;
        }
    }

    private void Browse()
    {
        if (_running || _busy)
            return;
        using var dialog = new OpenFileDialog
        {
            Filter = "영상 파일|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.m4v|모든 파일|*.*",
            Title = "송출할 영상 선택",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            UseVideo(dialog.FileName);
    }

    private void OnDragEntered(DragEventArgs e)
    {
        var ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !_running && !_busy;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok)
        {
            _field.SetHot(true);
            _preview.SetDrag(true);
        }
    }

    private void AcceptDrop(DragEventArgs e)
    {
        ClearDrag();
        if (_running || _busy)
            return;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            UseVideo(files[0]);
    }

    private void ClearDrag()
    {
        _field.SetHot(false);
        _preview.SetDrag(false);
    }

    private void UseVideo(string path)
    {
        if (Directory.Exists(path) || !File.Exists(path))
        {
            _status.Text = "영상 파일이 아닙니다.";
            return;
        }
        SetVideoPath(path);
        _preview.Pop();
        _status.Text = "준비됨  ·  Enter 또는 송출 시작";
    }

    private void SetVideoPath(string path)
    {
        _path.Text = path;
        var tip = path.Length == 0 ? null : path;
        _pathTip.SetToolTip(_path, tip);
        _pathTip.SetToolTip(_field, tip);
        RevealPathEnd();
    }

    // The saved path is wider than the field. Keep the file name in view.
    private void RevealPathEnd()
    {
        if (!_path.IsHandleCreated || _path.TextLength == 0)
            return;
        var end = (IntPtr)_path.TextLength;
        SendMessage(_path.Handle, EM_SETSEL, end, end);
        SendMessage(_path.Handle, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
    }

    private int PathBoxHeight()
    {
        var fromFont = _path.Font.Height + 6;
        if (!_path.IsHandleCreated)
            return fromFont;
        return Math.Max(_path.PreferredHeight + 2, fromFont);
    }

    private static int WrappedTextHeight(string text, Font font, int width)
    {
        var measured = TextRenderer.MeasureText(
            text,
            font,
            new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        return Math.Max(font.Height + 6, measured.Height + 6);
    }

    private void StartPlayback()
    {
        if (_running || _busy)
            return;

        var path = _path.Text.Trim().Trim('"');
        if (!File.Exists(path) || Directory.Exists(path))
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
            _busy = true;
            UpdateButtons();
            _status.Text = "카메라를 준비하는 중";
            _camera.Start();
            _pump.Start(path);
            _running = true;
            _busy = false;
            _reportedError = false;
            SaveLastVideo(path);
            _preview.Pop();
            _preview.SetLive(true);
            _status.Text = "영상을 여는 중";
            UpdateButtons();
        }
        catch (Exception ex)
        {
            _pump.Stop();
            _camera.Stop();
            _running = false;
            _busy = false;
            _preview.SetLive(false);
            UpdateButtons();
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

    private async Task StopPlaybackAsync()
    {
        if (_busy || (!_running && _pump.FramesPublished == 0))
        {
            if (!_running)
                _camera.Stop();
            return;
        }

        _busy = true;
        _running = false;
        UpdateButtons();
        _status.Text = "멈추는 중";
        _preview.SetLive(false);
        try
        {
            await Task.Run(_pump.Stop);
            _camera.Stop();
            ReleasePreviewBitmap();
            _status.Text = "중지됨";
        }
        catch (Exception ex)
        {
            _status.Text = "중지에 실패했습니다.";
            MessageBox.Show(this, ex.Message, "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
            if (_closeAfterStop && !IsDisposed)
                Close();
        }
    }

    private void UpdateButtons()
    {
        _start.Enabled = !_running && !_busy;
        _stop.Enabled = _running && !_busy;
        _browse.Enabled = !_running && !_busy;
        _path.Enabled = !_running && !_busy;
    }

    private void RefreshPreview()
    {
        if (_pump.Error is { } error && !_reportedError)
        {
            _reportedError = true;
            _ = StopPlaybackAsync();
            MessageBox.Show(this, error.Message, "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!_running)
            return;

        if (_pump.FramesPublished > 0)
        {
            var loops = _pump.LoopCount;
            var length = _pump.DurationSeconds > 0 ? $"{_pump.DurationSeconds:0.#}초" : "길이 확인 중";
            _status.Text = loops == 0
                ? $"송출 중  ·  {length}  ·  {_pump.SourceFps:0.#}fps"
                : $"반복 재생 중  ·  {loops}회  ·  {length}";
        }

        if (!_pump.TryCopyPreview(_previewBuffer))
            return;
        if (IsDisposed)
            return;

        CopyPreviewBitmap();
        _preview.ShowFrame(_previewBitmap!);
    }

    private void CopyPreviewBitmap()
    {
        _previewBitmap ??= new Bitmap(Shared.FrameWidth, Shared.FrameHeight, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, Shared.FrameWidth, Shared.FrameHeight);
        var data = _previewBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var srcStride = Shared.FrameWidth * 4;
            if (data.Stride == srcStride)
            {
                Marshal.Copy(_previewBuffer, 0, data.Scan0, srcStride * Shared.FrameHeight);
            }
            else
            {
                for (var y = 0; y < Shared.FrameHeight; y++)
                    Marshal.Copy(_previewBuffer, y * srcStride, data.Scan0 + (y * data.Stride), srcStride);
            }
        }
        finally
        {
            _previewBitmap.UnlockBits(data);
        }
    }

    private void ReleasePreviewBitmap()
    {
        _preview.ClearFrame();
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if ((_running || _busy) && !_closeAfterStop)
        {
            e.Cancel = true;
            _closeAfterStop = true;
            _ = StopPlaybackAsync();
            return;
        }
        base.OnFormClosing(e);
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

    private const int EM_SETSEL = 0x00B1;
    private const int EM_SCROLLCARET = 0x00B7;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motion.Dispose();
            _frames.Dispose();
            _pathTip.Dispose();
            ReleasePreviewBitmap();
            _pump.Dispose();
            _camera.Dispose();
        }
        base.Dispose(disposing);
    }
}
