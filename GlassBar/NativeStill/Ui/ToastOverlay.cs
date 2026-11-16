using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Ein kurzer Hinweis unten in der Mitte, über der unteren Pillen-Reihe (wie die Bestätigungen im Original,
/// z. B. „Gut gemacht.“): kleines Glas, kommt weich, bleibt ein paar Sekunden und geht wieder.
/// <see cref="Show"/> darf aus jedem Thread kommen.
/// </summary>
public sealed class ToastOverlay : IOverlay, IDisposable
{
    public int Z => 70;

    private readonly object _lock = new();
    private string? _pending;
    private string? _text;
    private double _shownAt, _until, _now;
    private float _t;
    private readonly UiText _layouts = new();
    private IntPtr _ctxPtr;
    private ID2D1SolidColorBrush? _brush;

    /// <summary>Hinweis zeigen (ersetzt einen gerade sichtbaren).</summary>
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock) _pending = text.Trim();
    }

    public void Update(UiContext c, double dt)
    {
        _now = c.Time;
        string? next;
        lock (_lock) { next = _pending; _pending = null; }
        if (next != null)
        {
            _text = next;
            _shownAt = c.Time;
            _until = c.Time + Math.Clamp(2.6 + next.Length * 0.05, 2.6, 6);
        }
        float target = _text != null && c.Time < _until ? 1 : 0;
        _t += (target - _t) * (float)(1 - Math.Exp(-dt * (target > _t ? 7 : 5)));
        if (Math.Abs(_t - target) < 0.003f) _t = target;
        if (_t == 0 && target == 0) _text = null;
    }

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_text == null || _t <= 0.003f) return;
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            _brush?.Dispose();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        }
        float k = c.K;
        var l = _layouts.Get(c, _text, 14 * k, 600, 460 * k, 1.4f, 0, true);
        float w = l.Metrics.Width + 40 * k, h = l.Metrics.Height + 22 * k;
        // Über der unteren Reihe (88 + 40 Einheiten), mit etwas Luft
        float y = c.Height - (88 + 40 + 22) * k - h + 8 * k * (1 - _t);
        var r = new Rect((c.Width - w) / 2, y, w, h);
        c.Glass.Sheet(ctx, c, r, Math.Min(h / 2, 20 * k), _t);
        _brush.Color = new Color4(c.Fg.X, c.Fg.Y, c.Fg.Z, 0.92f * _t);
        float lx = r.X + 20 * k - l.Metrics.Left;
        ctx.DrawTextLayout(new Vector2(lx, r.Y + 11 * k), l, _brush, DrawTextOptions.None);
    }

    public bool OnPointer(UiContext c, PointerEvent e) => false;
    public bool HitTest(UiContext c, Vector2 p) => false;
    public bool Animating => _t > 0 || _pending != null || _text != null && _now < _until;

    public void Dispose()
    {
        _brush?.Dispose();
        _brush = null;
        _layouts.Dispose();
    }
}
