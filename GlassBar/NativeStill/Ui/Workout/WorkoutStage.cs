using System.Diagnostics;
using System.Numerics;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui.Workout;

// =====================================================================================================
// Die Trainings-Bühne (workout.js, „wo-stage“): eine Einheit übernimmt die Mitte des Bildschirms und läuft von
// selbst, mit Klängen, weil wer trainiert auf dem Boden ist, weg von der Maus. Hier liegen auch der gespeicherte
// Stand (Verlauf, Ziele, Körperdaten) und die laufende Einheit, die nach jedem Schritt gesichert wird.
// =====================================================================================================

/// <summary>Was von einer Übung in der laufenden Einheit erledigt ist.</summary>
public sealed class DoneRec
{
    public int Rung { get; set; }
    public int N { get; set; }
    public int Sets { get; set; }
}

/// <summary>Die laufende Einheit (im Original "run"), Zeiten in ms seit 1970.</summary>
public sealed class RunState
{
    public string Plan { get; set; } = "full";
    public bool Gentle { get; set; }
    public List<Step> Steps { get; set; } = new();
    public int I { get; set; }
    public bool Paused { get; set; }
    public double Left { get; set; }
    public long EndsAt { get; set; }
    public long StartedAt { get; set; }
    public long PausedMs { get; set; }
    public long PausedAt { get; set; }
    public string Note { get; set; } = "";
    public Dictionary<string, DoneRec> Done { get; set; } = new();
    public Dictionary<string, int> Nudges { get; set; } = new();
    public string Focus { get; set; } = "muscle";
}

public sealed class SessionSave
{
    public RunState? Run { get; set; }
    public long SavedAt { get; set; }
}

/// <summary>Treffer-Fläche eines Knopfs: Befehl und Argument statt Lambda, damit pro Bild nichts entsteht.</summary>
internal readonly record struct Hit(Rect R, int Cmd, int Arg)
{
    public bool Contains(Vector2 p) => p.X >= R.X && p.X < R.X + R.Width && p.Y >= R.Y && p.Y < R.Y + R.Height;
}

/// <summary>
/// Gemeinsame Zeichenhilfe für Pille und Bühne: ein Pinsel je Gerätekontext, zwischengespeicherte Textformate
/// und Textlayouts (selten benutzte werden nach einer Weile freigegeben).
/// </summary>
internal sealed class WorkoutPaint : IDisposable
{
    private readonly record struct FmtKey(int Size, int Weight, bool Display);
    private readonly record struct TextKey(string Text, int Size, int Weight, bool Display, int MaxW, int Flags);
    private sealed class Entry { public IDWriteTextLayout Layout = null!; public long Used; }

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private nint _factoryPtr;
    private ID2D1RadialGradientBrush? _vignette;
    private bool _vignetteLight;
    private readonly Dictionary<FmtKey, (IDWriteTextFormat Fmt, IDWriteInlineObject? Ellipsis)> _formats = new();
    private readonly Dictionary<TextKey, Entry> _texts = new();
    private readonly List<TextKey> _drop = new();
    private float _k;
    private string _font = "";
    private UiFonts? _fonts;
    private long _frame;

    public ID2D1DeviceContext Ctx = null!;
    public UiContext C = null!;

    /// <summary>Nur messen: Zeichenaufrufe tun dann nichts.</summary>
    public bool Measuring;

    public void Begin(ID2D1DeviceContext ctx, UiContext c)
    {
        Ctx = ctx;
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            DisposeDevice();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        }
        Prepare(c);
    }

    /// <summary>Textteil vorbereiten (geht auch ohne Gerätekontext, zum Messen).</summary>
    public void Prepare(UiContext c)
    {
        C = c;
        _frame++;
        if (_factoryPtr != c.D2D.NativePointer || _round == null)
        {
            _round?.Dispose();
            _factoryPtr = c.D2D.NativePointer;
            _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
        }
        string font = c.Settings.Str("font");
        if (_k != c.K || _font != font || !ReferenceEquals(_fonts, c.Fonts))
        {
            _k = c.K;
            _font = font;
            _fonts = c.Fonts;
            ClearText();
        }
        // Ab und zu aufräumen: Layouts, die eine Weile nicht gebraucht wurden.
        if (_frame % 120 == 0 && _texts.Count > 0)
        {
            foreach (var (key, e) in _texts) if (_frame - e.Used > 240) _drop.Add(key);
            foreach (var key in _drop) { _texts[key].Layout.Dispose(); _texts.Remove(key); }
            _drop.Clear();
        }
    }

    private (IDWriteTextFormat Fmt, IDWriteInlineObject? Ellipsis) Format(float px, int weight, bool display)
    {
        var key = new FmtKey((int)MathF.Round(px * 100), weight, display);
        if (_formats.TryGetValue(key, out var f)) return f;
        var fmt = C.Fonts.Format(C, px, weight, display);
        IDWriteInlineObject? ell = null;
        try { ell = C.DWrite.CreateEllipsisTrimmingSign(fmt); } catch { }
        f = (fmt, ell);
        _formats[key] = f;
        return f;
    }

    public const int Wrap = 1, Ellipsis = 2, Center = 4, Right = 8;

    /// <summary>Textlayout (zwischengespeichert). maxW = 0: unbegrenzt in einer Zeile.</summary>
    public IDWriteTextLayout Text(string s, float px, int weight = 500, bool display = false, float maxW = 0, int flags = 0)
    {
        var key = new TextKey(s, (int)MathF.Round(px * 100), weight, display, (int)MathF.Round(maxW), flags);
        if (_texts.TryGetValue(key, out var e)) { e.Used = _frame; return e.Layout; }
        var (fmt, ell) = Format(px, weight, display);
        float w = maxW > 0 ? MathF.Round(maxW) : 100000;
        var l = C.DWrite.CreateTextLayout(s, fmt, w, 100000);
        l.WordWrapping = (flags & Wrap) != 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;
        l.TextAlignment = (flags & Center) != 0 ? TextAlignment.Center : (flags & Right) != 0 ? TextAlignment.Trailing : TextAlignment.Leading;
        if ((flags & Ellipsis) != 0 && ell != null)
            l.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ell);
        _texts[key] = new Entry { Layout = l, Used = _frame };
        return l;
    }

    public static float W(IDWriteTextLayout l) => l.Metrics.WidthIncludingTrailingWhitespace;
    public static float H(IDWriteTextLayout l) => l.Metrics.Height;

    public ID2D1SolidColorBrush Brush(Vector3 c, float a)
    {
        _brush!.Color = new Color4(c.X, c.Y, c.Z, Math.Clamp(a, 0, 1));
        return _brush;
    }

    public void Draw(IDWriteTextLayout l, float x, float y, Vector3 c, float a)
    {
        if (Measuring || a <= 0.002f) return;
        Ctx.DrawTextLayout(new Vector2(x, y), l, Brush(c, a), DrawTextOptions.None);
    }

    public void Fill(Rect r, float radius, Vector3 c, float a)
    {
        if (Measuring || a <= 0.002f) return;
        if (radius <= 0) Ctx.FillRectangle(r, Brush(c, a));
        else Ctx.FillRoundedRectangle(new RoundedRectangle(Rf(r), radius, radius), Brush(c, a));
    }

    private static System.Drawing.RectangleF Rf(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    public void Stroke(Rect r, float radius, Vector3 c, float a, float width)
    {
        if (Measuring || a <= 0.002f) return;
        // Innen liegend wie inset box-shadow.
        float h = width / 2;
        var rr = new Rect(r.X + h, r.Y + h, Math.Max(0, r.Width - width), Math.Max(0, r.Height - width));
        Ctx.DrawRoundedRectangle(new RoundedRectangle(Rf(rr), Math.Max(0, radius - h), Math.Max(0, radius - h)), Brush(c, a), width);
    }

    public void Line(Vector2 p, Vector2 q, Vector3 c, float a, float width)
    {
        if (Measuring || a <= 0.002f) return;
        Ctx.DrawLine(p, q, Brush(c, a), width, _round);
    }

    public void Dot(Vector2 p, float r, Vector3 c, float a)
    {
        if (Measuring || a <= 0.002f) return;
        Ctx.FillEllipse(new Ellipse(p, r, r), Brush(c, a));
    }

    public void Ring(Vector2 p, float r, Vector3 c, float a, float width)
    {
        if (Measuring || a <= 0.002f) return;
        Ctx.DrawEllipse(new Ellipse(p, r - width / 2, r - width / 2), Brush(c, a), width);
    }

    /// <summary>Das Zeichen für ein Video der Übung: ein Quadrat, aus dessen Ecke ein Pfeil zeigt (24er-Raster).</summary>
    public void ExtIcon(Rect box, Vector3 c, float a)
    {
        float s = box.Width * 0.62f / 24f;
        var o = new Vector2(box.X + box.Width * 0.19f, box.Y + box.Height * 0.19f);
        Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
        float w = 2.2f * s;
        Line(P(14, 4), P(20, 4), c, a, w);
        Line(P(20, 4), P(20, 10), c, a, w);
        Line(P(20, 4), P(11, 13), c, a, w);
        Line(P(18, 14), P(18, 19), c, a, w);
        Line(P(17, 20), P(5, 20), c, a, w);
        Line(P(4, 19), P(4, 7), c, a, w);
        Line(P(5, 6), P(10, 6), c, a, w);
        Line(P(18, 19), P(17, 20), c, a, w);
        Line(P(5, 20), P(4, 19), c, a, w);
        Line(P(4, 7), P(5, 6), c, a, w);
    }

    /// <summary>Weicher Schleier hinter der Bühne: dunkel auf dunklem, hell auf hellem Theme.</summary>
    public void Vignette(float alpha)
    {
        if (alpha <= 0.002f) return;
        bool light = C.LightTheme;
        if (_vignette == null || _vignetteLight != light)
        {
            _vignette?.Dispose();
            _vignetteLight = light;
            var col = light ? new Vector3(1, 1, 1) : Vector3.Zero;
            float a0 = light ? 0.35f : 0.3f;
            var gs = new[]
            {
                new GradientStop { Position = 0, Color = new Color4(col.X, col.Y, col.Z, a0) },
                new GradientStop { Position = 1, Color = new Color4(col.X, col.Y, col.Z, 0) },
            };
            using var coll = Ctx.CreateGradientStopCollection(gs);
            _vignette = Ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties { Center = Vector2.Zero, RadiusX = 1, RadiusY = 1 }, coll);
        }
        float w = C.Width, h = C.Height;
        _vignette.Center = new Vector2(w / 2, h / 2);
        _vignette.RadiusX = w * 0.7f;
        _vignette.RadiusY = h * 0.7f;
        _vignette.Opacity = alpha;
        Ctx.FillRectangle(new Rect(0, 0, w, h), _vignette);
    }

    private void ClearText()
    {
        foreach (var e in _texts.Values) e.Layout.Dispose();
        _texts.Clear();
        foreach (var f in _formats.Values) f.Ellipsis?.Dispose(); // Fmt gehört UiFonts
        _formats.Clear();
    }

    private void DisposeDevice()
    {
        _brush?.Dispose(); _brush = null;
        _vignette?.Dispose(); _vignette = null;
        _ctxPtr = 0;
    }

    public void Dispose()
    {
        DisposeDevice();
        _round?.Dispose(); _round = null;
        ClearText();
    }
}

/// <summary>Kleine Hilfen für Pille und Bühne.</summary>
internal static class Wo
{
    public static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    public static float Approach(float v, float target, double dt, float rate) =>
        v + (target - v) * (1 - MathF.Exp(-(float)dt * rate));
    public static Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);
    /// <summary>Farbe der Trainingskarte (--tp); ohne „bunte Karten“ ein ruhiges Weiß.</summary>
    public static Vector3 Tp(UiContext c) => c.Settings.Bool("cardColors") ? Rgb(150, 226, 140) : Rgb(226, 230, 240);
    public static string Amount(Move m, int n) => m.Kind == "reps" ? n.ToString() : $"{n}s";
}

/// <summary>
/// Die geführte Einheit auf der ganzen Bühne. Läuft auf der echten Uhr, nicht nach Bildern; war das Bild
/// länger als 8 s eingefroren, hält sie an, statt durch die Schritte zu rasen. Setzt c.StageBusy, solange sie
/// läuft (Uhr und Datum treten dann zurück).
/// </summary>
public sealed class WorkoutStage : IOverlay, IDisposable
{
    private const string DataKey = "workout.data", SessionKey = "workout.session";

    /// <summary>Gespeicherter Stand (Verlauf, Ziele, Körperdaten).</summary>
    public WorkoutData Data { get; }
    /// <summary>Zählt bei jeder Änderung, die die Karte betrifft, hoch.</summary>
    public int Version { get; private set; }
    /// <summary>Läuft eine Einheit oder zeigt die Bühne den Abschluss?</summary>
    public bool On => _run != null || _fin != null;
    public bool Running => _run != null;

    private StillValues _set = new();
    private UiContext? _c;
    private RunState? _run;
    private Fin? _fin;

    private sealed class FinRow { public int I; public string Id = "", Name = "", Amount = ""; public Target Base = new(); public string? Feel; }
    private sealed class Fin { public HistoryEntry Entry = null!; public List<FinRow> Rows = new(); public string Focus = "muscle", Sub = ""; public int Mins; public long Until; }

    public WorkoutStage()
    {
        Data = StillState.Get(DataKey, new WorkoutData());
        Data.History ??= new();
        Data.Targets ??= new();
        _lastTick = WorkoutPlan.NowMs();
        // Eine durch Neustart oder Stromausfall unterbrochene Einheit kommt pausiert zurück.
        var saved = StillState.Get(SessionKey, new SessionSave());
        try { Adopt(saved.Run, saved.SavedAt); } catch { _run = null; }
    }

    /* ---------- Gespeichert ---------- */

    public void Save()
    {
        Data.SavedAt = WorkoutPlan.NowMs();
        StillState.Set(DataKey, Data);
        Version++;
    }

    private void SaveRun()
    {
        StillState.Set(SessionKey, new SessionSave { Run = _run, SavedAt = WorkoutPlan.NowMs() });
        Version++;
    }

    public string Focus => _set.Str("workoutFocus") == "strength" ? "strength" : "muscle";

    public SessionOptions Options(bool gentle) =>
        new(Focus, _set.Bool("workoutTable"), Data.FullB, _set.Num("workoutRest") is var r && r > 0 ? r : 1, Data.Level, gentle);

    public Target TargetOf(string id) =>
        WorkoutPlan.Fit(id, Data.Targets.TryGetValue(id, out var t) ? t : WorkoutPlan.StartState(id, Data.Level, Focus), Focus);

    public Recommendation Suggestion() =>
        WorkoutPlan.Recommend(Data.History, WorkoutPlan.NowMs(), Math.Max(1, (int)Math.Round(_set.Num("workoutGoal"))));

    /// <summary>Von der Pille jedes Bild aufgerufen, damit die Einstellungen auch ohne Bühne bekannt sind.</summary>
    public void Bind(UiContext c)
    {
        _c = c;
        _set = c.Settings;
    }

    /* ---------- Die Einheit ---------- */

    private Step? StepNow => _run != null && _run.I >= 0 && _run.I < _run.Steps.Count ? _run.Steps[_run.I] : null;
    private double LeftMs(long now) => _run!.Paused ? _run.Left : Math.Max(0, _run.EndsAt - now);

    public void Start(string planId, bool gentle)
    {
        long now = WorkoutPlan.NowMs();
        var steps = WorkoutPlan.BuildSession(planId, Data.Targets, Options(gentle));
        _run = new RunState
        {
            Plan = planId, Gentle = gentle, Steps = steps, I = 0, EndsAt = now + steps[0].Sec * 1000L, StartedAt = now, Focus = Focus,
        };
        _fin = null;
        _endUntil = _abortUntil = 0;
        SaveRun();
        RenderStep();
        Cue("start");
    }

    private void Cue(string what)
    {
        if (!_set.Bool("workoutChime")) return;
        // Das Original spielt kleine Glockenfolgen; hier die kurzen Klänge aus tones.js.
        string name = what switch { "soon" => "workout-soon", "start" => "workout-start", "stop" => "workout-stop", _ => "workout-done" };
        Tones.Play(name, what == "soon" ? 0.5f : 0.75f);
    }

    private void NextStep()
    {
        if (_run == null || StepNow is not { } s) return;
        if (s.K == "work")
        {
            _run.Done.TryGetValue(s.M, out var d);
            _run.Done[s.M] = new DoneRec { Rung = s.Rung, N = s.N, Sets = (d?.Sets ?? 0) + 1 };
        }
        _run.I++;
        if (_run.I >= _run.Steps.Count) { Finish(false, false); return; }
        var n = StepNow!;
        _run.Paused = false;
        _run.Note = "";
        _run.EndsAt = WorkoutPlan.NowMs() + n.Sec * 1000L;
        _endUntil = 0;
        SaveRun();
        RenderStep();
        Cue(n.K == "work" ? "start" : "stop");
    }

    private void TogglePause(string note = "")
    {
        if (_run == null) return;
        long now = WorkoutPlan.NowMs();
        if (_run.Paused)
        {
            _run.EndsAt = now + (long)_run.Left;
            _run.PausedMs += now - _run.PausedAt;
            _run.Paused = false;
            _run.Note = "";
        }
        else
        {
            _run.Left = LeftMs(now);
            _run.PausedAt = now;
            _run.Paused = true;
            _run.Note = note;
        }
        SaveRun();
    }

    private void MoreRest()
    {
        if (_run == null || StepNow is not { } s) return;
        s.Sec += 30;
        if (_run.Paused) _run.Left += 30000;
        else _run.EndsAt += 30000;
        SaveRun();
    }

    private void Adjust(int dir)
    {
        if (_run == null || StepNow is not { } s || s.K != "work") return;
        long now = WorkoutPlan.NowMs();
        double gone = s.Sec * 1000.0 - LeftMs(now);
        string? said = WorkoutPlan.AdjustNow(_run.Steps, _run.I, dir, _run.Focus, out bool changed);
        if (said == null) return;
        if (changed) _run.Nudges[s.M] = (_run.Nudges.TryGetValue(s.M, out var v) ? v : 0) + dir;
        // Die Zeit dieses Satzes folgt seiner neuen Größe; was schon vorbei ist, bleibt vorbei.
        var sNow = StepNow!;
        double left = Math.Max(3000, sNow.Sec * 1000.0 - gone);
        if (!_run.Paused) _run.EndsAt = now + (long)left;
        else _run.Left = left;
        SaveRun();
        RenderStep();
        Toast(said);
    }

    private void AskEnd()
    {
        if (_run == null) return;
        long now = WorkoutPlan.NowMs();
        if (now >= _endUntil) { _endUntil = now + 3000; return; }
        _endUntil = 0;
        Finish(true, false);
    }

    /* Abbrechen: als hätte es nie begonnen. Nichts kommt in den Kalender, Ziele und der Ganzkörper-Wechsel
       bleiben, wie sie waren, und es fragt einmal vorher. */
    private void AskAbort()
    {
        if (_run == null) return;
        long now = WorkoutPlan.NowMs();
        if (now >= _abortUntil) { _abortUntil = now + 3000; return; }
        _abortUntil = 0;
        _run = null;
        SaveRun();
    }

    /* Vorbei. Es steht sofort im Kalender, und die Ziele fürs nächste Mal werden gesetzt, als hätte sich jede
       Übung genau richtig angefühlt (oder wie Leichter/Schwerer es nahelegten); die Tipps verfeinern sie. Ein nicht
       beendeter Satz oder eine leichtere Wiedereinstiegs-Einheit bewegt von sich aus nichts. */
    private void Finish(bool early, bool quiet)
    {
        var r = _run!;
        _run = null;
        if (r.Done.Count == 0)
        {
            _fin = null;
            SaveRun();
            return;
        }
        long ended = r.Paused ? r.PausedAt : WorkoutPlan.NowMs();
        int mins = Math.Max(1, (int)Math.Round((ended - r.StartedAt - r.PausedMs) / 60000.0));
        var entry = new HistoryEntry
        {
            Day = WorkoutPlan.DayKey(WorkoutPlan.LocalOf(r.StartedAt)), // der Tag des Beginns, auch nach Mitternacht
            At = ended,                                                   // das Ende, ab dem Erholung zählt
            Plan = r.Plan,
            Mins = mins,
            Focus = r.Focus,
            Early = early,
            Done = r.Done.Select(kv => new DoneMove { M = kv.Key, Rung = kv.Value.Rung, N = kv.Value.N, Sets = kv.Value.Sets }).ToList(),
        };
        Data.History.Add(entry);
        if (Data.History.Count > 2000) Data.History.RemoveRange(0, Data.History.Count - 2000);
        if (r.Plan == "full") Data.FullB = !Data.FullB;
        string label = L.T(WorkoutPlan.Plans.TryGetValue(r.Plan, out var pl) ? pl.Label : "Training");
        var fin = new Fin
        {
            Entry = entry, Focus = r.Focus, Mins = mins, Until = WorkoutPlan.NowMs() + 10 * 60000,
            Sub = L.F("{0} · {1} Min. Wie hat sich jede Übung angefühlt? Das bestimmt das nächste Mal.", label, mins),
        };
        for (int i = 0; i < entry.Done.Count; i++)
        {
            var d = entry.Done[i];
            int planned = r.Steps.Count(s => s.K == "work" && s.M == d.M);
            var mv = WorkoutPlan.Moves[d.M];
            var row = new FinRow
            {
                I = i, Id = d.M, Base = FitFor(d.M, r.Focus),
                Name = L.T(mv.Rungs[Math.Clamp(d.Rung, 0, mv.Rungs.Length - 1)].Name), Amount = $"{d.Sets} × {Wo.Amount(mv, d.N)}",
            };
            int nudge = r.Nudges.TryGetValue(d.M, out var nv) ? nv : 0;
            if (!r.Gentle && d.Sets >= planned) row.Feel = nudge > 0 ? "easy" : nudge < 0 ? "hard" : "right";
            fin.Rows.Add(row);
        }
        foreach (var row in fin.Rows) ApplyFeel(fin, row);
        // Eingetragen und die Einheit gelöscht, in einem Zug.
        Save();
        SaveRun();
        if (quiet) { _fin = null; return; }
        _fin = fin;
        Cue("done");
        _c?.Bloom(_c.Width / 2f, _c.Height / 2f, 0.8f);
    }

    private Target FitFor(string id, string focus) =>
        WorkoutPlan.Fit(id, Data.Targets.TryGetValue(id, out var t) ? t : WorkoutPlan.StartState(id, Data.Level, focus), focus);

    private void ApplyFeel(Fin fin, FinRow row)
    {
        Data.Targets[row.Id] = row.Feel != null ? WorkoutPlan.NextTarget(row.Id, row.Base, fin.Focus, row.Feel) : row.Base.Copy();
        fin.Entry.Done[row.I].Feel = row.Feel;
    }

    private void CloseFinish()
    {
        _fin = null;
        Version++;
    }

    /* Eine durch Neustart unterbrochene Einheit kommt pausiert zurück, wo sie war: erledigte Sätze bleiben
       erledigt, ein angefangener beginnt neu. Ist sie von einem anderen Tag und Stunden alt, ist sie vorbei:
       was gemacht wurde, wird am Tag ihres Beginns eingetragen. */
    private void Adopt(RunState? saved, long savedAt)
    {
        if (saved == null || saved.Steps == null || saved.I < 0 || saved.I >= saved.Steps.Count) return;
        foreach (var s in saved.Steps) if (s.K == "work" && !WorkoutPlan.Moves.ContainsKey(s.M)) return;
        saved.Done ??= new();
        saved.Nudges ??= new();
        _run = saved;
        long now = WorkoutPlan.NowMs();
        long at = savedAt > 0 ? savedAt : now;
        bool sameDay = WorkoutPlan.DayKey(WorkoutPlan.LocalOf(_run.StartedAt)) == WorkoutPlan.DayKey(DateTime.Now);
        if (!sameDay && now - at > 6 * 3600000L)
        {
            if (!_run.Paused) _run.PausedAt = at;
            _run.Paused = true;
            Finish(true, true);
            return;
        }
        if (_run.Paused)
        {
            // Die Zeit, in der alles aus war, zählt nicht als Trainingszeit.
            _run.PausedMs += Math.Max(0, at - _run.PausedAt);
            _run.PausedAt = at;
        }
        else
        {
            _run.PausedMs += Math.Max(0, now - at);
            _run.Left = StepNow!.Sec * 1000.0;
            _run.PausedAt = now;
            _run.Paused = true;
        }
        _run.Note = "Dort weitergemacht, wo du aufgehört hast";
        SaveRun();
        RenderStep();
    }

    /// <summary>Video zur Übung: eine YouTube-Suche im Standardbrowser. Eine laufende Einheit pausiert vorher.
    /// name ist der angezeigte (schon übersetzte) Name.</summary>
    public string OpenVideo(string name)
    {
        // Gespeichert wird die deutsche Notiz; übersetzt wird sie erst beim Anzeigen.
        if (_run is { Paused: false }) TogglePause("Pausiert wegen des Videos");
        string url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(L.F("Ausführung: {0}", name));
        string said;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            said = L.F("YouTube wird geöffnet: {0}", name);
        }
        catch { said = L.F("Der Browser ließ sich nicht öffnen. Suche auf YouTube nach: {0}", name); }
        if (_run != null) Toast(said);
        return said;
    }

    /* ---------- Anzeige-Zustand (nur bei Änderungen neu gebaut) ---------- */

    private string _topL = "", _kick = "", _name = "", _cue = "", _works = "", _repsSmall = "";
    private string _bigReps = "";
    private string _stepKind = "", _moveKind = "";
    private Works? _bodyWorks;
    private string _bodyFor = "";
    private string _toast = "";
    private long _toastUntil;
    private float _toastA;
    private long _endUntil, _abortUntil;
    private long _lastTick;
    private int _lastSecond = -1;
    private long _clockFor = -1, _remainFor = -1;
    private string _clock = "", _remain = "";
    private float _bar, _countBar;

    private void Toast(string text)
    {
        _toast = text;
        _toastUntil = WorkoutPlan.NowMs() + 2800;
    }

    private void RenderStep()
    {
        if (_run == null || StepNow is not { } s) return;
        bool work = s.K == "work";
        var target = work ? s : _run.Steps.Skip(_run.I).FirstOrDefault(x => x.K == "work") ?? s;
        if (target.K != "work") return;
        var m = WorkoutPlan.Moves[target.M];
        var r = m.Rungs[Math.Clamp(target.Rung, 0, m.Rungs.Length - 1)];
        var plan = WorkoutPlan.Plans[_run.Plan];
        var list = _run.Steps.Where(x => x.K == "work").Select(x => x.M).Distinct().ToList();
        string planLabel = L.T(plan.Label);
        _topL = plan.Circuit ? planLabel : L.F("{0} · Übung {1} von {2}", planLabel, list.IndexOf(target.M) + 1, list.Count);
        _kick = s.K == "ready" ? L.T("Mach dich bereit") : s.K == "rest" ? L.T("Pause. Als Nächstes")
            : plan.Circuit ? L.F("Runde {0} von {1}", s.Set + 1, s.Sets) : L.F("Satz {0} von {1}", s.Set + 1, s.Sets);
        _name = L.T(r.Name);
        _cue = L.T(r.Cue);
        if (_bodyFor != target.M)
        {
            _bodyFor = target.M;
            _bodyWorks = WorkoutBody.MusclesFor([target.M]);
            _works = L.F("Trainiert: {0}", string.Join(", ", _bodyWorks.PrimaryList.Select(x => WorkoutBody.Label(x).ToLower(L.Culture))));
        }
        _stepKind = s.K;
        _moveKind = m.Kind;
        _bigReps = s.N.ToString(L.Culture);
        _repsSmall = L.T(r.Sides ? "Wdh. pro Seite" : "Wdh.");
        _lastSecond = -1;
        _clockFor = _remainFor = -1;
        Version++;
    }

    /* ---------- Jedes Bild ---------- */

    public int Z => 40;

    private float _fade;
    private float _lastBusy;

    public bool Animating => _run != null || _fin != null || _fade > 0.001f || _toastA > 0.001f;

    public void Update(UiContext c, double dt)
    {
        Bind(c);
        long now = WorkoutPlan.NowMs();
        long before = _lastTick;
        _lastTick = now;
        Tick(now, before);
        if (_fin != null && now > _fin.Until) CloseFinish();

        _fade = Wo.Approach(_fade, On && !c.Editing ? 1 : 0, dt, On ? 7f : 10f);
        if (_fade < 0.001f && !On) _fade = 0;
        // Bühne beanspruchen, ohne einer anderen Überlagerung ihren Anspruch zu nehmen.
        if (c.StageBusy <= _lastBusy + 1e-4f) c.StageBusy = _fade;
        else c.StageBusy = Math.Max(c.StageBusy, _fade);
        _lastBusy = _fade;

        _toastA = Wo.Approach(_toastA, now < _toastUntil ? 1 : 0, dt, 12f);

        if (_run != null && StepNow is { } s)
        {
            double left = LeftMs(now), total = Math.Max(1, s.Sec * 1000.0);
            long sec = (long)Math.Ceiling(left / 1000);
            if (sec != _clockFor) { _clockFor = sec; _clock = $"{sec / 60}:{sec % 60:00}"; }
            double doneMs = 0;
            for (int i = 0; i < _run.I; i++) doneMs += _run.Steps[i].Sec * 1000.0;
            doneMs += total - left;
            double allMs = WorkoutPlan.SessionSeconds(_run.Steps) * 1000.0;
            long remain = Math.Max(0, (long)Math.Ceiling((allMs - doneMs) / 60000));
            if (remain != _remainFor) { _remainFor = remain; _remain = L.F("noch {0} Min.", remain); }
            _bar = Wo.Approach(_bar, (float)Math.Clamp(doneMs / allMs, 0, 1), dt, 10f);
            _countBar = Wo.Approach(_countBar, (float)Math.Clamp(1 - left / total, 0, 1), dt, 10f);
        }
    }

    private void Tick(long t, long before)
    {
        if (_run == null || StepNow is not { } s) return;
        long gap = t - before;
        if (!_run.Paused && gap > 8000)
        {
            // Macht mit der Zeit weiter, die beim Einfrieren übrig war; die Lücke zählt nicht.
            double left = Math.Clamp(_run.EndsAt - before, 1000, s.Sec * 1000.0);
            TogglePause("Pausiert, während das Hintergrundbild verdeckt war");
            _run.Left = left;
            _run.PausedAt = before;
            SaveRun();
            return;
        }
        if (_run.Paused) return;
        long leftMs = _run.EndsAt - t;
        if (leftMs <= 0) { NextStep(); return; }
        int sec = (int)Math.Ceiling(leftMs / 1000.0);
        if (sec != _lastSecond)
        {
            _lastSecond = sec;
            if (sec == 3 && s.K != "work") Cue("soon");
        }
    }

    /* ---------- Zeichnen ---------- */

    private const int CPause = 1, CNext = 2, CMore = 3, CEasy = 4, CHard = 5, CEnd = 6, CAbort = 7, CExt = 8, CFeel = 9, CFinDone = 10;
    private readonly List<Hit> _hits = new();
    private readonly WorkoutPaint _p = new();
    private readonly WorkoutBody _body = new();
    private Rect _finRect;
    private Rect _pressed;
    private bool _hasPressed;
    private static readonly string[] FeelIds = ["easy", "right", "hard"];
    private static readonly string[] FeelLabels = ["Zu leicht", "Genau richtig", "Zu schwer"];

    private struct Btn { public string Text; public int Cmd; public bool Main, End; public float W; }
    private readonly Btn[] _btns = new Btn[7];

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        _hits.Clear();
        if (_fade <= 0.002f || c.Editing) return;
        _p.Begin(ctx, c);
        float a = _fade;
        _p.Vignette(a);
        if (_fin != null) DrawFinish(c, a);
        else if (_run != null) DrawRun(c, a);
    }

    private bool Hover(UiContext c, Rect r) =>
        c.PointerInside && c.Pointer.X >= r.X && c.Pointer.X < r.X + r.Width && c.Pointer.Y >= r.Y && c.Pointer.Y < r.Y + r.Height;

    private void DrawRun(UiContext c, float a)
    {
        if (_run == null || StepNow is not { } s) return;
        var p = _p;
        float k = c.K;
        var fg = c.Fg;
        float colW = Math.Min(c.Width - 32, 860 * k);
        bool work = s.K == "work", reps = work && _moveKind == "reps", paused = _run.Paused;
        float liveA = paused ? 0.5f : 1;
        // Einblenden wie wo-in: leicht verkleinert und durchsichtig.
        float scaleIn = 0.985f + 0.015f * a;
        var saved = p.Ctx.Transform;
        var center = new Vector2(c.Width / 2f, c.Height / 2f);
        p.Ctx.Transform = Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateScale(scaleIn) * Matrix3x2.CreateTranslation(center) * saved;

        // Messen
        // Die Notiz wird deutsch gespeichert (RunState) und erst hier übersetzt.
        string topR = paused ? L.T(_run.Note.Length > 0 ? _run.Note : "Pausiert") : _remain;
        var lTop = p.Text(_topL, 17 * k, 600);
        var rTop = p.Text(topR, 17 * k, 600);
        float topH = Math.Max(WorkoutPaint.H(lTop), WorkoutPaint.H(rTop)) + 9 * k + 5 * k;

        float colGap = 44 * k;
        float textW = (colW - colGap) / 2;
        var kick = p.Text(_kick, 22 * k, 600, false, textW, WorkoutPaint.Wrap);
        float nameSize = 54 * k;
        var name = p.Text(_name, nameSize, 700, true, textW - nameSize * 0.85f, WorkoutPaint.Wrap);
        var big = p.Text(reps ? _bigReps : _clock, 128 * k, 700, true);
        var small = reps ? p.Text(_repsSmall, 30 * k, 600) : null;
        var count = reps ? p.Text(_clock, 24 * k, 600) : null;
        var works = p.Text(_works, 19 * k, 550, false, textW, WorkoutPaint.Wrap);
        float bigH = WorkoutPaint.H(big) * 0.82f; // line-height: 1
        float textH = WorkoutPaint.H(kick) + 4 * k + WorkoutPaint.H(name) + 4 * k + 6 * k + bigH
                      + (count != null ? 4 * k + 8 * k + WorkoutPaint.H(count) : 0) + 4 * k + 10 * k + WorkoutPaint.H(works);
        float bodyH = 340 * k;
        float liveH = Math.Max(bodyH, textH);
        var cue = p.Text(_cue, 22 * k, 500, false, colW, WorkoutPaint.Wrap | WorkoutPaint.Center);
        float cueH = Math.Max(WorkoutPaint.H(cue), 22 * k * 1.4f);

        // Knöpfe
        int nb = 0;
        if (work) { _btns[nb++] = new Btn { Text = L.T("Leichter"), Cmd = CEasy }; _btns[nb++] = new Btn { Text = L.T("Schwerer"), Cmd = CHard }; }
        _btns[nb++] = new Btn { Text = L.T(paused ? "Fortsetzen" : "Pause"), Cmd = CPause, Main = true };
        _btns[nb++] = new Btn { Text = L.T(work ? "Fertig" : "Überspringen"), Cmd = CNext };
        if (!work) _btns[nb++] = new Btn { Text = "+30 s", Cmd = CMore };
        long now = WorkoutPlan.NowMs();
        _btns[nb++] = new Btn { Text = L.T(now < _endUntil ? "Jetzt beenden?" : "Beenden und speichern"), Cmd = CEnd, End = true };
        _btns[nb++] = new Btn { Text = L.T(now < _abortUntil ? "Abbrechen? Dann zählt es nicht" : "Abbrechen"), Cmd = CAbort, End = true };
        float bh = 46 * k, bpad = 24 * k, bgap = 10 * k;
        float rowW = 0;
        int rows = 1;
        float maxRow = Math.Max(colW, Math.Min(c.Width - 32, 1100 * k));
        for (int i = 0; i < nb; i++)
        {
            _btns[i].W = WorkoutPaint.W(p.Text(_btns[i].Text, 15 * k, 650)) + 2 * bpad;
            float add = (rowW > 0 ? bgap : 0) + _btns[i].W;
            if (rowW > 0 && rowW + add > maxRow) { rows++; rowW = _btns[i].W; }
            else rowW += add;
        }
        float ctrlH = rows * bh + (rows - 1) * bgap;
        float toastH = 26 * k;
        float gap = 22 * k;
        float total = topH + gap + liveH + gap + cueH + gap - 12 * k + toastH - 12 * k + gap + ctrlH;
        float y = Math.Max(16 * k, (c.Height - total) / 2);
        float x0 = (c.Width - colW) / 2;

        // Oben: Plan, Restzeit, Fortschritt
        p.Draw(lTop, x0, y, fg, 0.72f * a);
        p.Draw(rTop, x0 + colW - WorkoutPaint.W(rTop), y, fg, 0.72f * a);
        float barY = y + topH - 5 * k;
        p.Fill(new Rect(x0, barY, colW, 5 * k), 2.5f * k, fg, 0.14f * a);
        if (_bar > 0.001f) p.Fill(new Rect(x0, barY, Math.Max(5 * k, colW * _bar), 5 * k), 2.5f * k, fg, 0.85f * a);
        y += topH + gap;

        // Mitte: Körper links, Text rechts
        float pairW = WorkoutBody.PairWidth(bodyH, 8 * k);
        float bodyX = x0 + textW - pairW;
        float sweep = work && !paused ? (float)(c.Time % 1.9 / 1.9) : -1;
        if (_bodyWorks != null) _body.DrawPair(p.Ctx, c, bodyX, y + (liveH - bodyH) / 2, bodyH, 8 * k, _bodyWorks, true, sweep, a * liveA);

        float tx = x0 + textW + colGap;
        float ty = y + (liveH - textH) / 2;
        p.Draw(kick, tx, ty, fg, 0.68f * a * liveA);
        ty += WorkoutPaint.H(kick) + 4 * k;
        p.Draw(name, tx, ty, fg, a * liveA);
        // Das Video-Zeichen hinter dem letzten Wort
        {
            name.HitTestTextPosition((uint)Math.Max(0, _name.Length - 1), true, out float ex, out float ey, out var hm);
            float es = nameSize * 0.56f * 1.45f;
            var er = new Rect(tx + ex + nameSize * 0.56f * 0.22f, ty + ey + (hm.Height - es) / 2 - nameSize * 0.56f * 0.12f, es, es);
            bool hov = Hover(c, er);
            if (hov) p.Fill(er, es * 0.28f, fg, 0.12f * a);
            p.ExtIcon(er, fg, (hov ? 1f : 0.5f) * a * liveA);
            _hits.Add(new Hit(er, CExt, 0));
        }
        ty += WorkoutPaint.H(name) + 4 * k + 6 * k;
        float bigTop = ty - WorkoutPaint.H(big) * 0.09f;
        p.Draw(big, tx, bigTop, fg, a * liveA);
        if (small != null)
        {
            // Grundlinien angleichen
            float bigBase = bigTop + Baseline(big), smallBase = Baseline(small);
            p.Draw(small, tx + WorkoutPaint.W(big) + 14 * k, bigBase - smallBase, fg, 0.68f * a * liveA);
        }
        ty += bigH;
        if (count != null)
        {
            ty += 4 * k + 8 * k;
            p.Draw(count, tx, ty, fg, 0.78f * a * liveA);
            float cbx = tx + WorkoutPaint.W(count) + 14 * k;
            float cbw = Math.Min(280 * k, tx + textW - cbx);
            float cby = ty + WorkoutPaint.H(count) / 2 - 3 * k;
            if (cbw > 0)
            {
                p.Fill(new Rect(cbx, cby, cbw, 6 * k), 3 * k, fg, 0.14f * a * liveA);
                if (_countBar > 0.001f) p.Fill(new Rect(cbx, cby, Math.Max(6 * k, cbw * _countBar), 6 * k), 3 * k, fg, 0.8f * a * liveA);
            }
            ty += WorkoutPaint.H(count);
        }
        ty += 4 * k + 10 * k;
        p.Draw(works, tx, ty, fg, 0.72f * a * liveA);
        y += liveH + gap;

        // Hinweis zur Ausführung
        p.Draw(cue, x0, y, fg, 0.78f * a * liveA);
        y += cueH + gap - 12 * k;

        // Kurze Meldung
        if (_toastA > 0.002f && _toast.Length > 0)
        {
            var t = p.Text(_toast, 17 * k, 650);
            p.Draw(t, (c.Width - WorkoutPaint.W(t)) / 2, y + (toastH - WorkoutPaint.H(t)) / 2, fg, _toastA * a);
        }
        y += toastH - 12 * k + gap;

        // Knöpfe, zentriert und umbrechend
        int start = 0;
        while (start < nb)
        {
            float w = 0;
            int end = start;
            while (end < nb)
            {
                float add = (end > start ? bgap : 0) + _btns[end].W;
                if (end > start && w + add > maxRow) break;
                w += add;
                end++;
            }
            float bx = (c.Width - w) / 2;
            for (int i = start; i < end; i++)
            {
                var b = _btns[i];
                var r = new Rect(bx, y, b.W, bh);
                bool hov = Hover(c, r);
                float bg = b.Main ? (hov ? 0.32f : 0.24f) : (hov ? 0.2f : 0.12f);
                p.Fill(r, bh / 2, fg, bg * a);
                var l = p.Text(b.Text, 15 * k, 650);
                p.Draw(l, bx + bpad, y + (bh - WorkoutPaint.H(l)) / 2, fg, (b.End ? 0.62f : 0.92f) * a);
                _hits.Add(new Hit(r, b.Cmd, 0));
                bx += b.W + bgap;
            }
            y += bh + bgap;
            start = end;
        }
        p.Ctx.Transform = saved;
    }

    private static float Baseline(IDWriteTextLayout l)
    {
        var lines = l.LineMetrics;
        return lines.Length > 0 ? lines[0].Baseline : l.Metrics.Height * 0.8f;
    }

    private void DrawFinish(UiContext c, float a)
    {
        var fin = _fin!;
        var p = _p;
        float k = c.K;
        var fg = c.Fg;
        float w = Math.Min(c.Width - 32, 620 * k), pad = 26 * k, gap = 14 * k;
        float inner = w - 2 * pad;
        var title = p.Text(L.T(fin.Entry.Early ? "Gute Leistung." : "Schön gemacht."), 34 * k, 700, true);
        var sub = p.Text(fin.Sub, 14.5f * k, 500, false, inner, WorkoutPaint.Wrap);

        // Breite der Gefühls-Knöpfe
        float fh = 32 * k, fpad = 11 * k, fgap = 4 * k;
        float optsW = 0;
        for (int i = 0; i < 3; i++) optsW += WorkoutPaint.W(p.Text(L.T(FeelLabels[i]), 12 * k, 600)) + 2 * fpad + (i > 0 ? fgap : 0);
        float nameW = Math.Max(40 * k, inner - optsW - 12 * k);

        float rowsH = 0;
        foreach (var row in fin.Rows)
        {
            var (n1, n2) = RowText(p, row, k, nameW);
            rowsH += Math.Max(fh, WorkoutPaint.H(n1) + WorkoutPaint.H(n2));
        }
        rowsH += Math.Max(0, fin.Rows.Count - 1) * 12 * k;
        float btnH = 36 * k;
        float h = pad + WorkoutPaint.H(title) + gap - 8 * k + WorkoutPaint.H(sub) + gap + rowsH + gap + btnH + pad;
        float x = (c.Width - w) / 2, y = (c.Height - h) / 2;
        _finRect = new Rect(x, y, w, h);
        c.Glass.Panel(p.Ctx, c, _finRect, 26 * k, a);

        float cy = y + pad;
        p.Draw(title, x + pad, cy, fg, a);
        cy += WorkoutPaint.H(title) + gap - 8 * k;
        p.Draw(sub, x + pad, cy, fg, 0.68f * a);
        cy += WorkoutPaint.H(sub) + gap;
        for (int ri = 0; ri < fin.Rows.Count; ri++)
        {
            var row = fin.Rows[ri];
            var (n1, n2) = RowText(p, row, k, nameW);
            float rh = Math.Max(fh, WorkoutPaint.H(n1) + WorkoutPaint.H(n2));
            float ny = cy + (rh - WorkoutPaint.H(n1) - WorkoutPaint.H(n2)) / 2;
            p.Draw(n1, x + pad, ny, fg, a);
            p.Draw(n2, x + pad, ny + WorkoutPaint.H(n1), fg, 0.58f * a);
            float bx = x + w - pad - optsW, by = cy + (rh - fh) / 2;
            for (int i = 0; i < 3; i++)
            {
                var l = p.Text(L.T(FeelLabels[i]), 12 * k, 600);
                float bw = WorkoutPaint.W(l) + 2 * fpad;
                var r = new Rect(bx, by, bw, fh);
                bool on = row.Feel == FeelIds[i], hov = Hover(c, r);
                p.Fill(r, fh / 2, fg, (on ? 0.28f : hov ? 0.15f : 0.08f) * a);
                if (on) p.Stroke(r, fh / 2, fg, 0.35f * a, 1);
                p.Draw(l, bx + fpad, by + (fh - WorkoutPaint.H(l)) / 2, fg, (on || hov ? 1f : 0.72f) * a);
                _hits.Add(new Hit(r, CFeel, ri * 3 + i));
                bx += bw + fgap;
            }
            cy += rh + 12 * k;
        }
        cy += gap - 12 * k;
        var br = new Rect(x + pad, cy, inner, btnH);
        bool bh = Hover(c, br);
        p.Fill(br, btnH / 2, fg, (bh ? 0.28f : 0.2f) * a);
        var done = p.Text(L.T("Fertig"), 13 * k, 600);
        p.Draw(done, br.X + (inner - WorkoutPaint.W(done)) / 2, cy + (btnH - WorkoutPaint.H(done)) / 2, fg, a);
        _hits.Add(new Hit(br, CFinDone, 0));
    }

    private static (IDWriteTextLayout, IDWriteTextLayout) RowText(WorkoutPaint p, FinRow row, float k, float w) =>
        (p.Text(row.Name, 14.5f * k, 650, false, w, WorkoutPaint.Ellipsis), p.Text(row.Amount, 12.5f * k, 500, false, w, WorkoutPaint.Ellipsis));

    /* ---------- Maus ---------- */

    public bool HitTest(UiContext c, Vector2 p)
    {
        if (_fade < 0.5f || c.Editing) return false;
        if (_fin != null && p.X >= _finRect.X && p.X < _finRect.X + _finRect.Width && p.Y >= _finRect.Y && p.Y < _finRect.Y + _finRect.Height) return true;
        foreach (var h in _hits) if (h.Contains(p)) return true;
        return false;
    }

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        if (!On || _fade < 0.5f || c.Editing) return false;
        Hit? at = null;
        foreach (var h in _hits) if (h.Contains(e.Pos)) { at = h; break; }
        bool inFin = _fin != null && e.Pos.X >= _finRect.X && e.Pos.X < _finRect.X + _finRect.Width && e.Pos.Y >= _finRect.Y && e.Pos.Y < _finRect.Y + _finRect.Height;
        switch (e.Kind)
        {
            case PointerKind.Down when e.Button == 0:
                _hasPressed = at != null;
                if (at is { } d) _pressed = d.R;
                return at != null || inFin;
            case PointerKind.Up when e.Button == 0:
                bool was = _hasPressed;
                _hasPressed = false;
                if (was && at is { } u && u.R.Equals(_pressed)) { Exec(u.Cmd, u.Arg); return true; }
                return at != null || inFin;
            case PointerKind.Move:
            case PointerKind.Wheel:
                return at != null || inFin;
        }
        return false;
    }

    private void Exec(int cmd, int arg)
    {
        switch (cmd)
        {
            case CPause: TogglePause(); break;
            case CNext: NextStep(); break;
            case CMore: MoreRest(); break;
            case CEasy: Adjust(-1); break;
            case CHard: Adjust(1); break;
            case CEnd: AskEnd(); break;
            case CAbort: AskAbort(); break;
            case CExt: if (_name.Length > 0) OpenVideo(_name); break;
            case CFeel:
                if (_fin != null && arg / 3 < _fin.Rows.Count)
                {
                    var row = _fin.Rows[arg / 3];
                    string id = FeelIds[arg % 3];
                    row.Feel = row.Feel == id ? null : id;
                    ApplyFeel(_fin, row);
                    Save();
                }
                break;
            case CFinDone: CloseFinish(); break;
        }
    }

    public void Dispose()
    {
        _p.Dispose();
        _body.Dispose();
    }
}
