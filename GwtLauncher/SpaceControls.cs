using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace GwtLauncher;

internal interface IBackdropProvider
{
    Bitmap? GetComposedBackdrop();
    Form HostForm { get; }
    float AnimationTime { get; }
}

internal interface IAnimatable
{
    bool StepAnimation(float dt);
}

internal static class BackdropRenderer
{
    public static void PaintBackdrop(Control target, Graphics g, Color tint)
    {
        if (target.Parent is SpaceGlassPanel glass && glass.UseSolidBackground)
        {
            using var sb = new SolidBrush(Color.FromArgb(255, glass.GlassTint.R, glass.GlassTint.G, glass.GlassTint.B));
            g.FillRectangle(sb, target.ClientRectangle);
            return;
        }

        if (target.FindForm() is IBackdropProvider provider)
        {
            var frame = provider.GetComposedBackdrop();
            var form = provider.HostForm;
            if (frame != null && form.ClientSize.Width > 0 && form.ClientSize.Height > 0)
            {
                var pt = form.PointToClient(target.PointToScreen(Point.Empty));
                var srcRect = new Rectangle(pt.X, pt.Y, target.ClientSize.Width, target.ClientSize.Height);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(frame, target.ClientRectangle, srcRect, GraphicsUnit.Pixel);

                if (tint.A > 0)
                {
                    using var overlay = new SolidBrush(tint);
                    g.FillRectangle(overlay, target.ClientRectangle);
                }
                return;
            }
        }

        using var fallback = new SolidBrush(Color.FromArgb(255, SpaceTheme.BgWindow));
        g.FillRectangle(fallback, target.ClientRectangle);
    }

    public static Rectangle ComputeCoverRect(Size imageSize, Size boxSize, float zoom = 1.0f, float offsetX = 0f, float offsetY = 0f)
    {
        float imgAspect = (float)imageSize.Width / Math.Max(1, imageSize.Height);
        float boxAspect = (float)boxSize.Width / Math.Max(1, boxSize.Height);
        float baseW, baseH;
        if (boxAspect > imgAspect)
        {
            baseW = boxSize.Width;
            baseH = boxSize.Width / imgAspect;
        }
        else
        {
            baseH = boxSize.Height;
            baseW = boxSize.Height * imgAspect;
        }

        float w = baseW * zoom;
        float h = baseH * zoom;
        float x = (boxSize.Width - w) * 0.5f + offsetX;
        float y = (boxSize.Height - h) * 0.5f + offsetY;
        return new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Ceiling(w), (int)Math.Ceiling(h));
    }

    public static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static float Approach(float current, float target, float speed, float dt)
    {
        float factor = 1f - MathF.Exp(-speed * dt);
        float next = current + (target - current) * factor;
        return MathF.Abs(next - target) < 0.004f ? target : next;
    }
}

internal sealed class SpaceGlassPanel : Panel
{
    public int CornerRadius { get; set; } = 10;
    public Color GlassTint { get; set; } = Color.FromArgb(210, 15, 18, 26);
    public Color BorderColor { get; set; } = SpaceTheme.BorderSubtle;
    public bool FullRectNoRadius { get; set; }
    public bool DrawBottomSeparator { get; set; }
    public bool UseSolidBackground { get; set; }

    public SpaceGlassPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (UseSolidBackground)
        {
            using var sb = new SolidBrush(Color.FromArgb(255, GlassTint.R, GlassTint.G, GlassTint.B));
            e.Graphics.FillRectangle(sb, ClientRectangle);
            return;
        }
        if (FullRectNoRadius)
        {
            BackdropRenderer.PaintBackdrop(this, e.Graphics, GlassTint);
            return;
        }
        BackdropRenderer.PaintBackdrop(this, e.Graphics, Color.Transparent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (FullRectNoRadius)
        {
            if (DrawBottomSeparator)
            {
                using var p = new Pen(BorderColor, 1f);
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
            base.OnPaint(e);
            return;
        }

        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = SpaceTheme.CreateRoundedRect(rect, CornerRadius);
        using var fill = new SolidBrush(GlassTint);
        g.FillPath(fill, path);

        using var border = new Pen(BorderColor, 1f);
        g.DrawPath(border, path);
        base.OnPaint(e);
    }
}

internal sealed class SpaceSplitPlayButton : Control, IAnimatable
{
    private bool _hoveredMain;
    private bool _hoveredArrow;
    private bool _pressedMain;
    private bool _pressedArrow;

    private float _hoverMainT;
    private float _hoverArrowT;
    private float _pressT;
    private float _animTime;

    public const int ArrowZoneWidth = 40;

    public event EventHandler? PrimaryClick;
    public event EventHandler? DropdownClick;

    public Color GradientStart { get; set; } = SpaceTheme.AccentRedBright;
    public Color GradientEnd { get; set; } = SpaceTheme.AccentRedDark;
    public int CornerRadius { get; set; } = 8;
    public bool ShowDropdown { get; set; } = true;

    public SpaceSplitPlayButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(248, 46);
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        ForeColor = Color.White;
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? string.Empty; Invalidate(); }
    }

    public bool StepAnimation(float dt)
    {
        _animTime += dt;
        float nextMain = BackdropRenderer.Approach(_hoverMainT, _hoveredMain ? 1f : 0f, 14f, dt);
        float nextArrow = BackdropRenderer.Approach(_hoverArrowT, _hoveredArrow ? 1f : 0f, 14f, dt);
        float nextPress = BackdropRenderer.Approach(_pressT, (_pressedMain || _pressedArrow) ? 1f : 0f, 22f, dt);

        _hoverMainT = nextMain;
        _hoverArrowT = nextArrow;
        _pressT = nextPress;

        if (Visible)
        {
            Invalidate();
            return true;
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool inArrow = ShowDropdown && e.X >= Width - ArrowZoneWidth;
        _hoveredArrow = inArrow;
        _hoveredMain = !inArrow;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoveredMain = _hoveredArrow = _pressedMain = _pressedArrow = false;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (ShowDropdown && e.X >= Width - ArrowZoneWidth)
                _pressedArrow = true;
            else
                _pressedMain = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasArrow = _pressedArrow;
        bool wasMain = _pressedMain;
        _pressedArrow = false;
        _pressedMain = false;

        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            if (wasArrow && ShowDropdown && e.X >= Width - ArrowZoneWidth)
                DropdownClick?.Invoke(this, EventArgs.Empty);
            else if (wasMain && Enabled)
                PrimaryClick?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, Color.Transparent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int pressOffset = _pressT > 0.45f && Enabled ? 1 : 0;
        var rect = new Rectangle(2, 2 + pressOffset, Math.Max(1, Width - 5), Math.Max(1, Height - 5));

        Color baseC1 = Enabled ? GradientStart : Color.FromArgb(52, 58, 70);
        Color baseC2 = Enabled ? GradientEnd : Color.FromArgb(40, 45, 56);
        Color c1 = Enabled ? BackdropRenderer.LerpColor(baseC1, ControlPaint.Light(baseC1, 0.22f), _hoverMainT) : baseC1;
        Color c2 = Enabled ? BackdropRenderer.LerpColor(baseC2, ControlPaint.Light(baseC2, 0.22f), _hoverMainT) : baseC2;

        if (Enabled)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(_animTime * 2.6f);
            int glowAlpha = (int)Math.Round(35 + pulse * 48 + Math.Max(_hoverMainT, _hoverArrowT) * 75);
            using var glowOuter = SpaceTheme.CreateRoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius + 2);
            using var glowPen = new Pen(Color.FromArgb(Math.Clamp(glowAlpha, 0, 175), c1), 2.5f);
            g.DrawPath(glowPen, glowOuter);
        }

        using var path = SpaceTheme.CreateRoundedRect(rect, CornerRadius);
        using (var fill = new LinearGradientBrush(rect, c1, c2, 12f))
        {
            g.FillPath(fill, path);
        }

        var oldClip = g.Clip;
        g.SetClip(path);

        // Periodic subtle diagonal shimmer sweep across the PLAY button
        if (Enabled)
        {
            float cycle = (_animTime % 4.2f) / 4.2f;
            if (cycle < 0.28f)
            {
                float t = cycle / 0.28f;
                int sx = (int)(rect.Left - 80 + t * (rect.Width + 160));
                var sweepRect = new Rectangle(sx, rect.Top - 10, 72, rect.Height + 20);
                using var sweepBrush = new LinearGradientBrush(
                    sweepRect,
                    Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(48, 255, 255, 255),
                    25f);
                sweepBrush.SetSigmaBellShape(0.5f, 0.8f);
                g.FillRectangle(sweepBrush, sweepRect);
            }
        }

        if (ShowDropdown)
        {
            var arrowZone = new Rectangle(rect.Right - ArrowZoneWidth, rect.Top, ArrowZoneWidth, rect.Height);
            if (_hoverArrowT > 0.01f)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb((int)(52 * _hoverArrowT), 255, 255, 255));
                g.FillRectangle(hoverBrush, arrowZone);
            }
            using (var sep = new Pen(Color.FromArgb(75, 0, 0, 0), 1f))
                g.DrawLine(sep, arrowZone.Left, rect.Top + 7, arrowZone.Left, rect.Bottom - 7);
            using (var sepHi = new Pen(Color.FromArgb(45, 255, 255, 255), 1f))
                g.DrawLine(sepHi, arrowZone.Left + 1, rect.Top + 7, arrowZone.Left + 1, rect.Bottom - 7);

        int cx = arrowZone.Left + ArrowZoneWidth / 2;
        int cy = arrowZone.Top + arrowZone.Height / 2 + 1;
        using var chevPen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(chevPen, cx - 5, cy - 2, cx, cy + 3);
        g.DrawLine(chevPen, cx, cy + 3, cx + 5, cy - 2);
        }

        g.Clip = oldClip;

        using (var topHi = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
        {
            g.DrawLine(topHi, rect.Left + CornerRadius, rect.Top + 1, rect.Right - CornerRadius, rect.Top + 1);
        }

        int textW = ShowDropdown ? rect.Width - ArrowZoneWidth : rect.Width;
        var textRect = new Rectangle(rect.Left, rect.Top, textW, rect.Height);
        var fg = Enabled ? ForeColor : SpaceTheme.TextMuted;
        TextRenderer.DrawText(g, Text, Font, textRect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SpaceButton : Control, IAnimatable
{
    private bool _hovered;
    private bool _pressed;
    private bool _selected;
    private float _hoverT;
    private float _selectT;
    private float _pressT;

    public Color GradientStart { get; set; } = SpaceTheme.BgCardHover;
    public Color GradientEnd { get; set; } = SpaceTheme.BgCard;
    public Color BorderColor { get; set; } = SpaceTheme.BorderSubtle;
    public int CornerRadius { get; set; } = 7;
    public bool DrawGlow { get; set; }
    public bool DrawGearIcon { get; set; }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public SpaceButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        ForeColor = SpaceTheme.TextPrimary;
        Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        Size = new Size(140, 34);
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? string.Empty; Invalidate(); }
    }

    public bool StepAnimation(float dt)
    {
        float nHover = BackdropRenderer.Approach(_hoverT, _hovered ? 1f : 0f, 15f, dt);
        float nSelect = BackdropRenderer.Approach(_selectT, _selected ? 1f : 0f, 14f, dt);
        float nPress = BackdropRenderer.Approach(_pressT, _pressed ? 1f : 0f, 22f, dt);
        bool changed = nHover != _hoverT || nSelect != _selectT || nPress != _pressT;
        _hoverT = nHover;
        _selectT = nSelect;
        _pressT = nPress;
        if (changed && Visible) Invalidate();
        return changed;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, Color.Transparent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int pressOffset = _pressT > 0.45f && Enabled ? 1 : 0;
        var rect = new Rectangle(1, 1 + pressOffset, Math.Max(1, Width - 3), Math.Max(1, Height - 3));

        Color baseC1 = Enabled
            ? BackdropRenderer.LerpColor(GradientStart, SpaceTheme.AccentRed, _selectT)
            : Color.FromArgb(35, 40, 50);
        Color baseC2 = Enabled
            ? BackdropRenderer.LerpColor(GradientEnd, SpaceTheme.AccentRedDark, _selectT)
            : Color.FromArgb(28, 32, 41);

        Color c1 = Enabled ? BackdropRenderer.LerpColor(baseC1, ControlPaint.Light(baseC1, 0.16f), _hoverT) : baseC1;
        Color c2 = Enabled ? BackdropRenderer.LerpColor(baseC2, ControlPaint.Light(baseC2, 0.16f), _hoverT) : baseC2;

        if (Enabled && (DrawGlow || _selectT > 0.05f))
        {
            using var glowPath = SpaceTheme.CreateRoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius + 1);
            int ga = (int)Math.Round(30 + _hoverT * 50 + _selectT * 35);
            using var glowPen = new Pen(Color.FromArgb(Math.Clamp(ga, 0, 140), c1), 1.5f);
            g.DrawPath(glowPen, glowPath);
        }

        using var path = SpaceTheme.CreateRoundedRect(rect, CornerRadius);
        using (var lgb = new LinearGradientBrush(rect, c1, c2, 15f))
            g.FillPath(lgb, path);

        var borderClr = BackdropRenderer.LerpColor(BorderColor, SpaceTheme.AccentRedBright, _selectT);
        if (borderClr != Color.Transparent)
        {
            var finalBorder = Enabled ? BackdropRenderer.LerpColor(borderClr, ControlPaint.Light(borderClr, 0.25f), _hoverT) : SpaceTheme.BorderSubtle;
            using var bp = new Pen(finalBorder, 1f);
            g.DrawPath(bp, path);
        }

        var fg = Enabled ? BackdropRenderer.LerpColor(ForeColor, Color.White, Math.Max(_selectT, _hoverT * 0.6f)) : SpaceTheme.TextMuted;
        if (DrawGearIcon)
        {
            DrawGear(g, rect.Left + 20, rect.Top + rect.Height / 2f, 5.2f, fg);
            var shifted = new Rectangle(rect.Left + 18, rect.Top, Math.Max(10, rect.Width - 18), rect.Height);
            TextRenderer.DrawText(g, Text, Font, shifted, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, rect, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static void DrawGear(Graphics g, float cx, float cy, float radius, Color color)
    {
        using var pen = new Pen(color, 1.6f);
        g.DrawEllipse(pen, cx - radius * 0.52f, cy - radius * 0.52f, radius * 1.04f, radius * 1.04f);
        using var toothPen = new Pen(color, 2.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 6; i++)
        {
            double a = i * Math.PI / 3.0;
            float x1 = cx + (float)Math.Cos(a) * (radius * 0.72f);
            float y1 = cy + (float)Math.Sin(a) * (radius * 0.72f);
            float x2 = cx + (float)Math.Cos(a) * (radius * 1.18f);
            float y2 = cy + (float)Math.Sin(a) * (radius * 1.18f);
            g.DrawLine(toothPen, x1, y1, x2, y2);
        }
    }
}

internal sealed class SpaceHeaderTab : Control, IAnimatable
{
    private bool _hovered;
    private bool _active;
    private float _hoverT;
    private float _activeT;

    public string IconGlyph { get; set; } = string.Empty;
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    public SpaceHeaderTab()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(124, 34);
        Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        ForeColor = SpaceTheme.TextSecondary;
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? string.Empty; Invalidate(); }
    }

    public bool StepAnimation(float dt)
    {
        float nHover = BackdropRenderer.Approach(_hoverT, _hovered ? 1f : 0f, 14f, dt);
        float nActive = BackdropRenderer.Approach(_activeT, _active ? 1f : 0f, 14f, dt);
        bool changed = nHover != _hoverT || nActive != _activeT;
        _hoverT = nHover;
        _activeT = nActive;
        if (changed && Visible) Invalidate();
        return changed;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, SpaceTheme.BgHeader);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        float pillStrength = Math.Max(_activeT, _hoverT * 0.55f);
        if (pillStrength > 0.01f)
        {
            using var path = SpaceTheme.CreateRoundedRect(rect, 7);
            using var fill = new SolidBrush(Color.FromArgb((int)Math.Round(40 * pillStrength), 255, 255, 255));
            g.FillPath(fill, path);
            if (_activeT > 0.02f)
            {
                using var border = new Pen(Color.FromArgb((int)Math.Round(55 * _activeT), 255, 255, 255), 1f);
                g.DrawPath(border, path);
            }
        }

        if (_activeT > 0.02f)
        {
            int maxW = Width - 32;
            int barW = Math.Max(4, (int)Math.Round(maxW * _activeT));
            int barX = (Width - barW) / 2;
            using var underline = new SolidBrush(Color.FromArgb((int)Math.Round(255 * _activeT), SpaceTheme.AccentRedBright));
            g.FillRectangle(underline, barX, Height - 3, barW, 2);
        }

        string label = string.IsNullOrEmpty(IconGlyph) ? Text : $"{IconGlyph}  {Text}";
        var fg = BackdropRenderer.LerpColor(SpaceTheme.TextSecondary, SpaceTheme.TextPrimary, Math.Max(_activeT, _hoverT));
        TextRenderer.DrawText(g, label, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class SpaceProgressBar : Control, IAnimatable
{
    private int _value;
    private int _maximum = 1000;
    private float _displayRatio;
    private float _animTime;

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, _maximum);
            if (_value != v) { _value = v; Invalidate(); }
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(1, value);
            if (_value > _maximum) _value = _maximum;
            Invalidate();
        }
    }

    public SpaceProgressBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(248, 5);
    }

    public bool StepAnimation(float dt)
    {
        _animTime += dt;
        float target = Math.Clamp((float)_value / _maximum, 0f, 1f);
        _displayRatio = BackdropRenderer.Approach(_displayRatio, target, 12f, dt);
        if (Visible)
        {
            Invalidate();
            return true;
        }
        return false;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, Color.Transparent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackRect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        int radius = Math.Max(2, Height / 2);
        using var trackPath = SpaceTheme.CreateRoundedRect(trackRect, radius);
        using var trackBrush = new SolidBrush(Color.FromArgb(165, 28, 34, 46));
        g.FillPath(trackBrush, trackPath);

        if (_displayRatio <= 0.005f) return;

        int fillW = Math.Max(Height, (int)Math.Round(trackRect.Width * _displayRatio));
        fillW = Math.Min(fillW, trackRect.Width);
        var fillRect = new Rectangle(0, 0, fillW, trackRect.Height);

        using var fillPath = SpaceTheme.CreateRoundedRect(fillRect, radius);
        using var fillBrush = new LinearGradientBrush(
            new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
            SpaceTheme.AccentRed,
            SpaceTheme.AccentAmber,
            0f);
        g.FillPath(fillBrush, fillPath);

        // Moving energy shimmer along the progress bar
        var oldClip = g.Clip;
        g.SetClip(fillPath);
        int shimmerW = 48;
        float t = (_animTime * 0.9f) % 1.0f;
        int sx = (int)(-shimmerW + t * (fillW + shimmerW * 2));
        var sRect = new Rectangle(sx, 0, shimmerW, Height);
        using var sBrush = new LinearGradientBrush(sRect, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(110, 255, 255, 255), 0f);
        sBrush.SetSigmaBellShape(0.5f, 0.7f);
        g.FillRectangle(sBrush, sRect);
        g.Clip = oldClip;
    }
}

internal sealed class SpaceToggle : Control, IAnimatable
{
    private bool _checked;
    private bool _hovered;
    private float _knobT;
    private float _hoverT;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public SpaceToggle()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        ForeColor = SpaceTheme.TextSecondary;
        Size = new Size(320, 28);
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value ?? string.Empty; Invalidate(); }
    }

    public bool StepAnimation(float dt)
    {
        float nKnob = BackdropRenderer.Approach(_knobT, _checked ? 1f : 0f, 18f, dt);
        float nHover = BackdropRenderer.Approach(_hoverT, _hovered ? 1f : 0f, 14f, dt);
        bool changed = nKnob != _knobT || nHover != _hoverT;
        _knobT = nKnob;
        _hoverT = nHover;
        if (changed && Visible) Invalidate();
        return changed;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, Color.FromArgb(238, 13, 16, 23));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int trackW = 38;
        int trackH = 20;
        int trackX = Width - trackW - 2;
        int trackY = (Height - trackH) / 2;
        var trackRect = new Rectangle(trackX, trackY, trackW, trackH);
        using var trackPath = SpaceTheme.CreateRoundedRect(trackRect, trackH / 2);

        Color trackColor = !Enabled
            ? Color.FromArgb(30, 35, 45)
            : BackdropRenderer.LerpColor(Color.FromArgb(40, 46, 60), SpaceTheme.AccentRed, _knobT);

        using var trackBrush = new SolidBrush(trackColor);
        g.FillPath(trackBrush, trackPath);

        if (_knobT < 0.9f || !Enabled)
        {
            using var p = new Pen(SpaceTheme.BorderBright, 1f);
            g.DrawPath(p, trackPath);
        }

        int knobSize = 14;
        float minX = trackRect.Left + 3f;
        float maxX = trackRect.Right - knobSize - 3f;
        float knobX = minX + (maxX - minX) * _knobT;
        float knobY = trackRect.Top + (trackH - knobSize) / 2f;
        using var knobBrush = new SolidBrush(Color.White);
        g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);

        var textRect = new Rectangle(0, 0, Math.Max(10, trackX - 10), Height);
        var textColor = !Enabled
            ? SpaceTheme.TextMuted
            : BackdropRenderer.LerpColor(ForeColor, SpaceTheme.TextPrimary, Math.Max(_hoverT, _knobT));
        TextRenderer.DrawText(g, Text, Font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SpaceTitleButton : Control, IAnimatable
{
    private bool _hovered;
    private float _hoverT;
    public bool IsClose { get; set; }

    public SpaceTitleButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        Size = new Size(38, 34);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        ForeColor = SpaceTheme.TextSecondary;
    }

    public bool StepAnimation(float dt)
    {
        float next = BackdropRenderer.Approach(_hoverT, _hovered ? 1f : 0f, 16f, dt);
        bool changed = next != _hoverT;
        _hoverT = next;
        if (changed && Visible) Invalidate();
        return changed;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        BackdropRenderer.PaintBackdrop(this, e.Graphics, SpaceTheme.BgHeader);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hoverT > 0.01f)
        {
            using var rectPath = SpaceTheme.CreateRoundedRect(new Rectangle(2, 4, Width - 4, Height - 8), 6);
            var tint = IsClose
                ? Color.FromArgb((int)Math.Round(210 * _hoverT), 220, 38, 38)
                : Color.FromArgb((int)Math.Round(55 * _hoverT), 255, 255, 255);
            using var b = new SolidBrush(tint);
            g.FillPath(b, rectPath);
        }

        var fg = BackdropRenderer.LerpColor(ForeColor, Color.White, _hoverT);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
