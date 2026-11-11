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
    private readonly FieldShell _planField = new();
    private readonly TextBox _planLink = new() { BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly DateTimePicker _planWhen = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm" };
    private readonly BounceButton _planButton = new() { Text = "예약", Fill = Color.FromArgb(255, 214, 196), Fore = Color.FromArgb(92, 42, 28) };
    private readonly FieldShell _accountField = new();
    private readonly TextBox _account = new() { BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly FieldShell _secretField = new();
    private readonly TextBox _secret = new() { BorderStyle = BorderStyle.None, BackColor = Color.White, UseSystemPasswordChar = true };
    private readonly System.Windows.Forms.Timer _planTimer = new() { Interval = 1000 };
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
    private MeetingPlan? _plan;
    private CancellationTokenSource? _joinCancel;
    private bool _joinRunning;
    private bool _loginLoaded;

    public MainForm(string? initialVideo)
    {
        Text = "MeetCam";
        Icon = AppIcon;
        Font = new Font("Segoe UI", 10f);
        BackColor = Paper;
        ForeColor = Color.FromArgb(42, 33, 28);
        MinimumSize = new Size(680, 612);
        ClientSize = new Size(760, 652);
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
        _planLink.Font = new Font("Segoe UI", 11f);
        _planLink.AutoSize = false;
        _planLink.PlaceholderText = "meet.google.com/abc-defg-hij";
        _account.Font = new Font("Segoe UI", 11f);
        _account.AutoSize = false;
        _account.PlaceholderText = "다른 Google 계정";
        _secret.Font = new Font("Segoe UI", 11f);
        _secret.AutoSize = false;
        _secret.PlaceholderText = "비밀번호";
        _planWhen.Font = new Font("Segoe UI", 10f);
        _planWhen.Value = DateTime.Now.AddHours(1);

        _field.Controls.Add(_path);
        _planField.Controls.Add(_planLink);
        _accountField.Controls.Add(_account);
        _secretField.Controls.Add(_secret);
        Controls.AddRange([_preview, _mark, _title, _subtitle, _field, _browse, _planField, _planWhen, _planButton, _accountField, _secretField, _start, _stop, _status]);

        _browse.Click += (_, _) => Browse();
        _start.Click += (_, _) => StartPlayback();
        _stop.Click += (_, _) => _ = StopPlaybackAsync();
        SetVideoPath(initialVideo ?? LoadLastVideo());
        if (_path.Text.Length > 0 && !File.Exists(_path.Text))
            _status.Text = "이전에 골랐던 영상을 찾지 못했습니다.";
        LoadPlan();
        LoadLogin();
        _account.Leave += (_, _) => PersistLogin();
        _secret.Leave += (_, _) => PersistLogin();
        _pathTip.SetToolTip(_account, "회의에 들어갈 계정입니다. 회의를 만든 계정은 피하세요.");
        _pathTip.SetToolTip(_secret, "이 Windows 사용자 자격 증명에 저장됩니다.");
        _planButton.Click += (_, _) => OnPlanButton();
        _planTimer.Tick += (_, _) => OnPlanTick();
        _planTimer.Start();

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

            var planTop = _field.Bottom + 10;
            const int whenWidth = 196;
            const int planButtonWidth = 96;
            var planFieldWidth = Math.Max(120, width - 40 - whenWidth - planButtonWidth - 16);
            _planField.SetBounds(20, planTop, planFieldWidth, fieldHeight);
            _planLink.SetBounds(16, pathTop, Math.Max(40, _planField.Width - 32), pathHeight);
            _planWhen.SetBounds(_planField.Right + 8, planTop + Math.Max(0, (fieldHeight - 28) / 2), whenWidth, Math.Max(28, fieldHeight - 8));
            _planButton.SetBounds(_planWhen.Right + 8, planTop - 2, planButtonWidth, fieldHeight + 4);

            var accountTop = _planField.Bottom + 10;
            const int secretWidth = 220;
            var accountWidth = Math.Max(160, width - 40 - secretWidth - 8);
            _accountField.SetBounds(20, accountTop, accountWidth, fieldHeight);
            _account.SetBounds(16, pathTop, Math.Max(40, _accountField.Width - 32), pathHeight);
            _secretField.SetBounds(_accountField.Right + 8, accountTop, secretWidth, fieldHeight);
            _secret.SetBounds(16, pathTop, Math.Max(40, _secretField.Width - 32), pathHeight);

            const int buttonHeight = 58;
            var buttonsTop = height - 72;
            var previewTop = _secretField.Bottom + 12;
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
            _planButton.BringToFront();
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
        if (e.KeyCode == Keys.Escape && (_running || _busy))
        {
            _ = StopPlaybackAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyCode != Keys.Enter)
            return;
        if (_account.Focused || _secret.Focused || _planLink.Focused || _planWhen.Focused)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        StartPlayback();
        e.Handled = true;
        e.SuppressKeyPress = true;
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

    private void LoadPlan()
    {
        _plan = MeetingPlan.TryLoad();
        if (_plan == null)
            return;
        if (!string.IsNullOrWhiteSpace(_plan.Link))
            _planLink.Text = _plan.Link;
        if (_plan.LocalStart.Year >= 2000)
            _planWhen.Value = _plan.LocalStart;
        if (!_plan.Armed)
            return;
        _planButton.Text = "취소";
        ShowArmedStatus();
    }

    private void OnPlanButton()
    {
        if (_plan?.Armed == true || _joinRunning)
        {
            CancelPlan();
            return;
        }
        ArmPlan();
    }

    private void ArmPlan()
    {
        if (!MeetingPlan.TryNormalize(_planLink.Text, out var link))
        {
            _status.Text = "Meet 링크를 입력해 주세요.";
            return;
        }

        var when = _planWhen.Value;
        if (when < DateTime.Now.AddMinutes(-1))
        {
            _status.Text = "지난 시간은 예약할 수 없습니다.";
            return;
        }

        _plan = new MeetingPlan { Link = link, LocalStart = when, Armed = true };
        _plan.Save();
        _planLink.Text = link;
        _planButton.Text = "취소";
        if (!PersistLogin())
            _status.Text = "예약했습니다. 계정 정보는 저장하지 못했습니다.";
        else
            ShowArmedStatus();
    }

    private void CancelPlan()
    {
        _joinCancel?.Cancel();
        if (_plan != null)
        {
            _plan.Armed = false;
            _plan.Save();
        }
        _planButton.Text = "예약";
        if (!_running)
            _status.Text = "예약을 취소했습니다.";
    }

    private void LoadLogin()
    {
        if (GoogleLogin.TryLoad(out var email, out var password))
        {
            _account.Text = email;
            _secret.Text = password;
        }
        _loginLoaded = true;
    }

    private bool PersistLogin()
    {
        if (!_loginLoaded)
            return true;
        return GoogleLogin.Save(_account.Text.Trim(), _secret.Text);
    }

    private void ShowArmedStatus()
    {
        if (_plan == null || !_plan.Armed || _running || _joinRunning)
            return;
        var path = _path.Text.Trim().Trim('"');
        _status.Text = File.Exists(path)
            ? $"{_plan.LocalStart:M월 d일 H:mm}에 회의에 들어갑니다"
            : "예약됨. 영상 파일을 찾지 못했습니다.";
    }

    private async void OnPlanTick()
    {
        try
        {
            if (_joinRunning || _plan == null || !_plan.Armed || IsDisposed)
                return;
            var now = DateTime.Now;
            if (now < _plan.LocalStart)
                return;
            if (now > _plan.LocalStart.AddMinutes(15))
            {
                _plan.Armed = false;
                _plan.Save();
                _planButton.Text = "예약";
                if (!_running)
                    _status.Text = "예약 시간이 지났습니다.";
                return;
            }

            var path = _path.Text.Trim().Trim('"');
            if (!File.Exists(path) || Directory.Exists(path))
            {
                _status.Text = "영상 파일을 찾을 수 없습니다.";
                return;
            }

            _joinRunning = true;
            _joinCancel = new CancellationTokenSource();
            var token = _joinCancel.Token;
            var link = _plan.Link;
            var account = _account.Text.Trim();
            var password = _secret.Text;
            PersistLogin();
            _status.Text = "회의에 들어가는 중";
            try
            {
                var joiner = new MeetJoiner();
                await joiner.RunAsync(link, account, password, OnMeetSignal, token);
            }
            catch (OperationCanceledException)
            {
                if (!_running && !IsDisposed)
                    _status.Text = "예약을 취소했습니다.";
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    _status.Text = "회의에 들어가지 못했습니다.";
                    MessageBox.Show(this, ex.Message, "MeetCam", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _joinRunning = false;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _joinRunning = false;
            if (!IsDisposed)
                _status.Text = "회의에 들어가지 못했습니다.";
        }
    }

    private Task<bool> OnMeetSignal(MeetSignal signal)
    {
        if (IsDisposed)
            return Task.FromResult(false);
        if (!InvokeRequired)
            return Task.FromResult(HandleMeetSignal(signal));

        var done = new TaskCompletionSource<bool>();
        BeginInvoke(() =>
        {
            try
            {
                done.SetResult(HandleMeetSignal(signal));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private bool HandleMeetSignal(MeetSignal signal)
    {
        switch (signal)
        {
            case MeetSignal.NeedLogin:
                _status.Text = "Chrome 창에서 Google 계정으로 로그인해 주세요.";
                return true;
            case MeetSignal.SigningIn:
                _status.Text = "Google 계정으로 로그인하는 중";
                return true;
            case MeetSignal.NeedVerification:
                _status.Text = "휴대폰 인증을 기다리는 중";
                return true;
            case MeetSignal.LoginRejected:
                _status.Text = "비밀번호가 맞지 않습니다.";
                return true;
            case MeetSignal.LoginBlocked:
                _status.Text = "Chrome 창에서 직접 로그인해 주세요.";
                return true;
            case MeetSignal.Asked:
                _status.Text = "참여를 요청했습니다.";
                return true;
            case MeetSignal.WaitingApproval:
                _status.Text = "승인을 기다리는 중";
                return true;
            case MeetSignal.Admitted:
                _status.Text = "승인되었습니다. 카메라를 켭니다";
                if (!_running)
                    StartPlayback();
                return _running;
            case MeetSignal.CameraOn:
                _status.Text = "회의 카메라를 켰습니다.";
                DisarmPlan();
                return true;
            case MeetSignal.Denied:
                _status.Text = "참여가 거부되었습니다.";
                DisarmPlan();
                return true;
            case MeetSignal.Invalid:
                _status.Text = "Meet 링크를 확인하지 못했습니다.";
                DisarmPlan();
                return true;
            default:
                if (_running)
                    _status.Text = "회의에서 카메라 버튼을 찾지 못했습니다.";
                else
                    _status.Text = "회의에 들어가지 못했습니다.";
                DisarmPlan();
                return false;
        }
    }

    private void DisarmPlan()
    {
        if (_plan != null)
        {
            _plan.Armed = false;
            _plan.Save();
        }
        _planButton.Text = "예약";
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
        PersistLogin();
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

    private static readonly Icon AppIcon = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("MeetCam.AppIcon.ico");
        return stream == null ? SystemIcons.Application : new Icon(stream);
    }

    private const int EM_SETSEL = 0x00B1;
    private const int EM_SCROLLCARET = 0x00B7;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _planTimer.Dispose();
            _joinCancel?.Cancel();
            _joinCancel?.Dispose();
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
