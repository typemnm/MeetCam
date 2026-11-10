using System.Drawing.Drawing2D;

namespace MeetCam;

internal sealed class FieldShell : Control
{
    private readonly Spring _glow = new() { Value = 0f, Target = 0f, Stiffness = 320f, Damping = 16f };

    public FieldShell()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        AllowDrop = true;
    }

    public void Tick(float dt)
    {
        var before = _glow.Value;
        _glow.Tick(dt);
        if (MathF.Abs(_glow.Value - before) > 0.001f)
            Invalidate();
    }

    public void SetHot(bool hot)
    {
        _glow.Target = hot ? 1f : 0f;
        if (hot)
            _glow.Velocity = 4f;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(255, 246, 241));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Rounded(rect, 16f);
        using var fill = new SolidBrush(Color.White);
        g.FillPath(fill, path);
        var coral = Color.FromArgb(255, 90, 54);
        var quiet = Color.FromArgb(236, 214, 204);
        var mix = Lerp(quiet, coral, Math.Clamp(_glow.Value, 0f, 1f));
        using var pen = new Pen(mix, 1.6f + (_glow.Value * 1.2f));
        g.DrawPath(pen, path);
    }

    private static Color Lerp(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            (int)(from.R + ((to.R - from.R) * amount)),
            (int)(from.G + ((to.G - from.G) * amount)),
            (int)(from.B + ((to.B - from.B) * amount)));
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
