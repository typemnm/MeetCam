using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MeetCam;

internal sealed class PreviewCard : Control
{
    private readonly Spring _scale = new() { Value = 0.9f, Target = 1f, Velocity = 3.5f, Stiffness = 260f, Damping = 12f };
    private readonly Spring _badge = new() { Value = 0f, Target = 0f, Stiffness = 340f, Damping = 13f };
    private Bitmap? _frame;
    private float _time;
    private bool _live;
    private bool _drag;

    public PreviewCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AllowDrop = true;
    }

    public void Tick(float dt)
    {
        _time += dt;
        var scaleBefore = _scale.Value;
        var badgeBefore = _badge.Value;
        _scale.Tick(dt);
        _badge.Tick(dt);
        if (_live || _drag || _frame == null || MathF.Abs(_scale.Value - scaleBefore) > 0.001f || MathF.Abs(_badge.Value - badgeBefore) > 0.001f)
            Invalidate();
    }

    public void ShowFrame(Bitmap frame)
    {
        var previous = _frame;
        _frame = frame;
        previous?.Dispose();
        Invalidate();
    }

    public void ClearFrame()
    {
        _frame?.Dispose();
        _frame = null;
        Invalidate();
    }

    public void SetLive(bool live)
    {
        _live = live;
        _badge.Kick(live ? 0.5f : _badge.Value, live ? 7f : 0f, live ? 1f : 0f);
        Invalidate();
    }

    public void SetDrag(bool drag)
    {
        if (_drag == drag)
            return;
        _drag = drag;
        if (drag)
            _scale.Kick(0.95f, 3.4f, 1.04f);
        else
            _scale.Target = 1f;
        Invalidate();
    }

    public void Pop() => _scale.Kick(0.88f, 4.6f, 1f);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(255, 246, 241));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var scale = Math.Clamp(_scale.Value, 0.84f, 1.08f);
        var cx = Width / 2f;
        var cy = Height / 2f;
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-cx, -cy);

        var card = new RectangleF(10, 8, Width - 20, Height - 18);
        using var shadow = new SolidBrush(Color.FromArgb(22, 80, 36, 20));
        g.FillEllipse(shadow, card.X + 24, card.Bottom - 8, card.Width - 48, 14);
        using var path = Rounded(card, 28f);
        using var fill = new SolidBrush(_drag ? Color.FromArgb(255, 236, 226) : Color.FromArgb(34, 28, 31));
        g.FillPath(fill, path);

        var state = g.Save();
        g.SetClip(path);
        if (_frame != null)
            DrawFrame(g, card);
        else
            DrawEmpty(g, card);
        g.Restore(state);

        using var border = new Pen(_drag ? Color.FromArgb(255, 90, 54) : Color.FromArgb(255, 214, 204), _drag ? 3f : 1.5f);
        g.DrawPath(border, path);
        if (_badge.Value > 0.04f)
            DrawBadge(g, card);
    }

    private void DrawFrame(Graphics g, RectangleF card)
    {
        const float aspect = 16f / 9f;
        var width = card.Width;
        var height = width / aspect;
        if (height > card.Height)
        {
            height = card.Height;
            width = height * aspect;
        }
        var dest = new RectangleF(card.X + ((card.Width - width) / 2f), card.Y + ((card.Height - height) / 2f), width, height);
        g.DrawImage(_frame!, dest);
    }

    private void DrawEmpty(Graphics g, RectangleF card)
    {
        var bob = MathF.Sin(_time * 2.4f) * 6f;
        using var hint = new SolidBrush(Color.FromArgb(196, 214, 206, 200));
        using var font = new Font("Segoe UI", 13f, FontStyle.Bold);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var text = _drag ? "놓을 때 통통 들어와요" : "영상을 여기로 끌어 놓으세요";
        g.DrawString(text, font, hint, new RectangleF(card.X, card.Y + bob, card.Width, card.Height), format);
    }

    private void DrawBadge(Graphics g, RectangleF card)
    {
        var pop = Math.Clamp(_badge.Value, 0.2f, 1.12f);
        var pulse = _live ? 0.4f + (0.6f * (0.5f + (0.5f * MathF.Sin(_time * 5f)))) : 1f;
        var state = g.Save();
        var originX = card.X + 64f;
        var originY = card.Y + 32f;
        g.TranslateTransform(originX, originY);
        g.ScaleTransform(pop, pop);
        g.TranslateTransform(-originX, -originY);
        var pill = new RectangleF(card.X + 18, card.Y + 16, 92, 32);
        using var path = Rounded(pill, 16f);
        using var fill = new SolidBrush(Color.FromArgb(236, 255, 255, 255));
        g.FillPath(fill, path);
        using var dot = new SolidBrush(Color.FromArgb((int)(255 * pulse), 255, 70, 54));
        g.FillEllipse(dot, pill.X + 12, pill.Y + 11, 10, 10);
        using var textBrush = new SolidBrush(Color.FromArgb(48, 32, 28));
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        g.DrawString("LIVE", font, textBrush, pill.X + 28, pill.Y + 7);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _frame?.Dispose();
        base.Dispose(disposing);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
