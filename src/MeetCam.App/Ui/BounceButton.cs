using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MeetCam;

internal sealed class BounceButton : Control
{
    private readonly Spring _scale = new() { Value = 0.72f, Target = 1f, Velocity = 5f, Stiffness = 320f, Damping = 11f };
    private bool _pressed;

    public BounceButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(168, 58);
        Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        Fill = Color.FromArgb(255, 90, 54);
        Fore = Color.White;
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fore { get; set; }

    public void Tick(float dt)
    {
        var before = _scale.Value;
        _scale.Target = !Enabled ? 1f : _pressed ? 0.92f : ClientRectangle.Contains(PointToClient(MousePosition)) ? 1.07f : 1f;
        _scale.Tick(dt);
        if (!Enabled)
            return;
        if (MathF.Abs(_scale.Value - before) > 0.001f || !_scale.Resting)
            Invalidate();
    }

    public void PopIn() => _scale.Kick(0.7f, 6.5f, 1f);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
            return;
        _pressed = true;
        Capture = true;
        _scale.Kick(0.9f, -1.4f, 0.92f);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed)
            return;
        _pressed = false;
        Capture = false;
        var inside = ClientRectangle.Contains(e.Location);
        _scale.Target = inside ? 1.07f : 1f;
        _scale.Velocity = 7f;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_pressed)
            _scale.Target = 1f;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(255, 246, 241));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var cx = Width / 2f;
        var cy = Height / 2f;
        var scale = Enabled ? Math.Clamp(_scale.Value, 0.7f, 1.14f) : 1f;
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-cx, -cy);

        var rect = new RectangleF(8, 9, Width - 16, Height - 18);
        using var path = Rounded(rect, rect.Height / 2f);
        using var shadow = new SolidBrush(Color.FromArgb(Enabled ? 28 : 12, 70, 32, 18));
        g.FillEllipse(shadow, rect.X + 10, rect.Bottom - 6, rect.Width - 20, 10);
        using var fill = new SolidBrush(Enabled ? Fill : Color.FromArgb(214, 206, 200));
        g.FillPath(fill, path);
        if (Focused && Enabled)
        {
            using var ring = new Pen(Color.FromArgb(180, 255, 255, 255), 2f);
            g.DrawPath(ring, path);
        }

        using var text = new SolidBrush(Enabled ? Fore : Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, text, rect, format);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled || e.KeyCode is not (Keys.Enter or Keys.Space))
            return;
        _scale.Kick(0.9f, 5f, 1f);
        OnClick(EventArgs.Empty);
        e.Handled = true;
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
