using System.Drawing.Drawing2D;

namespace MeetCam;

internal sealed class MarkBadge : Control
{
    private float _time;

    public MarkBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(76, 84);
    }

    public void Tick(float dt)
    {
        _time += dt;
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
        var bounce = MathF.Abs(MathF.Sin(_time * 2.7f));
        var lift = bounce * 9f;
        var squash = 1f - ((1f - bounce) * 0.1f);
        var cx = Width / 2f;
        var ground = Height - 14f;

        using var shadow = new SolidBrush(Color.FromArgb((int)(50 + (40 * (1f - bounce))), 90, 40, 24));
        var shadowWidth = 28f + ((1f - bounce) * 10f);
        g.FillEllipse(shadow, cx - (shadowWidth / 2f), ground - 3f, shadowWidth, 8f);

        g.TranslateTransform(cx, ground - 24f - lift);
        g.ScaleTransform(1f / squash, squash);
        g.TranslateTransform(-cx, -(ground - 24f - lift));

        var badge = new RectangleF(cx - 24f, ground - 48f - lift, 48f, 48f);
        using var fill = new SolidBrush(Color.FromArgb(255, 90, 54));
        g.FillEllipse(fill, badge);
        using var lens = new Pen(Color.White, 3.2f);
        g.DrawEllipse(lens, badge.X + 13f, badge.Y + 12f, 22f, 22f);
        using var dot = new SolidBrush(Color.FromArgb(64, 214, 168));
        g.FillEllipse(dot, badge.Right - 16f, badge.Y + 8f, 9f, 9f);
    }
}
