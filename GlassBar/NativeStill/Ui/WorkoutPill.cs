using System.Numerics;
using GlassBar.NativeStill.Ui.Workout;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui;

// =====================================================================================================
// Die Trainings-Pille (workout.js): die letzten sieben Tage als Punkte, geöffnet der Vorschlag für heute, jede
// andere Wahl einen Klick entfernt, und der Kalender. Eine Einheit übernimmt die Mitte des Bildschirms
// (WorkoutStage). Ein Vorschlag, auch eine Pause, hält nie etwas auf.
// =====================================================================================================

/// <summary>Ein Drehrad zum Auswählen (wie S.Wheel): Mausrad, Ziehen oder Klick oben/unten.</summary>
internal sealed class WoWheel
{
    public readonly string[] Items;
    public readonly string Ghost;
    public int Index;
    public float Pos;
    public Rect R;
    private bool _drag, _moved;
    private float _dragY, _rowH = 1;
    private int _dragIdx;

    public WoWheel(string[] items, string ghost) { Items = items; Ghost = ghost; }

    public void To(int i) { Index = Math.Clamp(i, 0, Items.Length - 1); Pos = Index; }
    public bool Animating => _drag || MathF.Abs(Pos - Index) > 0.002f;
    public void Step(double dt) { Pos = Wo.Approach(Pos, Index, dt, 18f); if (MathF.Abs(Pos - Index) < 0.002f) Pos = Index; }

    public static float Height(float px) => px * 1.2f * 3;

    public void Draw(WorkoutPaint p, UiContext c, float x, float y, float w, float px, float a)
    {
        float rowH = px * 1.2f;
        if (p.Measuring) return;
        _rowH = rowH;
        R = new Rect(x, y, w, rowH * 3);
        var ctx = p.Ctx;
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(R.X, R.Y, R.X + R.Width, R.Y + R.Height), AntialiasMode.Aliased);
        float cy = y + rowH * 1.5f;
        int lo = Math.Max(0, (int)MathF.Floor(Pos) - 2), hi = Math.Min(Items.Length - 1, (int)MathF.Ceiling(Pos) + 2);
        for (int i = lo; i <= hi; i++)
        {
            float d = i - Pos;
            float al = Math.Clamp(1 - MathF.Abs(d) * 0.62f, 0, 1);
            if (al <= 0.01f) continue;
            var l = p.Text(Items[i], px, 650);
            p.Draw(l, x + (w - WorkoutPaint.W(l)) / 2, cy + d * rowH - WorkoutPaint.H(l) / 2, c.Fg, al * al * a);
        }
        ctx.PopAxisAlignedClip();
    }

    public bool Contains(Vector2 q) => q.X >= R.X && q.X < R.X + R.Width && q.Y >= R.Y && q.Y < R.Y + R.Height;

    /// <summary>true = verarbeitet.</summary>
    public bool Pointer(PointerEvent e)
    {
        switch (e.Kind)
        {
            case PointerKind.Wheel when Contains(e.Pos):
                if (e.Wheel != 0) Index = Math.Clamp(Index - Math.Sign(e.Wheel), 0, Items.Length - 1);
                return true;
            case PointerKind.Down when e.Button == 0 && Contains(e.Pos):
                _drag = true; _moved = false; _dragY = e.Pos.Y; _dragIdx = Index;
                return true;
            case PointerKind.Move when _drag:
                float dy = _dragY - e.Pos.Y;
                if (MathF.Abs(dy) > 3) _moved = true;
                if (_moved) Index = Math.Clamp(_dragIdx + (int)MathF.Round(dy / _rowH), 0, Items.Length - 1);
                return true;
            case PointerKind.Up when _drag:
                _drag = false;
                if (!_moved && Contains(e.Pos))
                {
                    // Klick oben: eins zurück, unten: eins weiter.
                    float t = (e.Pos.Y - R.Y) / R.Height;
                    if (t < 1 / 3f) Index = Math.Max(0, Index - 1);
                    else if (t > 2 / 3f) Index = Math.Min(Items.Length - 1, Index + 1);
                }
                return true;
            case PointerKind.Leave:
                _drag = false;
                return false;
        }
        return false;
    }
}

public sealed class WorkoutPill : Pill
{
    public override string Id => "workout";

    /// <summary>Die Bühne der geführten Einheit (als Überlagerung zu registrieren).</summary>
    public WorkoutStage Stage { get; } = new();

    private const float CardW = 348, CardTop = 58, CardPad = 16;

    private readonly WorkoutPaint _p = new();
    private readonly WorkoutBody _body = new();
    private readonly List<Hit> _hits = new();
    private Rect _pressed;
    private bool _hasPressed;
    private float _viewH = 200;
    private UiContext? _c;

    // Karte
    private string _page;
    private string? _chosen;
    private string _chosenDay = "";
    private string _cardNote = "";
    private long _cardNoteUntil;
    private readonly List<WoWheel> _wheels = new();
    private (int Y, int M)? _calMonth;
    private string? _picked;

    public WorkoutPill()
    {
        _page = Stage.Data.Setup ? "main" : "level";
        Go(_page);
    }

    public override bool Shown(UiContext c) => c.Settings.Bool("workout") && !Stage.On;
    public override Vector2 CompactSize(UiContext c) => new(120 * c.K, 40 * c.K);
    public override Vector2 CardSize(UiContext c) => new(CardW * c.K, MathF.Ceiling(CardTop * c.K + _viewH + CardPad * c.K));
    public override bool Animating
    {
        get
        {
            foreach (var w in _wheels) if (w.Animating) return true;
            return false;
        }
    }

    /* ---------- Ansichts-Zustand (nur bei Änderungen neu gebaut) ---------- */

    private bool _dirty = true;
    private int _seenVersion = -1;
    private string _seenFocus = "", _seenDay = "";
    private double _seenGoal = -1, _seenRest = -1;
    private bool _seenTable, _seenBmi;
    private long _rebuiltAt;

    private sealed class ExRow { public string Name = "", Amount = ""; }

    private Recommendation? _rec;
    private string? _shown;
    private bool _gentle, _own;
    private string _kicker = "", _planName = "", _mins = "", _why = "", _altLabel = "", _meta = "", _title = "Training";
    private readonly List<ExRow> _rows = new();
    private Works? _cardWorks;
    private string? _bmiBold, _bmiRest;
    private readonly bool[] _dotOn = new bool[7];
    private bool _dotRest;

    // Kalender
    private string _monthTitle = "";
    private int _monthDays, _monthLead;
    private bool _monthIsNow;
    private readonly HashSet<int> _doneDays = new();
    private int _todayInMonth, _pickedInMonth;
    private readonly List<(string Head, string Names)> _entries = new();
    private string _calHint = "";

    private static readonly string[] Months = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
    private static readonly string[] Dow = ["M", "D", "M", "D", "F", "S", "S"];

    private void Rebuild(UiContext c)
    {
        _dirty = false;
        _rebuiltAt = WorkoutPlan.NowMs();
        var data = Stage.Data;
        string today = WorkoutPlan.DayKey(DateTime.Now);
        _rec = Stage.Suggestion();

        // Punkte: die letzten sieben Tage
        var days = new HashSet<string>(data.History.Select(e => e.Day));
        for (int i = 0; i < 7; i++) _dotOn[i] = days.Contains(WorkoutPlan.AddDays(today, i - 6));
        _dotRest = _rec.Kind == "rest";
        int tn = WorkoutPlan.DayNum(today);
        int n = days.Count(d => tn - WorkoutPlan.DayNum(d) is >= 0 and <= 6);
        _meta = $"{n} von {(int)Math.Round(c.Settings.Num("workoutGoal"))} diese Woche";
        _title = _page == "history" ? "Verlauf" : _page == "main" ? "Training" : _page == "body" && data.Setup ? "Dein BMI" : "Training einrichten";

        if (_page == "main") RebuildMain(c, today);
        else if (_page == "history") RebuildHistory(today);
    }

    private void RebuildMain(UiContext c, string today)
    {
        if (_chosenDay != today) { _chosen = null; _chosenDay = today; }
        var rec = _rec!;
        var data = Stage.Data;
        _shown = _chosen ?? rec.Plan;
        _rows.Clear();
        if (_shown != null)
        {
            _own = _chosen != null && _chosen != rec.Plan;
            string note = _own ? WorkoutPlan.NoteFor(_chosen!, data.History, WorkoutPlan.NowMs()) : "";
            _why = _own
                ? (note.Length > 0 ? note : rec.Kind == "rest" ? "Deine Wahl. Der heutige Vorschlag war Pause." : $"Deine Wahl. Der heutige Vorschlag war {WorkoutPlan.Plans[rec.Plan!].Label}.")
                : rec.Reason;
            _gentle = !_own && rec.Gentle;
            _kicker = _own ? "Deine Wahl" : "Heute vorgeschlagen";
            _planName = WorkoutPlan.Plans[_shown].Label;
            var steps = WorkoutPlan.BuildSession(_shown, data.Targets, Stage.Options(_gentle));
            var seen = new List<string>();
            foreach (var s in steps)
            {
                if (s.K != "work" || seen.Contains(s.M)) continue;
                seen.Add(s.M);
                var m = WorkoutPlan.Moves[s.M];
                var r = m.Rungs[s.Rung];
                _rows.Add(new ExRow { Name = r.Name, Amount = $"{s.Sets} × {Wo.Amount(m, s.N)}{(r.Sides ? " je Seite" : "")}" });
            }
            _mins = $"etwa {WorkoutPlan.SessionMinutes(steps)} Min.";
            var works = WorkoutBody.MusclesFor(seen);
            if (_cardWorks == null || _cardWorks.Key != works.Key) _cardWorks = works;
        }
        else
        {
            _kicker = "Heute vorgeschlagen";
            _planName = "Pause";
            _why = rec.Reason;
            var steps = WorkoutPlan.BuildSession(rec.Alt, data.Targets, Stage.Options(false));
            _altLabel = $"Trotzdem trainieren: {WorkoutPlan.Plans[rec.Alt].Label}, {WorkoutPlan.SessionMinutes(steps)} Min.";
        }
        var bmi = c.Settings.Bool("workoutBmi") && data.Body != null ? WorkoutPlan.Bmi(data.Body) : null;
        if (bmi is { } b)
        {
            _bmiBold = "BMI " + b.Value.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
            _bmiRest = " · " + b.Label;
        }
        else _bmiBold = _bmiRest = null;
    }

    private void RebuildHistory(string today)
    {
        var now = DateTime.Now;
        var (y, mo) = _calMonth ?? (now.Year, now.Month);
        _calMonth = (y, mo);
        _monthTitle = $"{Months[mo - 1]} {y}";
        _monthDays = DateTime.DaysInMonth(y, mo);
        _monthLead = ((int)new DateTime(y, mo, 1).DayOfWeek + 6) % 7; // Montag zuerst
        _monthIsNow = y == now.Year && mo == now.Month;
        _todayInMonth = _monthIsNow ? now.Day : 0;
        _doneDays.Clear();
        int total = 0;
        string prefix = $"{y:0000}-{mo:00}-";
        foreach (var e in Stage.Data.History)
            if (e.Day.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(e.Day.AsSpan(8), out int d))
            {
                _doneDays.Add(d);
                total++;
            }
        _pickedInMonth = _picked != null && _picked.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(_picked.AsSpan(8), out int pd) ? pd : 0;
        _entries.Clear();
        if (_picked != null && Stage.Data.History.Any(e => e.Day == _picked))
        {
            foreach (var e in Stage.Data.History.Where(e => e.Day == _picked))
            {
                var names = e.Done.Where(x => WorkoutPlan.Moves.ContainsKey(x.M)).Select(x =>
                {
                    var m = WorkoutPlan.Moves[x.M];
                    return m.Rungs[Math.Clamp(x.Rung, 0, m.Rungs.Length - 1)].Name;
                });
                string label = WorkoutPlan.Plans.TryGetValue(e.Plan, out var pl) ? pl.Label : "Training";
                _entries.Add(($"{label} · {Math.Max(1, e.Mins)} Min.", string.Join(", ", names)));
            }
            _calHint = "";
        }
        else _calHint = total > 0 ? $"{total} Training{(total == 1 ? "" : "s")} diesen Monat. Klicke auf einen Tag, um ihn anzusehen." : "Keine Trainings diesen Monat.";
    }

    private void Go(string page)
    {
        _page = page;
        _wheels.Clear();
        var data = Stage.Data;
        if (page == "level")
        {
            var w = new WoWheel(Enumerable.Range(0, 61).Select(i => i == 60 ? "60+" : i.ToString()).ToArray(), "60+");
            w.To(data.PushUps ?? 5);
            _wheels.Add(w);
        }
        else if (page == "body")
        {
            var b = data.Body ?? new BodyData();
            bool ft = data.HUnit == "ft", lb = data.WUnit == "lb";
            var age = new WoWheel(Enumerable.Range(10, 91).Select(i => i.ToString()).ToArray(), "100");
            var h = new WoWheel(ft ? Enumerable.Range(48, 40).Select(Ft).ToArray() : Enumerable.Range(120, 101).Select(i => i.ToString()).ToArray(), ft ? "7′11″" : "220");
            var wg = new WoWheel((lb ? Enumerable.Range(66, 375) : Enumerable.Range(30, 171)).Select(i => i.ToString()).ToArray(), "440");
            age.To(b.Age - 10);
            h.To(ft ? (int)Math.Round(b.Cm / 2.54) - 48 : (int)Math.Round(b.Cm) - 120);
            wg.To(lb ? (int)Math.Round(b.Kg * 2.20462) - 66 : (int)Math.Round(b.Kg) - 30);
            _wheels.Add(age); _wheels.Add(h); _wheels.Add(wg);
        }
        _dirty = true;
    }

    private static string Ft(int inches) => $"{inches / 12}′{inches % 12}″";

    private BodyData ReadBody()
    {
        var data = Stage.Data;
        bool ft = data.HUnit == "ft", lb = data.WUnit == "lb";
        return new BodyData
        {
            Age = 10 + _wheels[0].Index,
            Cm = ft ? (48 + _wheels[1].Index) * 2.54 : 120 + _wheels[1].Index,
            Kg = lb ? (66 + _wheels[2].Index) / 2.20462 : 30 + _wheels[2].Index,
        };
    }

    /// <summary>Eine Meldung, wo sie gesehen wird: auf der Bühne oder unter der Karte.</summary>
    private void Say(string text)
    {
        if (Stage.Running) return; // die Bühne zeigt sie selbst
        _cardNote = text;
        _cardNoteUntil = WorkoutPlan.NowMs() + 4500;
    }

    /* ---------- Jedes Bild ---------- */

    public override void Update(UiContext c, double dt)
    {
        _c = c;
        Stage.Bind(c);
        var s = c.Settings;
        string focus = s.Str("workoutFocus"), today = WorkoutPlan.DayKey(DateTime.Now);
        double goal = s.Num("workoutGoal"), rest = s.Num("workoutRest");
        bool table = s.Bool("workoutTable"), bmi = s.Bool("workoutBmi");
        if (Stage.Version != _seenVersion || focus != _seenFocus || goal != _seenGoal || rest != _seenRest || table != _seenTable || bmi != _seenBmi || today != _seenDay)
        {
            _seenVersion = Stage.Version; _seenFocus = focus; _seenGoal = goal; _seenRest = rest; _seenTable = table; _seenBmi = bmi; _seenDay = today;
            _dirty = true;
        }
        // Erholung zählt in Stunden: der Vorschlag wird jede Minute neu bedacht.
        if (WorkoutPlan.NowMs() - _rebuiltAt > 60000) _dirty = true;
        if (_cardNote.Length > 0 && WorkoutPlan.NowMs() > _cardNoteUntil) _cardNote = "";
        if (_dirty) Rebuild(c);
        foreach (var w in _wheels) w.Step(dt);

        // Höhe der Karte messen (ohne zu zeichnen)
        _p.Prepare(c);
        _p.Measuring = true;
        _viewH = View(c, 0, 0, 316 * c.K, 1);
        _p.Measuring = false;
    }

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        _hits.Clear();
        _p.Begin(ctx, c);
        float k = c.K;
        var b = Bounds;
        var tp = Wo.Tp(c);
        var fg = c.Fg;
        float open = Math.Clamp(Open, 0, 1);
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(b.X, b.Y, b.X + b.Width, b.Y + b.Height), AntialiasMode.PerPrimitive);

        // Hantel
        float isz = (20 + 10 * open) * k, ip = (10 + 6 * open) * k;
        DrawDumbbell(new Vector2(b.X + ip, b.Y + ip), isz, tp);

        // Kompakt: die Punkte der letzten sieben Tage
        float oc = 1 - Wo.Smooth(0.02f, 0.35f, open);
        if (oc > 0.01f)
        {
            float x = b.X + (36 + 3 + 3) * k + (1 - oc) * 6 * k, cy = b.Y + 20 * k;
            for (int i = 0; i < 7; i++)
            {
                var pt = new Vector2(x + i * 10 * k, cy);
                bool now = i == 6 && !_dotOn[i];
                if (now) _p.Ring(pt, 3 * k, _dotRest ? fg : tp, (_dotRest ? 0.3f : 1f) * oc, 1.3f * k);
                else if (_dotOn[i])
                {
                    _p.Dot(pt, 5 * k, tp, 0.12f * oc);
                    _p.Dot(pt, 3 * k, tp, oc);
                }
                else _p.Dot(pt, 3 * k, fg, 0.18f * oc);
            }
        }

        // Karte
        float ok = Wo.Smooth(0.35f, 0.85f, open);
        if (ok > 0.01f)
        {
            float dy = (1 - ok) * 6 * k;
            float hx = b.X + 58 * k, hy = b.Y + 18 * k + dy;
            var title = _p.Text(_title, 14.5f * k, 600);
            _p.Draw(title, hx, hy, fg, 0.96f * ok);
            // „Beta“ neben dem Titel, auf jeder Ansicht
            var beta = _p.Text("BETA", 10 * k, 650);
            float bw = WorkoutPaint.W(beta) + 14 * k, bh = 17 * k;
            var br = new Rect(hx + WorkoutPaint.W(title) + 10 * k, hy + (WorkoutPaint.H(title) - bh) / 2, bw, bh);
            _p.Stroke(br, bh / 2, tp, 0.45f * ok, 1);
            _p.Draw(beta, br.X + 7 * k, br.Y + (bh - WorkoutPaint.H(beta)) / 2, tp, ok);
            if (_page == "main" || _page == "history")
            {
                var meta = _p.Text(_meta, 12 * k, 550);
                _p.Draw(meta, b.X + b.Width - 18 * k - WorkoutPaint.W(meta), hy + Baseline(title) - Baseline(meta), fg, 0.55f * ok);
            }
            View(c, b.X + 16 * k, b.Y + CardTop * k + dy, 316 * k, ok);
        }
        ctx.PopAxisAlignedClip();
    }

    private static float Baseline(IDWriteTextLayout l)
    {
        var lines = l.LineMetrics;
        return lines.Length > 0 ? lines[0].Baseline : l.Metrics.Height * 0.8f;
    }

    private void DrawDumbbell(Vector2 o, float size, Vector3 tp)
    {
        float s = size / 24f, w = 2 * s;
        Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
        _p.Line(P(8.2f, 12), P(15.8f, 12), tp, 1, w);
        _p.Line(P(5.6f, 7.6f), P(5.6f, 16.4f), tp, 1, w);
        _p.Line(P(8.2f, 6), P(8.2f, 18), tp, 1, w);
        _p.Line(P(15.8f, 6), P(15.8f, 18), tp, 1, w);
        _p.Line(P(18.4f, 7.6f), P(18.4f, 16.4f), tp, 1, w);
        _p.Line(P(3.2f, 10.2f), P(3.2f, 13.8f), tp, 1, w);
        _p.Line(P(20.8f, 10.2f), P(20.8f, 13.8f), tp, 1, w);
    }

    /* ---------- Ansichten (messen und zeichnen in einem) ---------- */

    private const int CLevelNext = 1, CFocus = 2, CFocusNext = 3, CUnit = 4, CBodySkip = 5, CBodySave = 6, CStart = 7, CStartAlt = 8,
        CChip = 9, CHistory = 10, CBmi = 11, CExt = 12, CBack = 13, CMonth = 14, CDay = 15;

    private bool Hover(UiContext c, Rect r) =>
        !_p.Measuring && c.PointerInside && c.Pointer.X >= r.X && c.Pointer.X < r.X + r.Width && c.Pointer.Y >= r.Y && c.Pointer.Y < r.Y + r.Height;

    private void AddHit(Rect r, int cmd, int arg = 0)
    {
        if (!_p.Measuring) _hits.Add(new Hit(r, cmd, arg));
    }

    /// <summary>Zeichnet die aktuelle Ansicht ab (x, y) in Breite w; gibt die Höhe zurück.</summary>
    private float View(UiContext c, float x, float y, float w, float a)
    {
        float gap = 10 * c.K;
        float h = _page switch
        {
            "level" => ViewLevel(c, x, y, w, a, gap),
            "focus" => ViewFocus(c, x, y, w, a, gap),
            "body" => ViewBody(c, x, y, w, a, gap),
            "history" => ViewHistory(c, x, y, w, a, gap),
            _ => ViewMain(c, x, y, w, a, gap),
        };
        return Math.Max(0, h);
    }

    private float Para(UiContext c, string text, float px, int weight, float alpha, float x, float y, float w, float a)
    {
        var l = _p.Text(text, px, weight, false, w, WorkoutPaint.Wrap);
        _p.Draw(l, x, y, c.Fg, alpha * a);
        return WorkoutPaint.H(l);
    }

    /// <summary>tp-btn: primary (getönt) oder ghost.</summary>
    private float Button(UiContext c, Rect r, string text, bool primary, float a, int cmd, int arg = 0)
    {
        float k = c.K;
        var tp = Wo.Tp(c);
        bool hov = Hover(c, r);
        if (primary)
        {
            _p.Fill(r, r.Height / 2, tp, (hov ? 0.34f : 0.24f) * a);
            _p.Stroke(r, r.Height / 2, tp, 0.35f * a, 1);
        }
        else _p.Fill(r, r.Height / 2, c.Fg, (hov ? 0.14f : 0.08f) * a);
        var l = _p.Text(text, 13 * k, 600, false, r.Width - 16 * k, WorkoutPaint.Ellipsis);
        _p.Draw(l, r.X + (r.Width - WorkoutPaint.W(l)) / 2, r.Y + (r.Height - WorkoutPaint.H(l)) / 2, c.Fg, (primary || hov ? 1f : 0.75f) * a);
        AddHit(r, cmd, arg);
        return r.Height;
    }

    private float ViewLevel(UiContext c, float x, float y0, float w, float a, float gap)
    {
        float k = c.K, y = y0;
        y += Para(c, "Wie viele Liegestütze schaffst du am Stück?", 17 * k, 650, 1, x, y, w, a) + gap;
        y += Para(c, "Eine grobe Schätzung genügt. Sie legt fest, wo jede Übung beginnt, und passt sich dann an.", 12 * k, 500, 0.58f, x, y, w, a) + gap;
        var wh = _wheels[0];
        float ww = WorkoutPaint.W(_p.Text(wh.Ghost, 40 * k, 650)) + 40 * k;
        wh.Draw(_p, c, x + (w - ww) / 2, y, ww, 40 * k, a);
        y += WoWheel.Height(40 * k) + gap;
        y += Button(c, new Rect(x, y, w, 36 * k), "Weiter", true, a, CLevelNext);
        return y - y0;
    }

    private float ViewFocus(UiContext c, float x, float y0, float w, float a, float gap)
    {
        float k = c.K, y = y0;
        y += Para(c, "Worauf möchtest du dich konzentrieren?", 17 * k, 650, 1, x, y, w, a) + gap;
        string focus = Stage.Focus;
        y += Option(c, x, y, w, a, focus == "muscle", "Muskelaufbau", "Muskeln aufbauen: 8 bis 20 Wiederholungen, nah am Muskelversagen.", 0) + gap;
        y += Option(c, x, y, w, a, focus == "strength", "Kraft", "Stärker werden: 3 bis 8 Wiederholungen, schwerere Varianten.", 1) + gap;
        y += Para(c, "Brich jede Übung ab, die stechende Schmerzen verursacht. Muskelkater am nächsten Tag ist normal.", 12 * k, 500, 0.58f, x, y, w, a) + gap;
        y += Button(c, new Rect(x, y, w, 36 * k), "Weiter", true, a, CFocusNext);
        return y - y0;
    }

    private float Option(UiContext c, float x, float y, float w, float a, bool on, string name, string line, int arg)
    {
        float k = c.K, px = 14 * k, py = 10 * k;
        var l1 = _p.Text(name, 14 * k, 650, false, w - 2 * px, WorkoutPaint.Wrap);
        var l2 = _p.Text(line, 12 * k, 500, false, w - 2 * px, WorkoutPaint.Wrap);
        float h = py * 2 + WorkoutPaint.H(l1) + 2 * k + WorkoutPaint.H(l2);
        var r = new Rect(x, y, w, h);
        var tp = Wo.Tp(c);
        if (on)
        {
            _p.Fill(r, 14 * k, tp, 0.2f * a);
            _p.Stroke(r, 14 * k, tp, 0.45f * a, 1);
        }
        else _p.Fill(r, 14 * k, c.Fg, (Hover(c, r) ? 0.12f : 0.07f) * a);
        _p.Draw(l1, x + px, y + py, c.Fg, a);
        _p.Draw(l2, x + px, y + py + WorkoutPaint.H(l1) + 2 * k, c.Fg, 0.62f * a);
        AddHit(r, CFocus, arg);
        return h;
    }

    private float ViewBody(UiContext c, float x, float y0, float w, float a, float gap)
    {
        float k = c.K, y = y0;
        var data = Stage.Data;
        y += Para(c, data.Setup ? "Werte aktualisieren" : "Möchtest du deinen BMI sehen?", 17 * k, 650, 1, x, y, w, a) + gap;
        y += Para(c, "Optional und bleibt auf diesem PC. Er unterscheidet nicht zwischen Muskeln und Fett und kann daher steigen, wenn du stärker wirst.", 12 * k, 500, 0.58f, x, y, w, a) + gap;
        float colW = w / 3, px = 26 * k;
        string[] labels = ["Alter", "Größe", "Gewicht"];
        float colH = 0;
        for (int i = 0; i < 3; i++)
        {
            float cx = x + i * colW, cy = y;
            _wheels[i].Draw(_p, c, cx + 4 * k, cy, colW - 8 * k, px, a);
            cy += WoWheel.Height(px) + 4 * k;
            var lab = _p.Text(labels[i], 11.5f * k, 600);
            _p.Draw(lab, cx + (colW - WorkoutPaint.W(lab)) / 2, cy, c.Fg, 0.6f * a);
            cy += WorkoutPaint.H(lab) + 4 * k;
            if (i > 0)
            {
                string u1 = i == 1 ? "cm" : "kg", u2 = i == 1 ? "ft" : "lb";
                string cur = i == 1 ? data.HUnit : data.WUnit;
                var l1 = _p.Text(u1, 11 * k, 600);
                var l2 = _p.Text(u2, 11 * k, 600);
                float w1 = WorkoutPaint.W(l1) + 16 * k, w2 = WorkoutPaint.W(l2) + 16 * k;
                float ux = cx + (colW - w1 - w2 - 3 * k) / 2;
                for (int j = 0; j < 2; j++)
                {
                    var l = j == 0 ? l1 : l2;
                    float bw = j == 0 ? w1 : w2;
                    var r = new Rect(ux, cy, bw, 22 * k);
                    bool on = cur == (j == 0 ? u1 : u2), hov = Hover(c, r);
                    if (on) _p.Fill(r, 11 * k, c.Fg, 0.14f * a);
                    _p.Draw(l, ux + 8 * k, cy + (22 * k - WorkoutPaint.H(l)) / 2, c.Fg, (on || hov ? 1f : 0.5f) * a);
                    AddHit(r, CUnit, (i - 1) * 2 + j);
                    ux += bw + 3 * k;
                }
            }
            cy += 22 * k;
            colH = Math.Max(colH, cy - y);
        }
        y += colH + gap;
        string ghost = data.Setup ? "Zurück" : "Überspringen";
        float gw = WorkoutPaint.W(_p.Text(ghost, 13 * k, 600)) + 32 * k;
        Button(c, new Rect(x, y, gw, 36 * k), ghost, false, a, CBodySkip);
        Button(c, new Rect(x + gw + 8 * k, y, w - gw - 8 * k, 36 * k), "Speichern", true, a, CBodySave);
        y += 36 * k;
        return y - y0;
    }

    private float ViewMain(UiContext c, float x, float y0, float w, float a, float gap)
    {
        float k = c.K, y = y0;
        var fg = c.Fg;
        var tp = Wo.Tp(c);

        // Vorschlag: Art, Name, Dauer
        var kick = _p.Text(_kicker, 11.5f * k, 600);
        _p.Draw(kick, x, y, fg, 0.55f * a);
        y += WorkoutPaint.H(kick) + 1 * k;
        var name = _p.Text(_planName, 24 * k, 650);
        _p.Draw(name, x, y, fg, a);
        if (_shown != null)
        {
            var mins = _p.Text(_mins, 12 * k, 550);
            _p.Draw(mins, x + w - WorkoutPaint.W(mins), y + Baseline(name) - Baseline(mins), fg, 0.55f * a);
        }
        y += WorkoutPaint.H(name) + gap - 6 * k;
        y += Para(c, _why, 12.5f * k, 500, 0.72f, x, y, w, a) + gap;

        if (_shown != null)
        {
            // Körper links, Übungen rechts
            float bodyH = 150 * k, bodyW = WorkoutBody.PairWidth(bodyH, 2 * k);
            float lx = x + bodyW + 12 * k, lw = w - bodyW - 12 * k;
            float rowH = 0;
            var rowLayouts = 0f;
            foreach (var r in _rows) rowLayouts += WorkoutPaint.H(_p.Text(r.Amount, 12.5f * k, 500)) + 4 * k;
            float listH = Math.Max(0, rowLayouts - 4 * k);
            float splitH = Math.Max(bodyH, listH);
            if (!_p.Measuring && _cardWorks != null)
                _body.DrawPair(_p.Ctx, c, x, y + (splitH - bodyH) / 2, bodyH, 2 * k, _cardWorks, false, -1, a);
            float ly = y + (splitH - listH) / 2;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                var amt = _p.Text(r.Amount, 12.5f * k, 500);
                rowH = WorkoutPaint.H(amt);
                float aw = WorkoutPaint.W(amt);
                float es = 13.5f * k * 1.45f;
                float nameMax = Math.Max(20 * k, lw - aw - es - 10 * k);
                var nl = _p.Text(r.Name, 12.5f * k, 500, false, nameMax, WorkoutPaint.Ellipsis);
                _p.Draw(nl, lx, ly, fg, 0.85f * a);
                var er = new Rect(lx + Math.Min(WorkoutPaint.W(nl), nameMax) + 1 * k, ly + (rowH - es) / 2, es, es);
                bool hov = Hover(c, er);
                if (hov) _p.Fill(er, es * 0.28f, fg, 0.12f * a);
                if (!_p.Measuring) _p.ExtIcon(er, fg, (hov ? 1f : 0.5f) * a);
                AddHit(er, CExt, i);
                _p.Draw(amt, lx + lw - aw, ly, fg, 0.55f * a);
                ly += rowH + 4 * k;
            }
            y += splitH + gap;
            y += Button(c, new Rect(x, y, w, 36 * k), "Start", true, a, CStart) + gap;
        }
        else
        {
            y += Button(c, new Rect(x, y, w, 36 * k), _altLabel, false, a, CStartAlt) + gap;
        }

        // Jede andere Wahl
        float chipH = 28 * k, cx = x, cy = y;
        for (int i = 0; i < WorkoutPlan.PlanIds.Length; i++)
        {
            string id = WorkoutPlan.PlanIds[i];
            bool on = _shown == id, sugg = _rec?.Plan == id;
            var l = _p.Text(WorkoutPlan.Plans[id].Label, 12 * k, 600);
            float cw = WorkoutPaint.W(l) + 22 * k + (sugg ? 11 * k : 0);
            if (cx > x && cx + cw > x + w) { cx = x; cy += chipH + 5 * k; }
            var r = new Rect(cx, cy, cw, chipH);
            bool hov = Hover(c, r);
            if (on)
            {
                _p.Fill(r, chipH / 2, tp, 0.26f * a);
                _p.Stroke(r, chipH / 2, tp, 0.5f * a, 1);
            }
            else _p.Fill(r, chipH / 2, fg, (hov ? 0.14f : 0.08f) * a);
            _p.Draw(l, cx + 11 * k, cy + (chipH - WorkoutPaint.H(l)) / 2, fg, (on || hov ? 1f : 0.72f) * a);
            // Ein Punkt markiert den Vorschlag, was auch immer gewählt ist.
            if (sugg) _p.Dot(new Vector2(cx + 11 * k + WorkoutPaint.W(l) + 8.5f * k, cy + chipH / 2), 2.5f * k, tp, a);
            AddHit(r, CChip, i);
            cx += cw + 5 * k;
        }
        y = cy + chipH + gap;

        if (_cardNote.Length > 0) y += Para(c, _cardNote, 12 * k, 500, 0.58f, x, y, w, a) + gap;

        // Fuß: Verlauf, BMI
        var hist = _p.Text("Verlauf", 12 * k, 600);
        float fh = Math.Max(22 * k, WorkoutPaint.H(hist) + 6 * k);
        var hr = new Rect(x, y, WorkoutPaint.W(hist), fh);
        _p.Draw(hist, x, y + (fh - WorkoutPaint.H(hist)) / 2, fg, (Hover(c, hr) ? 1f : 0.62f) * a);
        AddHit(hr, CHistory);
        if (_bmiBold != null && _bmiRest != null)
        {
            float maxW = w - hr.Width - 10 * k;
            var lb = _p.Text(_bmiBold, 12 * k, 650);
            var lr = _p.Text(_bmiRest, 12 * k, 500, false, Math.Max(10 * k, maxW - WorkoutPaint.W(lb)), WorkoutPaint.Ellipsis);
            float tw = WorkoutPaint.W(lb) + Math.Min(WorkoutPaint.W(lr), Math.Max(10 * k, maxW - WorkoutPaint.W(lb)));
            var br = new Rect(x + w - tw, y, tw, fh);
            bool hov = Hover(c, br);
            float ty = y + (fh - WorkoutPaint.H(lb)) / 2;
            _p.Draw(lb, br.X, ty, fg, (hov ? 1f : 0.85f) * a);
            _p.Draw(lr, br.X + WorkoutPaint.W(lb), ty, fg, (hov ? 1f : 0.62f) * a);
            AddHit(br, CBmi);
        }
        y += fh;
        return y - y0;
    }

    private float ViewHistory(UiContext c, float x, float y0, float w, float a, float gap)
    {
        float k = c.K, y = y0;
        var fg = c.Fg;
        var tp = Wo.Tp(c);

        var back = _p.Text("‹ Zurück zu heute", 12 * k, 600);
        float fh = Math.Max(22 * k, WorkoutPaint.H(back) + 6 * k);
        var backR = new Rect(x, y, WorkoutPaint.W(back), fh);
        _p.Draw(back, x, y + (fh - WorkoutPaint.H(back)) / 2, fg, (Hover(c, backR) ? 1f : 0.62f) * a);
        AddHit(backR, CBack);
        y += fh + gap;

        // Monat mit Pfeilen
        float nb = 28 * k;
        var title = _p.Text(_monthTitle, 13.5f * k, 600);
        for (int j = 0; j < 2; j++)
        {
            if (j == 1 && _monthIsNow) continue;
            var r = new Rect(j == 0 ? x : x + w - nb, y, nb, nb);
            _p.Fill(r, nb / 2, fg, (Hover(c, r) ? 0.14f : 0.08f) * a);
            var arrow = _p.Text(j == 0 ? "‹" : "›", 16 * k, 500);
            _p.Draw(arrow, r.X + (nb - WorkoutPaint.W(arrow)) / 2, r.Y + (nb - WorkoutPaint.H(arrow)) / 2, fg, 0.8f * a);
            AddHit(r, CMonth, j == 0 ? -1 : 1);
        }
        _p.Draw(title, x + (w - WorkoutPaint.W(title)) / 2, y + (nb - WorkoutPaint.H(title)) / 2, fg, a);
        y += nb + gap;

        // Raster: Montag zuerst
        float colW = w / 7, cell = 32 * k, rg = 3 * k;
        for (int i = 0; i < 7; i++)
        {
            var l = _p.Text(Dow[i], 10.5f * k, 600);
            _p.Draw(l, x + i * colW + (colW - WorkoutPaint.W(l)) / 2, y, fg, 0.42f * a);
        }
        y += WorkoutPaint.H(_p.Text(Dow[0], 10.5f * k, 600)) + rg;
        int slots = _monthLead + _monthDays;
        int rows = (slots + 6) / 7;
        for (int d = 1; d <= _monthDays; d++)
        {
            int slot = _monthLead + d - 1;
            float cx = x + (slot % 7) * colW + (colW - cell) / 2, cy = y + (slot / 7) * (cell + rg);
            var r = new Rect(cx, cy, cell, cell);
            bool done = _doneDays.Contains(d), today = d == _todayInMonth;
            bool picked = d == _pickedInMonth;
            if (done) _p.Fill(r, cell / 2, tp, (Hover(c, r) ? 0.46f : 0.3f) * a);
            if (today) _p.Ring(new Vector2(cx + cell / 2, cy + cell / 2), cell / 2, fg, 0.5f * a, 1.3f * k);
            if (picked) _p.Ring(new Vector2(cx + cell / 2, cy + cell / 2), cell / 2, tp, a, 1.6f * k);
            var l = _p.Text(DayText(d), 12 * k, done ? 650 : 550);
            _p.Draw(l, cx + (cell - WorkoutPaint.W(l)) / 2, cy + (cell - WorkoutPaint.H(l)) / 2, fg, (done || today ? 1f : 0.5f) * a);
            if (done) AddHit(r, CDay, d);
        }
        y += rows * cell + (rows - 1) * rg + gap;

        if (_entries.Count == 0) y += Para(c, _calHint, 12 * k, 500, 0.62f, x, y, w, a);
        else
            for (int i = 0; i < _entries.Count; i++)
            {
                if (i > 0) y += gap;
                var (head, names) = _entries[i];
                var hl = _p.Text(head, 13 * k, 650, false, w, WorkoutPaint.Wrap);
                _p.Draw(hl, x, y, fg, a);
                y += WorkoutPaint.H(hl) + 2 * k;
                if (names.Length > 0) y += Para(c, names, 12 * k, 500, 0.62f, x, y, w, a);
            }
        return y - y0;
    }

    private static readonly string[] DayTexts = Enumerable.Range(0, 32).Select(i => i.ToString()).ToArray();
    private static string DayText(int d) => DayTexts[Math.Clamp(d, 0, 31)];

    /* ---------- Maus ---------- */

    public override bool OnPointer(UiContext c, PointerEvent e)
    {
        if (Open < 0.6f) return false;
        foreach (var w in _wheels)
            if (w.Pointer(e)) return true;
        Hit? at = null;
        foreach (var h in _hits) if (h.Contains(e.Pos)) { at = h; break; }
        switch (e.Kind)
        {
            case PointerKind.Down when e.Button == 0:
                _hasPressed = at != null;
                if (at is { } d) _pressed = d.R;
                return at != null;
            case PointerKind.Up when e.Button == 0:
                bool was = _hasPressed;
                _hasPressed = false;
                if (was && at is { } u && u.R.Equals(_pressed)) { Exec(c, u.Cmd, u.Arg); return true; }
                return at != null;
        }
        return false;
    }

    private void Exec(UiContext c, int cmd, int arg)
    {
        var data = Stage.Data;
        switch (cmd)
        {
            case CLevelNext:
                data.PushUps = _wheels[0].Index;
                data.Level = WorkoutPlan.LevelFor(_wheels[0].Index);
                Stage.Save();
                Go("focus");
                break;
            case CFocus:
                c.Settings.Set("workoutFocus", arg == 1 ? "strength" : "muscle");
                _dirty = true;
                break;
            case CFocusNext: Go("body"); break;
            case CUnit:
                data.Body = ReadBody();
                if (arg < 2) data.HUnit = arg == 0 ? "cm" : "ft";
                else data.WUnit = arg == 2 ? "kg" : "lb";
                Stage.Save();
                Go("body");
                break;
            case CBodySave:
                data.Body = ReadBody();
                data.Setup = true;
                Stage.Save();
                Go("main");
                break;
            case CBodySkip:
                data.Setup = true;
                Stage.Save();
                Go("main");
                break;
            case CStart:
                if (_shown != null) { Stage.Start(_shown, _gentle); _chosen = null; }
                break;
            case CStartAlt:
                if (_rec != null) { Stage.Start(_rec.Alt, false); _chosen = null; }
                break;
            case CChip:
            {
                string id = WorkoutPlan.PlanIds[arg];
                // Ein zweiter Klick auf die eigene Wahl führt zurück zum Vorschlag.
                _chosen = _chosen == id ? null : id == _rec?.Plan ? null : id;
                _chosenDay = WorkoutPlan.DayKey(DateTime.Now);
                _dirty = true;
                break;
            }
            case CHistory: _calMonth = null; _picked = null; Go("history"); break;
            case CBmi: Go("body"); break;
            case CExt:
                if (arg < _rows.Count) Say(Stage.OpenVideo(_rows[arg].Name));
                break;
            case CBack: Go("main"); break;
            case CMonth:
                if (_calMonth is { } m)
                {
                    var d = new DateTime(m.Y, m.M, 1).AddMonths(arg);
                    _calMonth = (d.Year, d.Month);
                    _picked = null;
                    _dirty = true;
                }
                break;
            case CDay:
                if (_calMonth is { } cm2) { _picked = $"{cm2.Y:0000}-{cm2.M:00}-{arg:00}"; _dirty = true; }
                break;
        }
    }

    public override void Dispose()
    {
        _p.Dispose();
        _body.Dispose();
        Stage.Dispose();
    }
}
