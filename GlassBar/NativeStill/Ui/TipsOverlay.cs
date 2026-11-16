using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// „Schon gewusst?“ aus guide.js: ab und zu ein leiser Tipp unten links zu etwas, das noch nicht benutzt wurde.
/// Jeder kommt nur einmal und verschwindet für immer, sobald die Funktion entdeckt ist; zweimal übergangen,
/// fragt er nicht mehr. Zustand in StillState "guide.state", abschaltbar über die Einstellung "tips".
/// Tipps zu Dingen, die es nativ nicht gibt (Hilfeseite, Aufgaben, Hilfsprogramm …), sind weggelassen.
/// </summary>
public sealed class TipsOverlay : IOverlay, IDisposable
{
    /// <summary>Die Oberfläche (StillUi.Add trägt sich selbst ein).</summary>
    public StillUi? Ui { get; set; }

    public int Z => 40;

    private sealed record Tip(string Id, string Title, string Text, string Label,
        Action<UiContext> Run, Func<UiContext, bool>? When, Func<UiContext, bool>? Done);

    /// <summary>Gespeicherter Zustand (wie still.guide.v1).</summary>
    public sealed class State
    {
        public Dictionary<string, int> seen { get; set; } = new();
        public Dictionary<string, int> ignored { get; set; } = new();
        public int shown { get; set; }
        public double nextAt { get; set; }
    }

    private const string Key = "guide.state";
    private const double Min = 60000, Hour = 60 * Min;
    private const double FirstAfter = 45000;     // so lange läuft der Hintergrund, bevor der erste Tipp kommt
    private const double EarlyGap = 30 * Min;    // zwischen den ersten, solange alles neu ist
    private const double LaterGap = 4 * Hour;    // danach
    private const int Early = 3;
    private const double VisibleFor = 22000;
    private const double Recent = 15000;         // so kürzlich bewegte Maus: jemand schaut hin
    private const int Ignores = 2;

    private readonly Tip[] _tips;
    private readonly State _state;
    private readonly double _bootAt = NowMs();
    private double _lastMove, _shownAt, _lastTick;
    private Vector2 _lastPointer = new(float.NaN);
    private Tip? _current;
    private bool _cardHover, _sawEditing;
    private readonly HashSet<string> _hoveredPills = new();

    // Darstellung
    private float _t;                                    // sichtbar 0..1
    private readonly StillUi.Spring _y = new(1, 0.8, 0.5);
    private Tip? _shownTip;                              // was gezeichnet wird (bleibt beim Ausblenden stehen)
    private Rect _card, _go, _ok, _next;
    private Vector2 _pointer;
    private bool _pointerIn;
    private string? _press;
    private double _swapAt = -10, _now;

    // Hinweisring um eine Pille
    private Pill? _spot;
    private double _spotUntil;

    private readonly UiText _text = new();
    private IntPtr _ctxPtr;
    private ID2D1SolidColorBrush? _brush;

    public TipsOverlay()
    {
        _state = StillState.Get<State?>(Key, null) ?? new State();
        _tips = new Tip[]
        {
            new("clock", "Deine Uhr ist auch ein Timer", "Klicke auf die Uhrzeit, um direkt an ihren Ziffern einen Timer oder Wecker zu stellen.", "Zeig es mir",
                ClickClock, null, null),
            new("edit", "Platziere alles, wo du möchtest", "Unter „Pillen und Uhr anordnen“ in den GlassBar-Einstellungen ziehst du die Pillen und die Uhr, wohin du möchtest.", "Ausprobieren",
                _ => { if (Ui?.Edit is { } e) e.Active = true; }, _ => Ui?.Edit != null, _ => _sawEditing),
            new("sound", "Klänge zum Konzentrieren", "Regen, Wellen, ein Feuer und mehr, live erzeugt. Kombiniere so viele, wie du magst.", "Zeig es mir",
                c => ShowPill(c, "sound"), c => HasPill("sound") && c.Settings.Bool("sounds"), _ => _hoveredPills.Contains("sound")),
            new("workout", "Ein Training ohne Geräte", "Es schlägt vor, was heute trainiert wird, läuft von selbst und passt sich an, wenn du stärker wirst.", "Zeig es mir",
                c => ShowPill(c, "workout"), c => HasPill("workout") && c.Settings.Bool("workout"), _ => _hoveredPills.Contains("workout")),
            new("water", "Ein Glas, das sich füllt", "Die Wasser-Pille begleitet deinen Tag und erinnert dich sanft.", "Zeig es mir",
                c => ShowPill(c, "water"), c => HasPill("water") && c.Settings.Bool("hydrate"), _ => _hoveredPills.Contains("water")),
            new("pomodoro", "Ein Fokus-Timer", "Die Pomodoro-Pille steuert Fokus-Einheiten und Pausen.", "Zeig es mir",
                c => ShowPill(c, "timer"), c => HasPill("timer") && c.Settings.Bool("pomo"), _ => _hoveredPills.Contains("timer")),
            new("prayer", "Gebetszeiten", "Wenn du betest, kann eine Pille die fünf Gebete begleiten, mit Erinnerungen und Verlauf.", "Zeig es mir",
                c => ShowPill(c, "prayer"), _ => HasPill("prayer"), c => c.Settings.Bool("prayer")),
        };
    }

    private static double NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private bool HasPill(string id) => Ui?.Pills.Exists(p => p.Id == id) == true;

    private void Save() => StillState.Set(Key, _state);

    /// <summary>Alle Tipps von vorn (für „Tipps erneut zeigen“).</summary>
    public void Reset()
    {
        _state.seen.Clear();
        _state.ignored.Clear();
        _state.shown = 0;
        _state.nextAt = 0;
        Save();
    }

    // ---------- Was die Knöpfe tun ----------

    /// <summary>Die Pille einschalten, kurz öffnen und mit einem Ring zeigen.</summary>
    private void ShowPill(UiContext c, string id)
    {
        if (StillUi.SettingKey(id) is { } key && !c.Settings.Bool(key)) NativeStillStore.Set(key, true);
        var pill = Ui?.Pills.Find(p => p.Id == id);
        if (pill == null) return;
        pill.AnnounceFor = Math.Max(pill.AnnounceFor, 7);
        _spot = pill;
        _spotUntil = c.Time + 7;
    }

    /// <summary>Wie ein Klick auf die Uhrzeit (öffnet dort Timer/Wecker).</summary>
    private void ClickClock(UiContext c)
    {
        if (!c.Settings.Bool("clockTap")) NativeStillStore.Set("clockTap", true);
        var r = c.ClockRect;
        if (Ui == null || r.Width <= 0 || r.Height <= 0) return;
        var p = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
        Ui.OnPointer(c, new PointerEvent(PointerKind.Down, p));
        Ui.OnPointer(c, new PointerEvent(PointerKind.Up, p));
    }

    // ---------- Ablauf ----------

    private bool Busy(UiContext c)
    {
        if (c.Editing || c.StageBusy > 0.01f) return true;
        if (Ui != null) foreach (var p in Ui.Pills) if (p.Hovered) return true;
        return false;
    }

    private bool IsDone(UiContext c, Tip t)
    {
        try { return t.Done?.Invoke(c) == true; } catch { return false; }
    }

    private bool Applies(UiContext c, Tip t)
    {
        try { return t.When?.Invoke(c) != false; } catch { return false; }
    }

    private void Hide()
    {
        _current = null;
        _cardHover = false;
    }

    private void Finish(string id)
    {
        _state.seen[id] = 1;
        Save();
        if (_current?.Id == id) Hide();
    }

    private void Fill(Tip t)
    {
        _current = t;
        _shownTip = t;
        _shownAt = NowMs();
    }

    private void ShowTip(Tip t)
    {
        Fill(t);
        _state.shown++;
        _state.nextAt = NowMs() + (_state.shown < Early ? EarlyGap : LaterGap);
        Save();
    }

    public void Update(UiContext c, double dt)
    {
        _now = c.Time;
        if (c.Pointer != _lastPointer) { _lastPointer = c.Pointer; _lastMove = NowMs(); }
        if (c.Editing) _sawEditing = true;
        if (Ui != null) foreach (var p in Ui.Pills) if (p.Hovered) _hoveredPills.Add(p.Id);

        if (c.Time - _lastTick >= 1 || c.Time < _lastTick) { _lastTick = c.Time; Tick(c); }

        float target = _current != null && !c.Editing ? 1 : 0;
        _t += (target - _t) * (float)(1 - Math.Exp(-dt * 6.5));
        if (Math.Abs(_t - target) < 0.003f) _t = target;
        _y.Target = target > 0 ? 0 : 1;
        _y.Step(dt);
        if (_t == 0 && _current == null) _shownTip = null;
        if (_spot != null && c.Time > _spotUntil) _spot = null;
        Layout(c);
    }

    private void Tick(UiContext c)
    {
        double t = NowMs();
        // Selbst gefunden: muss nie erwähnt werden
        foreach (var tip in _tips) if (!_state.seen.ContainsKey(tip.Id) && IsDone(c, tip)) Finish(tip.Id);

        bool on = c.Settings.Bool("tips");
        if (_current != null)
        {
            if (!on || Busy(c)) { Hide(); return; }   // Wichtigeres ist dran; er kann ein andermal wiederkommen
            if (!_cardHover && t - _shownAt > VisibleFor)
            {
                string id = _current.Id;
                _state.ignored[id] = _state.ignored.GetValueOrDefault(id) + 1;
                if (_state.ignored[id] >= Ignores) _state.seen[id] = 1;
                Save();
                Hide();
            }
            return;
        }
        if (!on || Busy(c)) return;
        if (t < _state.nextAt || t - _bootAt < FirstAfter || t - _lastMove > Recent) return;
        foreach (var tip in _tips)
            if (!_state.seen.ContainsKey(tip.Id) && Applies(c, tip)) { ShowTip(tip); break; }
    }

    // ---------- Aufbau und Maus ----------

    private void Layout(UiContext c)
    {
        var tip = _shownTip;
        if (tip == null) return;
        float k = c.K, w = 310 * k, inner = w - 36 * k;
        float h = 16 * k;
        h += _text.Get(c, "Schon gewusst?", 11.5f * k, 600).Metrics.Height + 2 * k;
        h += _text.Get(c, tip.Title, 16 * k, 650, inner).Metrics.Height + 4 * k;
        h += _text.Get(c, tip.Text, 13 * k, 500, inner, 1.42f).Metrics.Height + 12 * k;
        h += 32 * k + 16 * k;
        float bottom = c.Height - 88 * k;
        _card = new Rect(40 * k, bottom - h, w, h);
        float by = _card.Bottom - 16 * k - 32 * k, x = _card.X + 18 * k;
        float gw = _text.Get(c, tip.Label, 12.5f * k, 650).Metrics.WidthIncludingTrailingWhitespace + 28 * k;
        float ow = _text.Get(c, "Verstanden", 12.5f * k, 650).Metrics.WidthIncludingTrailingWhitespace + 28 * k;
        float nw = _text.Get(c, "Weiter", 12.5f * k, 650).Metrics.WidthIncludingTrailingWhitespace + 28 * k;
        _go = new Rect(x, by, gw, 32 * k);
        _ok = new Rect(x + gw + 6 * k, by, ow, 32 * k);
        _next = new Rect(_card.Right - 18 * k - nw, by, nw, 32 * k);
    }

    public bool HitTest(UiContext c, Vector2 p) => _current != null && _t > 0.5f && _card.Contains(p);

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        switch (e.Kind)
        {
            case PointerKind.Leave:
                _pointerIn = false;
                if (_cardHover)
                {
                    _cardHover = false;
                    _shownAt = NowMs() - VisibleFor + 5000;   // nach dem Verlassen noch kurz stehen lassen
                }
                return true;
            case PointerKind.Move:
                _pointer = e.Pos; _pointerIn = true; _cardHover = true;
                return true;
            case PointerKind.Down:
                _pointer = e.Pos; _cardHover = true;
                _press = e.Button == 0 ? ButtonAt(e.Pos) : null;
                return true;
            case PointerKind.Up:
                var press = _press;
                _press = null;
                if (press != null && press == ButtonAt(e.Pos)) Click(c, press);
                return true;
        }
        return true;
    }

    private string? ButtonAt(Vector2 p) =>
        _go.Contains(p) ? "go" : _ok.Contains(p) ? "ok" : _next.Contains(p) ? "next" : null;

    private void Click(UiContext c, string what)
    {
        var tip = _current;
        if (tip == null) return;
        switch (what)
        {
            case "go":
                Finish(tip.Id);
                tip.Run(c);
                break;
            case "ok":
                Finish(tip.Id);
                break;
            case "next":
                // Weiter durch die ganze Liste; was hier gelesen wurde, kommt nicht wieder
                var next = _tips[(Array.IndexOf(_tips, tip) + 1) % _tips.Length];
                _state.seen[tip.Id] = 1;
                _state.seen[next.Id] = 1;
                Save();
                Fill(next);
                _swapAt = c.Time;
                break;
        }
    }

    public bool Animating => _t > 0 || _current != null || _spot != null || _y.Moving;

    // ---------- Zeichnen ----------

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            _brush?.Dispose();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        }
        var b = _brush;
        var fg = c.Fg;
        float k = c.K;

        // Ring um die gezeigte Pille (gd-ring), atmet sanft
        if (_spot is { } spot && spot.Shown(c))
        {
            float left = (float)Math.Clamp((_spotUntil - c.Time) / 0.4, 0, 1);
            float pulse = 0.5f - 0.5f * MathF.Cos((float)(c.Time * Math.PI * 2 / 1.6));
            var r = spot.Bounds;
            float pad = 7 * k, rad = StillUi.Radius(c, r.Height, spot.Open) + pad;
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad), rad, rad);
            b.Color = new Color4(fg.X, fg.Y, fg.Z, (0.25f + 0.2f * pulse) * left);
            ctx.DrawRoundedRectangle(rr, b, 1.5f * k);
        }

        var tip = _shownTip;
        if (tip == null || _t <= 0.003f) return;
        float a = _t;
        var prev = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateTranslation(0, (float)_y.X * 10 * k) * prev;
        c.Glass.Panel(ctx, c, _card, 22 * k, a);

        float x = _card.X + 18 * k, y = _card.Y + 16 * k, inner = _card.Width - 36 * k;
        var kick = _text.Get(c, "Schon gewusst?", 11.5f * k, 600);
        b.Color = Col(fg, 0.55f * a);
        ctx.DrawTextLayout(new Vector2(x, y), kick, b, DrawTextOptions.None);
        int n = Array.IndexOf(_tips, tip) + 1;
        var count = _text.Get(c, $"{n:00}/{_tips.Length:00}", 11.5f * k, 600, 0, 0, 0.02f);
        ctx.DrawTextLayout(new Vector2(_card.Right - 18 * k - count.Metrics.WidthIncludingTrailingWhitespace, y), count, b, DrawTextOptions.None);
        y += kick.Metrics.Height + 2 * k;

        // Nach „Weiter“ blenden Titel und Text neu ein
        float sw = (float)Math.Clamp((c.Time - _swapAt) / 0.28, 0, 1);
        float swA = 1 - (1 - sw) * (1 - sw), swY = 4 * k * (1 - swA);
        var title = _text.Get(c, tip.Title, 16 * k, 650, inner);
        b.Color = Col(fg, a * swA);
        ctx.DrawTextLayout(new Vector2(x, y + swY), title, b, DrawTextOptions.None);
        y += title.Metrics.Height + 4 * k;
        var text = _text.Get(c, tip.Text, 13 * k, 500, inner, 1.42f);
        b.Color = Col(fg, 0.75f * a * swA);
        ctx.DrawTextLayout(new Vector2(x, y + swY), text, b, DrawTextOptions.None);

        string? hover = _pointerIn ? ButtonAt(_pointer) : null;
        Button(ctx, c, _go, tip.Label, hover == "go" ? 0.3f : 0.22f, 1, a);
        Button(ctx, c, _ok, "Verstanden", hover == "ok" ? 0.14f : 0.08f, 0.78f, a);
        Button(ctx, c, _next, "Weiter", hover == "next" ? 0.14f : 0.08f, 0.78f, a);
        ctx.Transform = prev;
    }

    private void Button(ID2D1DeviceContext ctx, UiContext c, Rect r, string label, float bg, float fgA, float a)
    {
        var b = _brush!;
        var fg = c.Fg;
        var pressed = _press != null && r.Contains(_pointer);
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.Width, r.Height), r.Height / 2, r.Height / 2);
        var prev = ctx.Transform;
        if (pressed) ctx.Transform = Matrix3x2.CreateScale(0.95f, new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2)) * prev;
        b.Color = Col(fg, bg * a);
        ctx.FillRoundedRectangle(ref rr, b);
        var l = _text.Get(c, label, 12.5f * c.K, 650);
        b.Color = Col(fg, fgA * a);
        ctx.DrawTextLayout(new Vector2(r.X + 14 * c.K, r.Y + (r.Height - l.Metrics.Height) / 2), l, b, DrawTextOptions.None);
        ctx.Transform = prev;
    }

    private static Color4 Col(Vector3 c, float a) => new(c.X, c.Y, c.Z, a);

    public void Dispose()
    {
        _brush?.Dispose();
        _brush = null;
        _text.Dispose();
    }
}
