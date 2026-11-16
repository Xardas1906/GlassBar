using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Der Anordnen-Modus aus layout.js: Pillen entlang ihrer Reihe oder in die andere ziehen, die Uhr frei verschieben
/// (mit Zug zur Mitte und zu den Dritteln), mit − ausblenden und über + zurückholen. Escape oder ein Klick auf
/// eine leere Stelle beendet ihn. Gestartet wird er über GlassBar (Einstellungen → „Pillen und Uhr anordnen“).
/// </summary>
public sealed class EditOverlay : IOverlay, IDisposable
{
    /// <summary>Die Oberfläche (setzt der Integrator; StillUi.Add trägt sich selbst ein).</summary>
    public StillUi? Ui { get; set; }

    private bool _active;
    private volatile bool _want;
    /// <summary>Anordnen-Modus an/aus. Darf aus jedem Thread gesetzt werden; wirksam im nächsten Bild.</summary>
    public bool Active
    {
        get => _want;
        set => _want = value;
    }

    /// <summary>Übernimmt den gewünschten Zustand (im Render-Thread).</summary>
    private void Apply(double now)
    {
        if (_want == _active) return;
        _active = _want;
        if (_active)
        {
            _enteredAt = now;
            if (!StillState.Get("layout.intro", false))
            {
                StillState.Set("layout.intro", true);
                _introUntil = now + 9;
            }
            return;
        }
        _sheetOpen = false;
        _introUntil = 0;
        EndDrag(drop: true);
        _press = null;
    }

    public int Z => 60;

    private static readonly string[] Ids = { "music", "water", "timer", "prayer", "sound", "workout", "weather" };
    private static string Label(string id) => id switch
    {
        "music" => "Läuft gerade", "water" => "Wasser", "timer" => "Pomodoro", "prayer" => "Gebetszeiten",
        "sound" => "Klangwelten", "workout" => "Training", "weather" => "Wetter", _ => id,
    };
    private static string Short(string id) => id switch { "music" => "Musik", "sound" => "Klänge", _ => Label(id) };

    private const string IntroText = "Ziehen zum Verschieben. − blendet aus, + holt zurück.";
    private const string IconsTip = "Für den aufgeräumtesten Look blende deine Desktopsymbole aus: Rechtsklick auf den Desktop → Ansicht → Desktopsymbole anzeigen.";

    private float _t, _sheetT, _plusTurn;
    private bool _sheetOpen;
    private double _now, _enteredAt = -10, _lastDrop = -10, _introUntil, _bumpAt = -10;
    private Vector2 _pointer;
    private bool _pointerIn;
    private string? _press;

    // Ziehen
    private string? _dragId;
    private Pill? _dragPill;
    private Vector2 _down;
    private bool _dragMoved;
    private double _cx, _cy, _nx, _ny, _fx = double.NaN, _fy = double.NaN;
    private float? _guideX, _guideY;

    private readonly List<(Vector2 From, Vector2 To, double Start)> _beads = new();

    // Aufbau (je Bild berechnet)
    private Rect _add, _done, _sheet, _reset;
    private readonly List<(string Id, Rect R)> _rows = new();
    private readonly List<string> _hidden = new();
    private float _tipY, _emptyY, _footY;

    private readonly UiText _text = new();
    private IntPtr _ctxPtr;
    private ID2D1SolidColorBrush? _brush;

    public void Update(UiContext c, double dt)
    {
        _now = c.Time;
        Apply(c.Time);
        float ease = (float)(1 - Math.Exp(-dt * 9));
        _t = Approach(_t, _active ? 1 : 0, ease);
        _sheetT = Approach(_sheetT, _sheetOpen && _active ? 1 : 0, (float)(1 - Math.Exp(-dt * 14)));
        _plusTurn = Approach(_plusTurn, _sheetOpen ? 1 : 0, (float)(1 - Math.Exp(-dt * 12)));
        if (_beads.RemoveAll(b => c.Time - b.Start > 0.56) > 0) _bumpAt = c.Time;
        Layout(c);
    }

    private static float Approach(float v, float target, float f)
    {
        v += (target - v) * f;
        return Math.Abs(v - target) < 0.002f ? target : v;
    }

    public bool Animating => _active || _t > 0 || _beads.Count > 0;

    public bool HitTest(UiContext c, Vector2 p) => _active;

    /// <summary>Tasten, falls das Fenster welche bekommt: Escape beendet den Modus.</summary>
    public bool OnKeyDown(int virtualKey)
    {
        if (virtualKey != 0x1B || !_want) return false;
        _want = false;
        return true;
    }

    // ---------- Aufbau ----------

    private void Layout(UiContext c)
    {
        float pk = c.PK;
        _add = new Rect(24 * pk, 24 * pk, 40 * pk, 40 * pk);
        var doneL = _text.Get(c, "Fertig", 14 * pk, 650);
        float dw = doneL.Metrics.WidthIncludingTrailingWhitespace + 40 * pk;
        _done = new Rect(c.Width - 24 * pk - dw, 24 * pk, dw, 40 * pk);

        _hidden.Clear();
        foreach (var id in Ids)
            if ((id == "weather" || Ui?.Pills.Exists(p => p.Id == id) == true) && StillUi.SettingKey(id) is { } key && !c.Settings.Bool(key))
                _hidden.Add(id);

        float x0 = 24 * pk, y0 = 74 * pk, w = 290 * pk, pad = 10 * pk;
        float y = y0 + pad;
        y += 4 * pk + _text.Get(c, "AUSGEBLENDET", 11 * pk, 700).Metrics.Height + 6 * pk;
        _rows.Clear();
        foreach (var id in _hidden)
        {
            _rows.Add((id, new Rect(x0 + pad, y, w - 2 * pad, 44 * pk)));
            y += 44 * pk;
        }
        _emptyY = y;
        if (_hidden.Count == 0) y += 4 * pk + _text.Get(c, "Alles ist auf dem Bildschirm.", 13 * pk, 500).Metrics.Height + 8 * pk;
        y += 6 * pk;
        _footY = y;
        y += 1 + 6 * pk;
        var resetL = _text.Get(c, "Layout zurücksetzen", 12.5f * pk, 600);
        float rw = resetL.Metrics.WidthIncludingTrailingWhitespace + 24 * pk;
        _reset = new Rect(x0 + w - pad - rw, y, rw, 30 * pk);
        y += 30 * pk + 6 * pk;
        _tipY = y;
        y += _text.Get(c, IconsTip, 12 * pk, 500, w - 2 * pad - 16 * pk, 1.45f).Metrics.Height + 2 * pk + pad;
        _sheet = new Rect(x0, y0, w, y - y0);
    }

    private Rect? ClockHandle(UiContext c)
    {
        Rect? r = null;
        if (c.ClockRect.Width > 0 && c.ClockRect.Height > 0) r = c.ClockRect;
        if (c.DateRect.Width > 0 && c.DateRect.Height > 0) r = r is { } a ? Rect.Union(a, c.DateRect) : c.DateRect;
        if (r is not { } u) return null;
        float p = 18 * c.K;
        return new Rect(u.X - p, u.Y - p, u.Width + 2 * p, u.Height + 2 * p);
    }

    private static Vector2 MinusCentre(UiContext c, Rect r) => new(r.Left + 4 * c.K, r.Top + 4 * c.K);

    private static bool InCircle(Vector2 p, Vector2 m, float r) => Vector2.DistanceSquared(p, m) <= r * r;

    /// <summary>Was liegt an der Stelle? (Reihenfolge wie die Ebenen übereinander)</summary>
    private string HitAt(UiContext c, Vector2 p)
    {
        if (_sheetOpen && _sheet.Contains(p))
        {
            foreach (var (id, r) in _rows) if (r.Contains(p)) return "row:" + id;
            if (_reset.Contains(p)) return "reset";
            return "sheet";
        }
        if (InCircle(p, Centre(_add), _add.Width / 2)) return "add";
        if (_done.Contains(p)) return "done";
        if (Ui != null)
        {
            if (_dragId == null)
                foreach (var pill in Ui.Pills)
                    if (Ui.IsOn(c, pill) && Ui.VisualRect(c, pill) is { } r && InCircle(p, MinusCentre(c, r), 13 * c.K))
                        return "minus:" + pill.Id;
            for (int i = Ui.Pills.Count - 1; i >= 0; i--)
            {
                var pill = Ui.Pills[i];
                if (Ui.IsOn(c, pill) && Ui.VisualRect(c, pill) is { } r && r.Contains(p)) return "pill:" + pill.Id;
            }
        }
        if (ClockHandle(c) is { } ch && ch.Contains(p)) return "clock";
        return "empty";
    }

    // ---------- Maus ----------

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        _now = c.Time;
        Apply(c.Time);
        if (!_active) return false;
        switch (e.Kind)
        {
            case PointerKind.Leave:
                _pointerIn = false;
                return true;
            case PointerKind.Move:
                _pointer = e.Pos; _pointerIn = true;
                if (_dragId != null) DragMove(c, e.Pos);
                return true;
            case PointerKind.Down:
                _pointer = e.Pos; _pointerIn = true;
                if (e.Button != 0) return true;
                _press = HitAt(c, e.Pos);
                if (_press.StartsWith("pill:") || _press == "clock")
                {
                    _sheetOpen = false;
                    _dragId = _press == "clock" ? "clock" : _press[5..];
                    _dragPill = Ui?.Pills.Find(p => p.Id == _dragId);
                    _down = e.Pos;
                    _dragMoved = false;
                }
                return true;
            case PointerKind.Up:
                _pointer = e.Pos;
                if (_dragId != null)
                {
                    bool moved = _dragMoved;
                    EndDrag(drop: true);
                    _press = null;
                    if (moved) _lastDrop = c.Time;
                    return true;
                }
                var press = _press;
                _press = null;
                if (press == null || HitAt(c, e.Pos) != press) return true;
                Click(c, press);
                return true;
        }
        return true;
    }

    private void Click(UiContext c, string what)
    {
        if (what == "add") { _sheetOpen = !_sheetOpen; return; }
        if (what == "done") { _want = false; Apply(c.Time); return; }
        if (what == "reset") { ResetLayout(); return; }
        if (what.StartsWith("row:")) { if (StillUi.SettingKey(what[4..]) is { } key) NativeStillStore.Set(key, true); return; }
        if (what.StartsWith("minus:")) { Hide(c, what[6..]); return; }
        if (what == "empty")
        {
            // Erst schließt ein Klick ins Leere das Blatt, dann den Modus
            if (c.Time - _lastDrop < 0.3 || c.Time - _enteredAt < 0.5) return;
            if (_sheetOpen) _sheetOpen = false;
            else { _want = false; Apply(c.Time); }
        }
    }

    /// <summary>Ausblenden: eine Glasperle trägt es in den +-Knopf.</summary>
    private void Hide(UiContext c, string id)
    {
        if (Ui?.Pills.Find(p => p.Id == id) is { } pill && Ui.VisualRect(c, pill) is { } r)
            _beads.Add((Centre(r), Centre(_add), c.Time));
        if (StillUi.SettingKey(id) is { } key) NativeStillStore.Set(key, false);
    }

    private void ResetLayout()
    {
        Ui?.ResetRows();
        NativeStillStore.Set("clockX", StillValues.OriginalDefaults["clockX"]);
        NativeStillStore.Set("clockY", StillValues.OriginalDefaults["clockY"]);
        NativeStillStore.Set("weatherPos", StillValues.OriginalDefaults["weatherPos"]);
    }

    private void DragMove(UiContext c, Vector2 p)
    {
        var d = p - _down;
        if (!_dragMoved)
        {
            if (d.Length() < 4) return;
            _dragMoved = true;
            if (_dragId == "clock") ClockStart(c);
            else if (_dragPill != null) Ui?.CarryStart(_dragPill);
        }
        if (_dragId == "clock") ClockMove(c, d);
        else Ui?.CarryMove(c, d, p);
    }

    private void EndDrag(bool drop)
    {
        if (_dragId == null) return;
        if (_dragMoved)
        {
            if (_dragId == "clock") ClockDrop();
            else Ui?.CarryEnd();
        }
        _dragId = null;
        _dragPill = null;
        _dragMoved = false;
        _guideX = _guideY = null;
    }

    /* Die Uhr: frei, mit Zug zur Mitte und zu den Dritteln. Versatz in Einheiten der Einstellung (±1 = 30 % des Bildschirms). */
    private void ClockStart(UiContext c)
    {
        var s = c.Settings;
        _cx = s.Num("clockX");
        _cy = s.Num("clockY");
        var r = c.ClockRect;
        bool has = r.Width > 0 && r.Height > 0;
        // Wo die Mitte ohne jeden Versatz läge
        _nx = has ? r.X + r.Width / 2 - _cx * 0.3 * c.Width : c.Width / 2.0;
        _ny = has ? r.Y + r.Height / 2 - _cy * 0.3 * c.Height : c.Height / 2.0;
        _fx = _fy = double.NaN;
    }

    private void ClockMove(UiContext c, Vector2 d)
    {
        double vw = c.Width, vh = c.Height, pull = 16 * c.K;
        double fx = Math.Clamp(_cx + d.X / (0.3 * vw), -1, 1);
        double fy = Math.Clamp(_cy + d.Y / (0.3 * vh), -1, 1);
        double x = _nx + fx * 0.3 * vw, y = _ny + fy * 0.3 * vh;
        double defY = Convert.ToDouble(StillValues.OriginalDefaults["clockY"], CultureInfo.InvariantCulture);
        double? gx = Snap(x, pull, vw / 2, vw / 3, vw * 2 / 3);
        double? gy = Snap(y, pull, _ny + defY * 0.3 * vh, vh / 3, vh * 2 / 3);
        if (gx is { } a) fx = (a - _nx) / (0.3 * vw);
        if (gy is { } b) fy = (b - _ny) / (0.3 * vh);
        _fx = Math.Clamp(fx, -1, 1);
        _fy = Math.Clamp(fy, -1, 1);
        _guideX = gx is { } ga ? (float)ga : null;
        _guideY = gy is { } gb ? (float)gb : null;
        // Live zeigen, gespeichert wird beim Loslassen
        c.Settings.Set("clockX", _fx);
        c.Settings.Set("clockY", _fy);
    }

    private static double? Snap(double v, double pull, params double[] lines)
    {
        foreach (var l in lines) if (Math.Abs(v - l) < pull) return l;
        return null;
    }

    private void ClockDrop()
    {
        _guideX = _guideY = null;
        if (double.IsNaN(_fx)) return;
        NativeStillStore.Set("clockX", Math.Round(_fx * 1000) / 1000);
        NativeStillStore.Set("clockY", Math.Round(_fy * 1000) / 1000);
    }

    // ---------- Zeichnen ----------

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_t <= 0.002f) return;
        EnsureDevice(ctx);
        var b = _brush!;
        var fg = c.Fg;
        float k = c.K, pk = c.PK, a = _t;
        bool dragging = _dragId != null && _dragMoved;
        string hover = _pointerIn && _active && !dragging ? HitAt(c, _pointer) : "";

        // Griffe der Pillen: Platzhalter, Ring beim Drüberfahren
        if (Ui != null)
        {
            foreach (var pill in Ui.Pills)
            {
                if (!Ui.IsOn(c, pill) || Ui.VisualRect(c, pill) is not { } r) continue;
                bool lifted = dragging && _dragPill == pill;
                if (Ui.IsGhost(c, pill))
                {
                    var rr = Round(r, r.Height / 2);
                    b.Color = Col(fg, 0.05f * a);
                    ctx.FillRoundedRectangle(ref rr, b);
                    b.Color = Col(fg, 0.32f * a);
                    ctx.DrawRoundedRectangle(Round(Inset(r, 0.75f * k), r.Height / 2 - 0.75f * k), b, 1.5f * k, UiIcons.Dashed(c.D2D));
                    var lab = _text.Get(c, Short(pill.Id), 13 * k, 550);
                    float lw = lab.Metrics.WidthIncludingTrailingWhitespace, iw = 16 * k, gap = 7 * k;
                    float x = r.X + (r.Width - (iw + gap + lw)) / 2, cy = r.Y + r.Height / 2;
                    b.Color = Col(fg, 0.72f * a);
                    UiIcons.Draw(ctx, c.D2D, pill.Id, new Vector2(x + iw / 2, cy), iw, b);
                    ctx.DrawTextLayout(new Vector2(x + iw + gap, cy - lab.Metrics.Height / 2), lab, b, DrawTextOptions.None);
                }
                if (!lifted && hover == "pill:" + pill.Id)
                {
                    b.Color = Col(fg, 0.32f * a);
                    ctx.DrawRoundedRectangle(Round(Inset(r, -0.75f * k), r.Height / 2 + 0.75f * k), b, 1.5f * k);
                }
            }
        }

        // Die Uhr: gestrichelter Rahmen
        if (ClockHandle(c) is { } chr)
        {
            bool on = hover == "clock" || dragging && _dragId == "clock";
            var rr = Round(chr, 30 * k);
            if (on) { b.Color = Col(fg, 0.03f * a); ctx.FillRoundedRectangle(ref rr, b); }
            b.Color = Col(fg, (on ? 0.42f : 0.2f) * a);
            ctx.DrawRoundedRectangle(Round(Inset(chr, 0.75f * k), 30 * k), b, 1.5f * k, UiIcons.Dashed(c.D2D));
        }

        // Wo die getragene Pille landen würde
        if (Ui != null && Ui.CarrySlot(c, out var slot, out bool full) && slot.Width > 0.5f)
        {
            var acc = c.Accent;
            if (!full)
            {
                var rr = Round(slot, slot.Height / 2);
                b.Color = new Color4(acc.X, acc.Y, acc.Z, 0.12f * a);
                ctx.FillRoundedRectangle(ref rr, b);
                b.Color = new Color4(acc.X, acc.Y, acc.Z, 0.85f * a);
                ctx.DrawRoundedRectangle(Round(Inset(slot, 0.75f * k), slot.Height / 2 - 0.75f * k), b, 1.5f * k);
            }
            else
            {
                b.Color = Col(fg, 0.3f * a);
                ctx.DrawRoundedRectangle(Round(Inset(slot, 0.75f * k), slot.Height / 2 - 0.75f * k), b, 1.5f * k, UiIcons.Dashed(c.D2D));
                var m = Centre(slot);
                float h = 7 * k * 0.7071f;
                b.Color = Col(fg, 0.45f * a);
                ctx.DrawLine(m + new Vector2(-h, -h), m + new Vector2(h, h), b, 1.5f * k);
                ctx.DrawLine(m + new Vector2(-h, h), m + new Vector2(h, -h), b, 1.5f * k);
            }
        }

        // Hilfslinien der Uhr
        if (_guideX != null || _guideY != null)
        {
            var acc = c.Accent;
            b.Color = new Color4(acc.X, acc.Y, acc.Z, 0.6f * a);
            if (_guideX is { } gx) ctx.FillRectangle(new Vortice.RawRectF(gx, 0, gx + 1, c.Height), b);
            if (_guideY is { } gy) ctx.FillRectangle(new Vortice.RawRectF(0, gy, c.Width, gy + 1), b);
        }

        // − in der Ecke jeder Pille
        if (Ui != null && !dragging)
        {
            foreach (var pill in Ui.Pills)
            {
                if (!Ui.IsOn(c, pill) || Ui.VisualRect(c, pill) is not { } r) continue;
                var m = MinusCentre(c, r);
                float s = 0.3f + 0.7f * EaseOut(a);
                float rad = 11 * k * s;
                b.Color = new Color4(0, 0, 0, 0.18f * a);
                ctx.FillEllipse(new Ellipse(m + new Vector2(0, 2 * k), rad + 1.5f * k, rad + 1.5f * k), b);
                bool hv = hover == "minus:" + pill.Id;
                b.Color = c.LightTheme ? new Color4(28 / 255f, 30 / 255f, 40 / 255f, (hv ? 1 : 0.86f) * a)
                    : hv ? new Color4(1, 1, 1, a) : new Color4(238 / 255f, 240 / 255f, 246 / 255f, 0.94f * a);
                ctx.FillEllipse(new Ellipse(m, rad, rad), b);
                b.Color = c.LightTheme ? new Color4(1, 1, 1, a) : new Color4(0x1b / 255f, 0x1d / 255f, 0x26 / 255f, a);
                UiIcons.Draw(ctx, c.D2D, "minus", m, 13 * k * s, b, 2.6f);
            }
        }

        DrawButtons(ctx, c, hover);
        if (_sheetT > 0.002f) DrawSheet(ctx, c, hover);
        DrawIntro(ctx, c);

        // Perlen auf dem Weg zum +
        foreach (var (from, to, start) in _beads)
        {
            float t = (float)Math.Clamp((_now - start) / 0.56, 0, 1);
            float e = EaseInOut(t);
            var p = Vector2.Lerp(from, to, e);
            float rad = 13 * k * (1 - 0.6f * e);
            b.Color = Col(fg, 0.45f * (0.9f - 0.7f * e));
            ctx.FillEllipse(new Ellipse(p, rad, rad), b);
        }
    }

    private void DrawButtons(ID2D1DeviceContext ctx, UiContext c, string hover)
    {
        var b = _brush!;
        var fg = c.Fg;
        float pk = c.PK, a = _t, s = 0.7f + 0.3f * EaseOut(a);
        var prev = ctx.Transform;

        // + oben links (mit Zähler)
        double bump = _now - _bumpAt;
        float bs = bump is >= 0 and < 0.5 ? 1 + 0.16f * (float)Math.Sin(Math.PI * bump / 0.5) : 1;
        var addC = Centre(_add);
        ctx.Transform = Matrix3x2.CreateScale(s * bs, addC) * prev;
        c.Glass.Panel(ctx, c, _add, _add.Width / 2, a);
        b.Color = Col(fg, 0.9f * a);
        UiIcons.Draw(ctx, c.D2D, "plus", addC, 19 * pk, b, 1.6f, 45 * _plusTurn);
        if (_hidden.Count > 0)
        {
            var num = _text.Get(c, _hidden.Count.ToString(CultureInfo.InvariantCulture), 11 * pk, 700);
            float w = Math.Max(18 * pk, num.Metrics.WidthIncludingTrailingWhitespace + 10 * pk), h = 18 * pk;
            var br = new Rect(_add.Right + 3 * pk - w, _add.Top - 3 * pk, w, h);
            var rr = Round(br, h / 2);
            var acc = c.Accent;
            b.Color = new Color4(acc.X, acc.Y, acc.Z, a);
            ctx.FillRoundedRectangle(ref rr, b);
            b.Color = new Color4(0x10 / 255f, 0x12 / 255f, 0x1a / 255f, a);
            ctx.DrawTextLayout(new Vector2(br.X + (w - num.Metrics.Width) / 2, br.Y + (h - num.Metrics.Height) / 2), num, b, DrawTextOptions.None);
        }

        // Fertig oben rechts
        ctx.Transform = Matrix3x2.CreateScale(s, Centre(_done)) * prev;
        var dr = Round(_done, 20 * pk);
        b.Color = new Color4(0, 0, 0, 0.18f * a);
        var sh = Round(new Rect(_done.X + 2 * pk, _done.Y + 6 * pk, _done.Width - 4 * pk, _done.Height), 20 * pk);
        ctx.FillRoundedRectangle(ref sh, b);
        b.Color = Col(fg, (hover == "done" ? 1 : 0.92f) * a);
        ctx.FillRoundedRectangle(ref dr, b);
        var doneL = _text.Get(c, "Fertig", 14 * pk, 650);
        b.Color = c.LightTheme ? new Color4(1, 1, 1, a) : new Color4(0x12 / 255f, 0x13 / 255f, 0x1a / 255f, a);
        ctx.DrawTextLayout(new Vector2(_done.X + 20 * pk, _done.Y + (_done.Height - doneL.Metrics.Height) / 2), doneL, b, DrawTextOptions.None);
        ctx.Transform = prev;
    }

    private void DrawSheet(ID2D1DeviceContext ctx, UiContext c, string hover)
    {
        var b = _brush!;
        var fg = c.Fg;
        float pk = c.PK, a = _sheetT * _t;
        var prev = ctx.Transform;
        float sc = 0.92f + 0.08f * EaseOut(_sheetT);
        ctx.Transform = Matrix3x2.CreateScale(sc, new Vector2(_sheet.X, _sheet.Y)) * Matrix3x2.CreateTranslation(0, -6 * pk * (1 - _sheetT)) * prev;
        c.Glass.Sheet(ctx, c, _sheet, 22 * pk, a);

        float x = _sheet.X + 10 * pk;
        var title = _text.Get(c, "AUSGEBLENDET", 11 * pk, 700, 0, 0, 0.14f);
        b.Color = Col(fg, 0.38f * a);
        ctx.DrawTextLayout(new Vector2(x + 8 * pk, _sheet.Y + 14 * pk), title, b, DrawTextOptions.None);

        foreach (var (id, r) in _rows)
        {
            if (hover == "row:" + id)
            {
                var hr = Round(r, 12 * pk);
                b.Color = Col(fg, 0.07f * a);
                ctx.FillRoundedRectangle(ref hr, b);
            }
            var ic = new Vector2(r.X + 8 * pk + 15 * pk, r.Y + r.Height / 2);
            b.Color = Col(fg, 0.08f * a);
            ctx.FillEllipse(new Ellipse(ic, 15 * pk, 15 * pk), b);
            b.Color = Col(fg, 0.9f * a);
            UiIcons.Draw(ctx, c.D2D, id, ic, 16 * pk, b);
            var lab = _text.Get(c, Label(id), 13.5f * pk, 550);
            ctx.DrawTextLayout(new Vector2(r.X + 48 * pk, r.Y + (r.Height - lab.Metrics.Height) / 2), lab, b, DrawTextOptions.None);
            var pc = new Vector2(r.Right - 8 * pk - 11 * pk, ic.Y);
            var acc = c.Accent;
            b.Color = new Color4(acc.X, acc.Y, acc.Z, 0.9f * a);
            ctx.FillEllipse(new Ellipse(pc, 11 * pk, 11 * pk), b);
            b.Color = new Color4(0x10 / 255f, 0x12 / 255f, 0x1a / 255f, a);
            UiIcons.Draw(ctx, c.D2D, "plus", pc, 13 * pk, b, 2.4f);
        }
        if (_hidden.Count == 0)
        {
            var empty = _text.Get(c, "Alles ist auf dem Bildschirm.", 13 * pk, 500);
            b.Color = Col(fg, 0.6f * a);
            ctx.DrawTextLayout(new Vector2(x + 8 * pk, _emptyY + 4 * pk), empty, b, DrawTextOptions.None);
        }

        b.Color = Col(fg, 0.07f * a);
        ctx.FillRectangle(new Vortice.RawRectF(x, _footY, _sheet.Right - 10 * pk, _footY + 1), b);
        if (hover == "reset")
        {
            var rr = Round(_reset, 10 * pk);
            b.Color = Col(fg, 0.07f * a);
            ctx.FillRoundedRectangle(ref rr, b);
        }
        var resetL = _text.Get(c, "Layout zurücksetzen", 12.5f * pk, 600);
        b.Color = Col(fg, (hover == "reset" ? 1 : 0.6f) * a);
        ctx.DrawTextLayout(new Vector2(_reset.X + 12 * pk, _reset.Y + (_reset.Height - resetL.Metrics.Height) / 2), resetL, b, DrawTextOptions.None);

        var tip = _text.Get(c, IconsTip, 12 * pk, 500, _sheet.Width - 20 * pk - 16 * pk, 1.45f);
        b.Color = Col(fg, 0.5f * a);
        ctx.DrawTextLayout(new Vector2(x + 8 * pk, _tipY), tip, b, DrawTextOptions.None);
        ctx.Transform = prev;
    }

    /// <summary>Beim ersten Mal: wie es geht, und der Tipp zu den Desktopsymbolen.</summary>
    private void DrawIntro(ID2D1DeviceContext ctx, UiContext c)
    {
        double left = _introUntil - _now;
        if (left <= 0 || !_active) return;
        float pk = c.PK;
        double since = 9 - left;
        float a = (float)Math.Clamp(Math.Min(since / 0.5, left / 0.5), 0, 1) * _t;
        float maxW = 470 * pk, inner = maxW - 40 * pk;
        var main = _text.Get(c, IntroText, 14 * pk, 550, inner, 1.45f, 0, true);
        var small = _text.Get(c, IconsTip, 12 * pk, 500, inner, 1.45f, 0, true);
        float h = 11 * pk + main.Metrics.Height + 3 * pk + small.Metrics.Height + 12 * pk;
        float w = Math.Max(main.Metrics.Width, small.Metrics.Width) + 40 * pk;
        float dy = -8 * pk * (1 - EaseOut(Math.Min(1, (float)since / 0.7f)));
        var r = new Rect((c.Width - w) / 2, 96 * pk + dy, w, h);
        c.Glass.Sheet(ctx, c, r, 20 * pk, a);
        var b = _brush!;
        b.Color = Col(c.Fg, 0.9f * a);
        float cw = w - 40 * pk;
        ctx.DrawTextLayout(new Vector2(r.X + 20 * pk - main.Metrics.Left + (cw - main.Metrics.Width) / 2, r.Y + 11 * pk), main, b, DrawTextOptions.None);
        b.Color = Col(c.Fg, 0.55f * a);
        ctx.DrawTextLayout(new Vector2(r.X + 20 * pk - small.Metrics.Left + (cw - small.Metrics.Width) / 2,
            r.Y + 11 * pk + main.Metrics.Height + 3 * pk), small, b, DrawTextOptions.None);
    }

    // ---------- Hilfen ----------

    private static Color4 Col(Vector3 c, float a) => new(c.X, c.Y, c.Z, a);
    private static Vector2 Centre(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    private static Rect Inset(Rect r, float d) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));
    private static RoundedRectangle Round(Rect r, float rad)
    {
        rad = Math.Max(0, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        return new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.Width, r.Height), rad, rad);
    }
    private static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);
    private static float EaseInOut(float t) => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;

    private void EnsureDevice(ID2D1DeviceContext ctx)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        _brush?.Dispose();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    }

    public void Dispose()
    {
        _brush?.Dispose();
        _brush = null;
        _text.Dispose();
    }
}

/// <summary>
/// Zwischenspeicher für Textlayouts der Oberfläche (damit nicht jedes Bild neue DirectWrite-Objekte entstehen).
/// Die Layouts gehören dem Speicher; nur innerhalb des Bildes benutzen.
/// </summary>
internal sealed class UiText : IDisposable
{
    private readonly record struct Key(string Text, IDWriteTextFormat Format, int MaxW, int Line, int Track, bool Center);
    private readonly Dictionary<Key, IDWriteTextLayout> _cache = new();

    /// <summary>maxWidth 0 = einzeilig; lineHeight 0 = Standard (sonst Vielfaches der Größe); track = Laufweite in em.</summary>
    public IDWriteTextLayout Get(UiContext c, string text, float size, int weight, float maxWidth = 0, float lineHeight = 0,
        float track = 0, bool center = false)
    {
        var fmt = c.Fonts.Format(c, size, weight);
        var key = new Key(text, fmt, (int)MathF.Round(maxWidth * 2), (int)MathF.Round(lineHeight * 100), (int)MathF.Round(track * 1000), center);
        if (_cache.TryGetValue(key, out var l)) return l;
        if (_cache.Count > 300) Clear();
        l = c.DWrite.CreateTextLayout(text, fmt, maxWidth > 0 ? maxWidth : 100000, 100000);
        l.WordWrapping = maxWidth > 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;
        if (center) l.TextAlignment = TextAlignment.Center;
        if (lineHeight > 0) l.SetLineSpacing(LineSpacingMethod.Uniform, size * lineHeight, size * lineHeight * 0.8f);
        if (track != 0)
            using (var l1 = l.QueryInterfaceOrNull<IDWriteTextLayout1>())
                l1?.SetCharacterSpacing(0, track * size, 0, new TextRange(0, (uint)text.Length));
        _cache[key] = l;
        return l;
    }

    public void Clear()
    {
        foreach (var l in _cache.Values) l.Dispose();
        _cache.Clear();
    }

    public void Dispose() => Clear();
}

/// <summary>Die kleinen Strich-Symbole aus layout.js (viewBox 24×24), als Direct2D-Pfade; einmal je Fabrik gebaut.</summary>
internal static class UiIcons
{
    private static string Circle(float cx, float cy, float r) =>
        FormattableString.Invariant($"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["music"] = "M9 17.5V6.3l10-2.1v11.3" + Circle(6.5f, 17.5f, 2.5f) + Circle(16.5f, 15.5f, 2.5f),
        ["water"] = "M12 3.4c-2.4 3.4-6 7.5-6 11a6 6 0 0 0 12 0c0-3.5-3.6-7.6-6-11z",
        ["timer"] = Circle(12, 13.2f, 7.3f) + "M12 9.4v3.8l2.5 1.5M9.6 3h4.8",
        ["prayer"] = "M19.4 14.7A7.9 7.9 0 0 1 9.3 4.6a7.9 7.9 0 1 0 10.1 10.1z",
        ["sound"] = "M6.2 9v6M12 6v12M17.8 8.4v7.2",
        ["workout"] = "M8.2 12h7.6M5.6 7.6v8.8M8.2 6v12M15.8 6v12M18.4 7.6v8.8M3.2 10.2v3.6M20.8 10.2v3.6",
        ["weather"] = "M7.2 18.6h9.9a3.9 3.9 0 0 0 .5-7.8 5.4 5.4 0 0 0-10.4-.9 4.4 4.4 0 0 0 0 8.7z",
        ["minus"] = "M7 12h10",
        ["plus"] = "M12 6.5v11M6.5 12h11",
    };

    private static readonly object Lock = new();
    private static IntPtr _factory;
    private static readonly Dictionary<string, ID2D1PathGeometry> Cache = new();
    private static ID2D1StrokeStyle? _round, _dashed;

    /// <summary>Symbol zeichnen: Mitte, Kantenlänge in Pixeln, Strichstärke in viewBox-Einheiten, Drehung in Grad.</summary>
    public static void Draw(ID2D1DeviceContext ctx, ID2D1Factory1 f, string name, Vector2 centre, float size, ID2D1Brush brush,
        float stroke = 1.6f, float rotateDeg = 0)
    {
        var g = Get(f, name);
        if (g == null) return;
        var prev = ctx.Transform;
        float s = size / 24;
        ctx.Transform = Matrix3x2.CreateTranslation(-12, -12) * Matrix3x2.CreateRotation(rotateDeg * MathF.PI / 180)
            * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(centre) * prev;
        ctx.DrawGeometry(g, brush, stroke, _round);
        ctx.Transform = prev;
    }

    /// <summary>Gestrichelte Linie (border: dashed).</summary>
    public static ID2D1StrokeStyle Dashed(ID2D1Factory1 f)
    {
        lock (Lock)
        {
            Check(f);
            return _dashed ??= f.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Flat, EndCap = CapStyle.Flat, DashCap = CapStyle.Flat,
                LineJoin = LineJoin.Round, MiterLimit = 10, DashStyle = DashStyle.Custom,
            }, new[] { 3f, 2.2f });
        }
    }

    private static void Check(ID2D1Factory1 f)
    {
        if (_factory == f.NativePointer) return;
        foreach (var g in Cache.Values) g.Dispose();
        Cache.Clear();
        _round?.Dispose(); _dashed?.Dispose();
        _round = _dashed = null;
        _factory = f.NativePointer;
    }

    private static ID2D1PathGeometry? Get(ID2D1Factory1 f, string name)
    {
        lock (Lock)
        {
            Check(f);
            _round ??= f.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round, MiterLimit = 10,
            });
            if (Cache.TryGetValue(name, out var g)) return g;
            if (!Paths.TryGetValue(name, out var d)) return null;
            g = f.CreatePathGeometry();
            using (var sink = g.Open())
            {
                Build(sink, d);
                sink.Close();
            }
            Cache[name] = g;
            return g;
        }
    }

    /// <summary>Ein kleiner SVG-Pfad-Leser (M L H V C A Z, groß und klein, mit Wiederholung).</summary>
    private static void Build(ID2D1GeometrySink sink, string d)
    {
        int i = 0;
        char cmd = ' ';
        Vector2 cur = default, start = default;
        bool open = false;
        while (true)
        {
            Skip(d, ref i);
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var p = new Vector2(Num(d, ref i), Num(d, ref i));
                    if (rel) p += cur;
                    if (open) sink.EndFigure(FigureEnd.Open);
                    sink.BeginFigure(p, FigureBegin.Hollow);
                    open = true;
                    cur = start = p;
                    cmd = rel ? 'l' : 'L';   // weitere Paare sind Linien
                    break;
                }
                case 'L':
                {
                    var p = new Vector2(Num(d, ref i), Num(d, ref i));
                    if (rel) p += cur;
                    sink.AddLine(p); cur = p;
                    break;
                }
                case 'H':
                {
                    float x = Num(d, ref i);
                    cur = new Vector2(rel ? cur.X + x : x, cur.Y);
                    sink.AddLine(cur);
                    break;
                }
                case 'V':
                {
                    float y = Num(d, ref i);
                    cur = new Vector2(cur.X, rel ? cur.Y + y : y);
                    sink.AddLine(cur);
                    break;
                }
                case 'C':
                {
                    var p1 = new Vector2(Num(d, ref i), Num(d, ref i));
                    var p2 = new Vector2(Num(d, ref i), Num(d, ref i));
                    var p3 = new Vector2(Num(d, ref i), Num(d, ref i));
                    if (rel) { p1 += cur; p2 += cur; p3 += cur; }
                    sink.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                    cur = p3;
                    break;
                }
                case 'A':
                {
                    float rx = Num(d, ref i), ry = Num(d, ref i), rot = Num(d, ref i);
                    bool large = Num(d, ref i) != 0, sweep = Num(d, ref i) != 0;
                    var p = new Vector2(Num(d, ref i), Num(d, ref i));
                    if (rel) p += cur;
                    sink.AddArc(new ArcSegment
                    {
                        Point = p, Size = new Vortice.Mathematics.Size(rx, ry), RotationAngle = rot,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
                        ArcSize = large ? ArcSize.Large : ArcSize.Small,
                    });
                    cur = p;
                    break;
                }
                case 'Z':
                    if (open) { sink.EndFigure(FigureEnd.Closed); open = false; }
                    cur = start;
                    cmd = ' ';
                    break;
                default:
                    i++;
                    break;
            }
        }
        if (open) sink.EndFigure(FigureEnd.Open);
    }

    private static void Skip(string d, ref int i)
    {
        while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
    }

    private static float Num(string d, ref int i)
    {
        Skip(d, ref i);
        int s = i;
        if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
        bool dot = false;
        while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.' && !dot))
        {
            if (d[i] == '.') dot = true;
            i++;
        }
        return s == i ? 0 : float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
