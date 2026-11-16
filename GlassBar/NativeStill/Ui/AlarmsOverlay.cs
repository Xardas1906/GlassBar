using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using GlassBar.NativeStill.Audio;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Rect = Vortice.Mathematics.Rect;

namespace GlassBar.NativeStill.Ui;

// =====================================================================================================
// Timer und Wecker, gestellt an der Uhr selbst (alarms.js). Ein Klick auf die Zeit macht ihre Ziffern zu
// Rädern an derselben Stelle (Timer oder Wecker). Läuft etwas, steht neben der Zeit eine kleine Anzeige wie
// auf einem Zifferblatt. Ist es so weit, erscheint unter der Uhr eine Glaskarte und ein Klang wiederholt sich,
// bis jemand ihn stoppt.
// =====================================================================================================

/// <summary>Gespeicherter Timer ("alarms.timer"); Zeiten in Unix-Millisekunden.</summary>
internal sealed class ClockTimerData
{
    public string Status { get; set; } = "idle";   // idle | running | paused
    public long EndsAt { get; set; }
    public long Left { get; set; }
    public long Total { get; set; }
    public long Last { get; set; } = 5 * 60_000;
}

/// <summary>Ein Wecker; Days wie Date.getDay() (0 = Sonntag), leer = einmalig.</summary>
internal sealed class ClockAlarm
{
    public int Id { get; set; }
    public int H { get; set; }
    public int M { get; set; }
    public int[] Days { get; set; } = [];
    public bool On { get; set; }
    public long? Next { get; set; }

    // Nur für die Anzeige (nicht gespeichert)
    [JsonIgnore] public float Knob = -1;
    [JsonIgnore] public int TextSig = -1;
    [JsonIgnore] public string TimeText = "";
    [JsonIgnore] public string DaysText = "";
}

/// <summary>Alle Wecker ("alarms.book").</summary>
internal sealed class ClockAlarmBook
{
    public List<ClockAlarm> List { get; set; } = new();
    public int LastId { get; set; }
    public int[] LastTime { get; set; } = [7, 0];
}

/// <summary>
/// Das Wecker-Ziffernblatt, der laufende Timer und die Wecker neben der Uhr, die klingelnde Karte darunter.
/// Setzt <see cref="UiContext.StageBusy"/> (über Math.Max), solange das Ziffernblatt offen ist (Uhr und Datum
/// treten dann zurück) und lässt die Uhr beim Klingeln sanft atmen.
/// </summary>
public sealed class AlarmsOverlay : IOverlay, IDisposable
{
    private const long Sec = 1000, Min = 60 * Sec, Hour = 60 * Min;
    private const int MaxAlarms = 5;
    private const long RingFor = 10 * Min;   // ein Klingeln, das niemand beantwortet, hört von selbst auf
    private const long Late = 10 * Min;      // später als das: leise als verpasst melden
    private const string TimerKey = "alarms.timer";
    private const string BookKey = "alarms.book";

    private static readonly string[] DayShort = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    private static readonly int[] DayOrder = [1, 2, 3, 4, 5, 6, 0];
    private static readonly string[] Pad2 = Enumerable.Range(0, 60).Select(i => i.ToString("00", CultureInfo.InvariantCulture)).ToArray();
    private static readonly int[] Presets = [1, 5, 10, 25];
    private static readonly string[] PresetText = ["1 min", "5 min", "10 min", "25 min"];

    /// <summary>Wie weit die Aufgabenzeile unter der Uhr gerade zurücktreten soll (Ziffernblatt offen oder es klingelt), 0..1.</summary>
    internal static float TaskVeil;

    public int Z => 60;

    /// <summary>Klingelt gerade ein Timer oder Wecker (auch die leise Meldung „verpasst“)?</summary>
    public bool Ringing => _ring != null;

    public bool Animating =>
        _dialA > 0 || _ringA > 0 || _drag != null || _hintA is > 0 and < 1 || _dirty
        || _tCard is > 0 and < 1 || _aCard is > 0 and < 1 || _sideAnim;

    private readonly ClockUiPaint _p = new();
    private ClockTimerData _timer = new();
    private ClockAlarmBook _book = new();
    private bool _loaded;
    private double _time;
    private string? _prevTimerSound, _prevAlarmSound;
    private bool _dirty, _sideAnim;
    private double _dt;

    // ---------- Klingeln ----------
    private sealed class RingItem
    {
        public bool IsTimer;
        public int Id;
        public long At, Total;
        public bool Quiet;
        public double Since;
        public string Title = "", Sub = "", A = "", B = "";
    }
    private RingItem? _ring, _ringShown;
    private readonly List<RingItem> _queue = new();
    private double _queueAt = -1;
    private IDisposable? _sound;
    private float _ringA;

    // ---------- Ziffernblatt ----------
    private bool _dialOpen, _dial24;
    private string _mode = "timer";
    private int _editId, _days;
    private readonly Wheel[] _wheels = [new(), new(), new()];
    private int _wheelCount;
    private bool _ampm;
    private float _dialA;
    private double _idleAt;
    private int _noteSig = int.MinValue;
    private string _note = "", _goText = "Start";
    private bool _goDisabled;

    private Wheel? _drag;
    private float _dragY0, _dragLastY;
    private double _dragP0, _dragV;
    private long _dragLastT;
    private bool _dragMoved;
    private int _wheelAcc;

    // ---------- Neben der Zeit ----------
    private float _tItemA, _aItemA, _tCard, _aCard;
    private double _tCardLeave, _aCardLeave;
    private int _tTextSig = int.MinValue, _tTitleSig = int.MinValue, _aTextSig = int.MinValue;
    private string _tText = "", _tTitle = "", _tPause = "Pause", _aText = "";
    private float _arcFrac = -1;
    private ID2D1PathGeometry? _arc;
    private ID2D1Factory1? _arcFactory;

    // ---------- Hinweis ----------
    private double _hoverSince = -1;
    private float _hintA;

    // ---------- Anordnung (in Update berechnet, in Draw/OnPointer benutzt) ----------
    private struct Btn { public int Id; public Rect R; public bool Off; }
    private readonly List<Btn> _btns = new(64);
    private int _press, _hover;
    private bool _clockOk;
    private Rect _clock, _dialRect, _pillRect, _wheelsRect, _noteRect, _goRect, _tItem, _aItem, _tCardR, _aCardR, _ringR;
    private float _ccx, _ccy, _F, _wheelGhostW;
    private Vector2 _ptr;
    private bool _ptrIn;

    // Schaltflächen-Kennungen
    private const int BTabTimer = 1, BTabAlarm = 2, BGo = 3, BPause = 4, BAdd = 5, BCancel = 6, BNew = 7, BRingA = 8, BRingB = 9;
    private const int BPreset = 20, BDay = 30, BSwitch = 40, BRow = 50, BRemove = 60;

    public AlarmsOverlay() { }

    // =================================================================================================
    // Zeit und Text
    // =================================================================================================

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    private static long Ms(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();
    private static int Mod(int a, int n) => ((a % n) + n) % n;

    private static string FmtHM(int hh, int mm, bool c24) =>
        c24 ? $"{Pad2[hh]}:{Pad2[mm]}" : $"{(hh % 12 == 0 ? 12 : hh % 12)}:{Pad2[mm]} {(hh < 12 ? "AM" : "PM")}";

    private static string FmtTime(long ms, bool c24) { var d = Local(ms); return FmtHM(d.Hour, d.Minute, c24); }

    // 4:32, über eine Stunde 1:04:32
    private static string FmtLeft(long ms)
    {
        long s = Math.Max(0, (ms + Sec - 1) / Sec);
        long hh = s / 3600, mm = s % 3600 / 60, ss = s % 60;
        return hh > 0 ? $"{hh}:{Pad2[mm]}:{Pad2[ss]}" : $"{mm}:{Pad2[ss]}";
    }

    // 5 min, 1 h 30 min, 45 s
    private static string FmtSpan(long ms)
    {
        long s = (long)Math.Round(ms / (double)Sec);
        long hh = s / 3600, mm = s % 3600 / 60, ss = s % 60;
        var parts = new List<string>(3);
        if (hh > 0) parts.Add($"{hh} h");
        if (mm > 0) parts.Add($"{mm} min");
        if (ss > 0) parts.Add($"{ss} s");
        return parts.Count > 0 ? string.Join(' ', parts) : "0 s";
    }

    private static string FmtIn(long ms)
    {
        long m = Math.Max(1, (long)Math.Round(ms / (double)Min));
        return m >= 60 ? $"{m / 60}h{(m % 60 != 0 ? $" {m % 60}m" : "")}" : $"{m}m";
    }

    private static string DaysText(int[] days)
    {
        if (days.Length == 0) return "Einmalig";
        if (days.Length == 7) return "Jeden Tag";
        int mask = Mask(days);
        if (mask == 0b0111110) return "Wochentags";
        if (mask == 0b1000001) return "Wochenende";
        return string.Join(' ', DayOrder.Where(d => (mask & (1 << d)) != 0).Select(d => DayShort[d]));
    }

    private static int Mask(int[] days) { int m = 0; foreach (var d in days) m |= 1 << d; return m; }
    private static int[] FromMask(int mask) { var l = new List<int>(7); for (int d = 0; d < 7; d++) if ((mask & (1 << d)) != 0) l.Add(d); return l.ToArray(); }

    /// <summary>Wann ein Wecker nach <paramref name="from"/> das nächste Mal klingelt (heute oder an einem späteren Tag).</summary>
    private static long? NextAt(int h, int m, int dayMask, long from)
    {
        var start = Local(from).Date;
        for (int i = 0; i < 8; i++)
        {
            var c = start.AddDays(i).AddHours(h).AddMinutes(m);
            long t = Ms(c);
            if (t > from && (dayMask == 0 || (dayMask & (1 << (int)c.DayOfWeek)) != 0)) return t;
        }
        return null;
    }

    // =================================================================================================
    // Gespeicherter Zustand
    // =================================================================================================

    private void Load()
    {
        _loaded = true;
        _timer = StillState.Get(TimerKey, new ClockTimerData());
        _book = StillState.Get(BookKey, new ClockAlarmBook());
        _book.List ??= new();
        foreach (var a in _book.List) a.Days ??= [];
        if (_book.LastTime is not { Length: 2 }) _book.LastTime = [7, 0];
    }

    private void SaveTimer() => StillState.Set(TimerKey, _timer);
    private void SaveAlarms() => StillState.Set(BookKey, _book);

    private ClockAlarm? FindAlarm(int id) => _book.List.Find(x => x.Id == id);

    private ClockAlarm? NextAlarm()
    {
        ClockAlarm? best = null;
        foreach (var a in _book.List)
            if (a.On && a.Next is long n && (best == null || n < best.Next!.Value)) best = a;
        return best;
    }

    // =================================================================================================
    // Timer
    // =================================================================================================

    private void StartTimer(long span)
    {
        if (span <= 0) return;
        _timer.Status = "running";
        _timer.Total = span;
        _timer.EndsAt = NowMs() + span;
        _timer.Left = span;
        _timer.Last = span;
        SaveTimer();
    }

    private void PauseTimer()
    {
        if (_timer.Status != "running") return;
        _timer.Left = Math.Max(0, _timer.EndsAt - NowMs());
        _timer.Status = "paused";
        SaveTimer();
    }

    private void ResumeTimer()
    {
        if (_timer.Status != "paused") return;
        _timer.EndsAt = NowMs() + _timer.Left;
        _timer.Status = "running";
        SaveTimer();
    }

    private void AddTimer(long ms)
    {
        if (_timer.Status == "running") _timer.EndsAt += ms;
        else if (_timer.Status == "paused") _timer.Left += ms;
        else return;
        _timer.Total += ms;
        SaveTimer();
    }

    private void CancelTimer()
    {
        _timer.Status = "idle";
        SaveTimer();
    }

    private long TimerLeft() => _timer.Status == "running" ? Math.Max(0, _timer.EndsAt - NowMs()) : _timer.Left;

    // =================================================================================================
    // Wecker
    // =================================================================================================

    private void SaveAlarm(int hh, int mm, int dayMask)
    {
        var a = FindAlarm(_editId);
        if (a == null)
        {
            if (_book.List.Count >= MaxAlarms) return;
            a = new ClockAlarm { Id = ++_book.LastId };
            _book.List.Add(a);
        }
        a.H = hh; a.M = mm; a.Days = FromMask(dayMask); a.On = true;
        a.Next = NextAt(hh, mm, dayMask, NowMs());
        a.TextSig = -1;
        _book.LastTime = [hh, mm];
        _book.List.Sort((x, y) => (x.H * 60 + x.M).CompareTo(y.H * 60 + y.M));
        SaveAlarms();
    }

    private void SetAlarmOn(int id, bool on)
    {
        var a = FindAlarm(id);
        if (a == null) return;
        a.On = on;
        a.Next = on ? NextAt(a.H, a.M, Mask(a.Days), NowMs()) : null;
        SaveAlarms();
    }

    private void RemoveAlarm(int id)
    {
        _book.List.RemoveAll(x => x.Id == id);
        SaveAlarms();
    }

    // =================================================================================================
    // Klingeln
    // =================================================================================================

    private void Ring(UiContext c, RingItem item)
    {
        if (_ring != null) { _queue.Add(item); return; }
        bool c24 = c.Settings.Bool("clock24");
        item.Since = _time;
        _ring = _ringShown = item;
        CloseDial();
        if (item.IsTimer)
        {
            item.Title = item.Quiet ? "Timer lief ab, während du weg warst" : "Timer abgelaufen";
            item.Sub = item.Quiet ? $"{FmtSpan(item.Total)} · beendet um {FmtTime(item.At, c24)}" : FmtSpan(item.Total);
        }
        else
        {
            var a = FindAlarm(item.Id);
            item.Title = item.Quiet ? "Verpasster Wecker" : "Wecker";
            item.Sub = a != null ? $"{FmtHM(a.H, a.M, c24)} · {DaysText(a.Days)}" : FmtTime(item.At, c24);
        }
        item.A = item.Quiet ? "OK" : "Stopp";
        item.B = item.IsTimer ? "+1 min" : $"Schlummern {c.Settings.Num("snoozeMin").ToString("0.#", CultureInfo.InvariantCulture)} Min.";
        if (!item.Quiet)
        {
            // Der Klang wiederholt sich nahtlos und wächst langsam (das macht Tones.Loop)
            try { _sound = Tones.Loop(item.IsTimer ? c.Settings.Str("timerSound") : c.Settings.Str("alarmSound")); }
            catch { _sound = null; }
        }
    }

    private void Finish()
    {
        try { _sound?.Dispose(); } catch { }
        _sound = null;
        _ring = null;
        if (_queue.Count > 0) _queueAt = _time + 0.4;
    }

    private void Stop()
    {
        if (_ring == null) return;
        if (!_ring.IsTimer && FindAlarm(_ring.Id) is { } a)
        {
            if (a.Days.Length > 0) a.Next = NextAt(a.H, a.M, Mask(a.Days), NowMs());
            else { a.On = false; a.Next = null; }
            SaveAlarms();
        }
        Finish();
    }

    private void Second(UiContext c)
    {
        if (_ring == null) return;
        if (_ring.IsTimer) StartTimer(Min);
        else if (FindAlarm(_ring.Id) is { } a)
        {
            a.Next = NowMs() + (long)(Math.Max(1, c.Settings.Num("snoozeMin")) * Min);
            SaveAlarms();
        }
        Finish();
    }

    /// <summary>Die Zeit beobachten (wie check() im Original, aber jedes Bild).</summary>
    private void Check(UiContext c)
    {
        long t = NowMs();
        if (_timer.Status == "running" && t >= _timer.EndsAt)
        {
            long late = t - _timer.EndsAt;
            _timer.Status = "idle";
            SaveTimer();
            Ring(c, new RingItem { IsTimer = true, Total = _timer.Total, At = _timer.EndsAt, Quiet = late > Late });
        }
        for (int i = 0; i < _book.List.Count; i++)
        {
            var a = _book.List[i];
            if (!a.On || a.Next is not long next || t < next || (_ring is { IsTimer: false } r && r.Id == a.Id)) continue;
            long late = t - next;
            if (late > Late)
            {
                // Verpasst, während der Hintergrund schlief: sagen, dann auf den nächsten warten
                Ring(c, new RingItem { Id = a.Id, At = next, Quiet = true });
                if (a.Days.Length > 0) a.Next = NextAt(a.H, a.M, Mask(a.Days), t);
                else { a.On = false; a.Next = null; }
            }
            else
            {
                a.Next = null;   // bis gestoppt oder geschlummert wird
                Ring(c, new RingItem { Id = a.Id, At = t });
            }
            SaveAlarms();
        }
        if (_ring is { Quiet: false } rr && (_time - rr.Since) * 1000 > RingFor) Stop();
        if (_ring == null && _queueAt >= 0 && _time >= _queueAt)
        {
            _queueAt = -1;
            if (_queue.Count > 0) { var next = _queue[0]; _queue.RemoveAt(0); Ring(c, next); }
        }
    }

    // =================================================================================================
    // Ziffernblatt
    // =================================================================================================

    /// <summary>Ein Rad wie die Zeitauswahl eines Telefons: der gewählte Wert hell in der Mitte, die Nachbarn
    /// kleiner und blasser darüber und darunter. Ziehen (mit Schwung), scrollen oder auf einen Nachbarn klicken.</summary>
    private sealed class Wheel
    {
        private const double Kp = 4 * Math.PI * Math.PI / (0.3 * 0.3), Cd = 2 * 2 * Math.PI / 0.3;  // Feder: Dämpfung 1, Antwort 0,3 s
        public string[] Values = [];
        public int N;
        public bool Wrap = true;
        public double X, V, Target;
        public Rect Bounds;
        public float Size;

        public void Set(string[] values, bool wrap) { Values = values; N = values.Length; Wrap = wrap; X = V = Target = 0; }
        public int Index => Wrap ? Mod((int)Math.Round(Target), N) : Math.Clamp((int)Math.Round(Target), 0, N - 1);

        public void To(int i, bool snap)
        {
            double t = i;
            if (Wrap)
            {
                int r = (int)Math.Round(Target);
                t = r + Mod(i - r + N / 2, N) - N / 2;
            }
            if (snap) { X = Target = t; V = 0; }
            else Target = t;
        }

        public void Nudge(int d)
        {
            double t = Math.Round(Target) + d;
            if (!Wrap) t = Math.Clamp(t, 0, N - 1);
            Target = t;
        }

        public void Snap(double p) { X = Target = p; V = 0; }

        public bool Moving => Math.Abs(X - Target) > 0.0005 || Math.Abs(V) > 0.0005;

        public void Step(double dt)
        {
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += (-Kp * (X - Target) - Cd * V) * h;
                X += V * h;
            }
            if (!Moving) { X = Target; V = 0; }
            // Die Zahlen klein halten, wenn das Rad schon oft herum ist
            if (Wrap && Math.Abs(Target) > N * 4 && Math.Abs(X - Target) < 0.001)
            {
                double shift = N * Math.Round(Target / N);
                X -= shift;
                Target -= shift;
            }
        }
    }

    private static readonly string[] Hours24 = Pad2[..24];
    private static readonly string[] Hours12 = Enumerable.Range(1, 12).Select(i => i.ToString("00", CultureInfo.InvariantCulture)).ToArray();
    private static readonly string[] Minutes = Pad2;
    private static readonly string[] AmPm = ["AM", "PM"];

    private void BuildWheels(bool c24)
    {
        _ampm = false;
        if (_mode == "timer")
        {
            _wheels[0].Set(Hours24, true); _wheels[1].Set(Minutes, true); _wheels[2].Set(Minutes, true);
            _wheelCount = 3;
        }
        else if (c24)
        {
            _wheels[0].Set(Hours24, true); _wheels[1].Set(Minutes, true);
            _wheelCount = 2;
        }
        else
        {
            _wheels[0].Set(Hours12, true); _wheels[1].Set(Minutes, true); _wheels[2].Set(AmPm, false);
            _wheelCount = 3;
            _ampm = true;
        }
    }

    private (int H, int M) DialHM()
    {
        if (!_ampm) return (_wheels[0].Index, _wheels[1].Index);
        return ((_wheels[0].Index + 1) % 12 + (_wheels[2].Index == 1 ? 12 : 0), _wheels[1].Index);
    }

    private long DialSpan() => (_wheels[0].Index * 3600L + _wheels[1].Index * 60L + _wheels[2].Index) * Sec;

    private void SetWheelsHM(int hh, int mm, bool snap)
    {
        if (!_ampm) _wheels[0].To(hh, snap);
        else
        {
            _wheels[0].To((hh + 11) % 12, snap);
            _wheels[2].To(hh >= 12 ? 1 : 0, snap);
        }
        _wheels[1].To(mm, snap);
    }

    private void SetMode(string m)
    {
        _mode = m;
        BuildWheels(_dial24);
        if (m == "timer")
        {
            long s = (long)Math.Round(_timer.Last / (double)Sec);
            _wheels[0].To((int)Math.Min(23, s / 3600), true);
            _wheels[1].To((int)(s % 3600 / 60), true);
            _wheels[2].To((int)(s % 60), true);
        }
        else
        {
            var a = FindAlarm(_editId);
            int hh = a?.H ?? _book.LastTime[0], mm = a?.M ?? _book.LastTime[1];
            _days = a != null ? Mask(a.Days) : 0;
            SetWheelsHM(Math.Clamp(hh, 0, 23), Math.Clamp(mm, 0, 59), true);
        }
        _noteSig = int.MinValue;
    }

    private void Touch() => _idleAt = _time + 20;

    /// <summary>Ziffernblatt öffnen ("timer" oder "alarm"; id = Wecker zum Bearbeiten, 0 = neu).</summary>
    public void OpenDial(string mode = "timer", int id = 0)
    {
        if (!_loaded) Load();
        _editId = id;
        _dialOpen = true;
        _drag = null;
        SetMode(mode == "alarm" ? "alarm" : "timer");
        Touch();
    }

    private void CloseDial()
    {
        if (!_dialOpen) return;
        _dialOpen = false;
        _drag = null;
    }

    private void RefreshNote(UiContext c)
    {
        long now = NowMs();
        bool c24 = c.Settings.Bool("clock24");
        bool full = _mode != "timer" && _editId == 0 && _book.List.Count >= MaxAlarms;
        int sig = HashCode.Combine(_mode, _wheels[0].Index, _wheels[1].Index, _wheelCount > 2 ? _wheels[2].Index : 0, _days, full,
            now / Min, HashCode.Combine(_editId, c24));
        if (sig == _noteSig) return;
        _noteSig = sig;
        if (_mode == "timer")
        {
            long span = DialSpan();
            _goText = "Start";
            _goDisabled = span == 0;
            _note = span > 0 ? $"Endet um {FmtTime(now + span, c24)}" : "Dreh an den Rädern oder wähle eine Zeit";
        }
        else
        {
            var (hh, mm) = DialHM();
            long next = NextAt(hh, mm, _days, now) ?? now;
            _goText = _editId != 0 ? "Speichern" : "Stellen";
            _goDisabled = full;
            // Innerhalb eines Tages: wie lange; weiter weg: an welchem Tag
            string when = next - now < 20 * Hour ? $"klingelt in {FmtIn(next - now)}" : $"klingelt {DayShort[(int)Local(next).DayOfWeek]} {FmtTime(next, c24)}";
            _note = full ? $"Schon {MaxAlarms} Wecker. Entferne einen in der Liste neben der Uhr." : $"{DaysText(FromMask(_days))} · {when}";
        }
    }

    // =================================================================================================
    // Bild für Bild
    // =================================================================================================

    public void Update(UiContext c, double dt)
    {
        dt = Math.Clamp(dt, 0, 0.25);
        _dt = dt;
        _time = c.Time;
        if (!_loaded) Load();
        _p.Begin(c);
        _dirty = false;

        // Klänge: beim Ändern einmal vorspielen (wie S.sounds.preview)
        string ts = c.Settings.Str("timerSound"), als = c.Settings.Str("alarmSound");
        if (_prevTimerSound != null && ts != _prevTimerSound) { try { Tones.Play(ts, 0.8f); } catch { } }
        if (_prevAlarmSound != null && als != _prevAlarmSound) { try { Tones.Play(als, 0.8f); } catch { } }
        _prevTimerSound = ts; _prevAlarmSound = als;

        bool c24 = c.Settings.Bool("clock24");
        if (_dialOpen && c24 != _dial24) { _dial24 = c24; SetMode(_mode); }
        if (!_dialOpen) _dial24 = c24;

        Check(c);

        if (_dialOpen && _time >= _idleAt && _drag == null) CloseDial();
        if (_dialOpen && c.Editing) CloseDial();

        float a0 = _dialA;
        _dialA = Approach(_dialA, _dialOpen ? 1 : 0, (float)(dt / 0.35));
        if (_dialA > 0) for (int i = 0; i < _wheelCount; i++) if (_drag != _wheels[i]) _wheels[i].Step(dt);
        if (_dialOpen) RefreshNote(c);

        _ringA = Approach(_ringA, _ring != null ? 1 : 0, (float)(dt / 0.35));
        if (_ringA <= 0 && _ring == null) _ringShown = null;

        _ptr = c.Pointer;
        _ptrIn = c.PointerInside;

        Layout(c);

        // Hinweis: nach kurzem Verweilen über der Zeit
        bool tap = c.Settings.Bool("clockTap") && _clockOk && !c.Editing;
        bool overClock = tap && _ptrIn && _clock.Contains(_ptr) && !_dialOpen && _ring == null && _drag == null;
        if (overClock) { if (_hoverSince < 0) _hoverSince = _time; }
        else _hoverSince = -1;
        _hintA = Approach(_hintA, overClock && _time - _hoverSince > 0.7 ? 1 : 0, (float)(dt / 0.3));

        // Karten neben der Zeit (öffnen beim Drüberfahren, schließen mit kurzer Verzögerung)
        bool tHover = _ptrIn && _tItemA > 0.5f && (_tItem.Contains(_ptr) || (_tCard > 0.05f && Grow(_tCardR, _tItem).Contains(_ptr)));
        bool aHover = _ptrIn && _aItemA > 0.5f && (_aItem.Contains(_ptr) || (_aCard > 0.05f && Grow(_aCardR, _aItem).Contains(_ptr)));
        if (_dialA > 0.05f) tHover = aHover = false;
        if (tHover) _tCardLeave = _time;
        if (aHover) _aCardLeave = _time;
        _tCard = Approach(_tCard, tHover || _time - _tCardLeave < 0.12 ? 1 : 0, (float)(dt / 0.2));
        _aCard = Approach(_aCard, aHover || _time - _aCardLeave < 0.12 ? 1 : 0, (float)(dt / 0.2));

        foreach (var al in _book.List)
        {
            float target = al.On ? 1 : 0;
            if (al.Knob < 0) al.Knob = target;
            else al.Knob = Approach(al.Knob, target, (float)(dt / 0.25));
        }

        _hover = _ptrIn ? HitButton(_ptr) : 0;

        // Bühne: das Ziffernblatt nimmt den Platz der Zeit ein; beim Klingeln atmet die Uhr
        float busy = Ease(_dialA);
        if (_ring is { Quiet: false })
        {
            double ph = (_time - _ring.Since) / 2.4;
            busy = Math.Max(busy, 0.4f * (float)(0.5 - 0.5 * Math.Cos(ph * 2 * Math.PI)));
        }
        if (busy > 0) c.StageBusy = Math.Max(c.StageBusy, busy);
        TaskVeil = Math.Max(_dialA, _ringA);

        _sideAnim = _tItemA is > 0 and < 1 || _aItemA is > 0 and < 1 || _book.List.Exists(x => Math.Abs(x.Knob - (x.On ? 1 : 0)) > 0.001f);
        if (a0 != _dialA) _dirty = true;
        for (int i = 0; i < _wheelCount; i++) if (_wheels[i].Moving) _dirty = true;
    }

    private static float Approach(float v, float target, float step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    private static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    private static Rect Grow(Rect card, Rect item) =>
        Rect.FromLTRB(Math.Min(card.Left, item.Left), Math.Min(card.Top, item.Top), Math.Max(card.Right, item.Right), Math.Max(card.Bottom, item.Bottom));

    private void AddBtn(int id, Rect r, bool off = false) => _btns.Add(new Btn { Id = id, R = r, Off = off });

    private int HitButton(Vector2 p)
    {
        for (int i = _btns.Count - 1; i >= 0; i--)
            if (!_btns[i].Off && _btns[i].R.Contains(p)) return _btns[i].Id;
        return 0;
    }

    private bool BtnRect(int id, out Rect r, out bool off)
    {
        foreach (var b in _btns) if (b.Id == id) { r = b.R; off = b.Off; return true; }
        r = default; off = true;
        return false;
    }

    private void Layout(UiContext c)
    {
        _btns.Clear();
        float K = c.K;
        _clock = c.ClockRect;
        _clockOk = _clock.Width > 1 && _clock.Height > 1 && c.Settings.Bool("showClock");
        _ccx = _clockOk ? _clock.X + _clock.Width / 2 : c.Width / 2f;
        _ccy = _clockOk ? _clock.Y + _clock.Height / 2 : c.Height * 0.42f;
        bool c24 = c.Settings.Bool("clock24");

        // ---------- Ring-Karte unter der Uhr ----------
        if (_ringShown is { } ri)
        {
            var fT = _p.Fmt(c, 16 * K, 650);
            var fS = _p.Fmt(c, 13 * K, 500);
            var fB = _p.Fmt(c, 13 * K, 650);
            var t = _p.Size(c, ri.Title, fT);
            var s = _p.Size(c, ri.Sub, fS);
            float bA = _p.Size(c, ri.A, fB).X + 36 * K;
            float bB = ri.Quiet ? 0 : _p.Size(c, ri.B, fB).X + 36 * K;
            float actW = bA + (ri.Quiet ? 0 : 8 * K + bB);
            float w = Math.Max(260 * K, Math.Max(Math.Max(t.X, s.X), actW) + 36 * K);
            float h = 14 * K + t.Y + 3 * K + s.Y + 12 * K + 34 * K + 14 * K;
            float top = (_clockOk ? _clock.Bottom : _ccy) + 16 * K + (1 - Ease(_ringA)) * 8 * K;
            _ringR = new Rect(_ccx - w / 2, top, w, h);
            float by = top + h - 14 * K - 34 * K;
            float bx = _ccx - actW / 2;
            bool live = _ring != null;
            AddBtn(BRingA, new Rect(bx, by, bA, 34 * K), !live);
            if (!ri.Quiet) AddBtn(BRingB, new Rect(bx + bA + 8 * K, by, bB, 34 * K), !live);
        }

        // ---------- Neben der Zeit ----------
        bool tOn = _timer.Status != "idle" && _clockOk;
        var na = _clockOk ? NextAlarm() : null;
        float sideStep = (float)(_dt / 0.3);
        _tItemA = Approach(_tItemA, tOn ? 1 : 0, sideStep);
        _aItemA = Approach(_aItemA, na != null ? 1 : 0, sideStep);
        var fSide = _p.Fmt(c, 16 * K, 550);
        float lineH = 16 * K * 1.2f, icon = 16 * K * 1.05f;
        if (tOn)
        {
            long left = TimerLeft();
            int sig = (int)((left + Sec - 1) / Sec);
            if (sig != _tTextSig) { _tTextSig = sig; _tText = FmtLeft(left); _dirty = true; }
            int tsig = HashCode.Combine(_timer.Total, _timer.Status);
            if (tsig != _tTitleSig)
            {
                _tTitleSig = tsig;
                _tTitle = $"Timer · {FmtSpan(_timer.Total)}{(_timer.Status == "paused" ? " · pausiert" : "")}";
                _tPause = _timer.Status == "running" ? "Pause" : "Fortsetzen";
            }
        }
        if (na != null)
        {
            long t = NowMs(), next = na.Next!.Value;
            int sig = HashCode.Combine(next, c24, Local(t).Date);
            if (sig != _aTextSig)
            {
                _aTextSig = sig;
                var d = Local(next);
                var today = Local(t).Date;
                bool sameDay = d.Date == today, tomorrow = d.Date == today.AddDays(1);
                _aText = sameDay || (tomorrow && next - t < 18 * Hour) ? FmtTime(next, c24) : $"{DayShort[(int)d.DayOfWeek]} {FmtTime(next, c24)}";
            }
        }
        int n = (_tItemA > 0 ? 1 : 0) + (_aItemA > 0 ? 1 : 0);
        float x0 = (_clockOk ? _clock.Right : _ccx) + 16 * K;
        float y = _ccy - (n * lineH + Math.Max(0, n - 1) * 6 * K) / 2;
        _tItem = _aItem = default;
        if (_tItemA > 0)
        {
            _tItem = new Rect(x0, y, icon + 7 * K + _p.Size(c, _tText, fSide).X, lineH);
            y += lineH + 6 * K;
        }
        if (_aItemA > 0) _aItem = new Rect(x0, y, icon + 7 * K + _p.Size(c, _aText, fSide).X, lineH);

        var fCard = _p.Fmt(c, 12.5f * K, 600);
        if (_tItemA > 0)
        {
            var title = _p.Size(c, _tTitle, fCard);
            float b1 = _p.Size(c, _tPause, fCard).X + 24 * K, b2 = _p.Size(c, "+1 min", fCard).X + 24 * K, b3 = _p.Size(c, "Abbrechen", fCard).X + 24 * K;
            float actW = b1 + b2 + b3 + 12 * K;
            float w = Math.Max(230 * K, Math.Max(title.X, actW) + 28 * K);
            float h = 12 * K + title.Y + 10 * K + 28 * K + 12 * K;
            float top = _tItem.Bottom + 8 * K - (1 - _tCard) * 4 * K;
            _tCardR = new Rect(_tItem.X, top, w, h);
            bool off = _tCard < 0.5f;
            float by = top + 12 * K + title.Y + 10 * K, bx = _tItem.X + 14 * K;
            AddBtn(BPause, new Rect(bx, by, b1, 28 * K), off); bx += b1 + 6 * K;
            AddBtn(BAdd, new Rect(bx, by, b2, 28 * K), off); bx += b2 + 6 * K;
            AddBtn(BCancel, new Rect(bx, by, b3, 28 * K), off);
        }
        if (_aItemA > 0)
        {
            var fTime = _p.Fmt(c, 15 * K, 600);
            var fDays = _p.Fmt(c, 12.5f * K, 500);
            float timeW = 0, daysW = 0, rowH = Math.Max(15 * K * 1.25f, 18 * K);
            foreach (var al in _book.List)
            {
                int sig = HashCode.Combine(al.H, al.M, Mask(al.Days), c24);
                if (sig != al.TextSig) { al.TextSig = sig; al.TimeText = FmtHM(al.H, al.M, c24); al.DaysText = DaysText(al.Days); }
                timeW = Math.Max(timeW, _p.Size(c, al.TimeText, fTime).X);
                daysW = Math.Max(daysW, _p.Size(c, al.DaysText, fDays).X);
            }
            float rm = 18 * K;   // kleines × zum Entfernen
            float listH = _book.List.Count > 0 ? _book.List.Count * rowH + (_book.List.Count - 1) * 6 * K + 10 * K : 0;
            float bNew = _p.Size(c, "Neuer Wecker", fCard).X + 24 * K;
            float w = Math.Max(230 * K, Math.Max(timeW + 10 * K + daysW + 10 * K + 30 * K + 8 * K + rm, bNew) + 28 * K);
            float h = 12 * K + listH + 28 * K + 12 * K;
            float top = _aItem.Bottom + 8 * K - (1 - _aCard) * 4 * K;
            _aCardR = new Rect(_aItem.X, top, w, h);
            bool off = _aCard < 0.5f;
            float ry = top + 12 * K, left = _aItem.X + 14 * K, right = _aItem.X + w - 14 * K;
            for (int i = 0; i < _book.List.Count; i++)
            {
                AddBtn(BRow + i, new Rect(left, ry, timeW + 10 * K + daysW, rowH), off);
                AddBtn(BSwitch + i, new Rect(right - rm - 8 * K - 30 * K, ry + (rowH - 18 * K) / 2, 30 * K, 18 * K), off);
                AddBtn(BRemove + i, new Rect(right - rm, ry + (rowH - rm) / 2, rm, rm), off);
                ry += rowH + 6 * K;
            }
            AddBtn(BNew, new Rect(left, top + 12 * K + listH, bNew, 28 * K), off);
        }

        // ---------- Ziffernblatt ----------
        if (_dialA > 0) LayoutDial(c);
        else _dialRect = default;
    }

    private void LayoutDial(UiContext c)
    {
        float K = c.K;
        float clockFont = (float)(188 * K * Math.Max(0.05, c.Settings.Num("clockScale")));
        float F = _F = Math.Min(clockFont, 124 * K);

        // Oben: Timer | Wecker
        var fTab = _p.Fmt(c, 13 * K, 600);
        float wT = _p.Size(c, "Timer", fTab).X + 32 * K, wA = _p.Size(c, "Wecker", fTab).X + 32 * K;
        float pillW = wT + wA + 2 * K + 6 * K, pillH = 36 * K;

        // Räder
        var fWheel = WheelFormat(c, F);
        _wheelGhostW = _p.Size(c, "00", fWheel).X;
        float colonW = _p.Size(c, ":", fWheel).X + 0.12f * F;
        float wheelsW = 0;
        for (int i = 0; i < _wheelCount; i++)
        {
            bool amp = _ampm && i == 2;
            if (i > 0 && !amp) wheelsW += colonW;
            if (amp) wheelsW += 0.09f * F + _p.Size(c, "PM", _p.Fmt(c, 0.3f * F, 600, true)).X;
            else wheelsW += _wheelGhostW;
        }
        float wheelsH = 2.6f * F;

        // Darunter: Vorschläge oder Tage, Hinweis, Knopf
        var fChip = _p.Fmt(c, 12.5f * K, 600);
        var fNote = _p.Fmt(c, 12.5f * K, 500);
        var fGo = _p.Fmt(c, 14 * K, 650);
        float extraH = _mode == "timer" ? 28 * K : 30 * K;
        var noteS = _p.Size(c, _note, fNote);
        float goW = Math.Max(120 * K, _p.Size(c, _goText, fGo).X + 44 * K), goH = 38 * K;

        float total = pillH + 14 * K + wheelsH + 14 * K + extraH + 14 * K - 4 * K + noteS.Y + 14 * K + goH;
        float top = _ccy - total / 2;
        float y = top;

        _pillRect = new Rect(_ccx - pillW / 2, y, pillW, pillH);
        AddBtn(BTabTimer, new Rect(_pillRect.X + 3 * K, y + 3 * K, wT, 30 * K));
        AddBtn(BTabAlarm, new Rect(_pillRect.X + 3 * K + wT + 2 * K, y + 3 * K, wA, 30 * K));
        y += pillH + 14 * K;

        _wheelsRect = new Rect(_ccx - wheelsW / 2, y, wheelsW, wheelsH);
        float x = _wheelsRect.X;
        for (int i = 0; i < _wheelCount; i++)
        {
            bool amp = _ampm && i == 2;
            if (i > 0 && !amp) x += colonW;
            var w = _wheels[i];
            if (amp)
            {
                x += 0.09f * F;
                float ww = _p.Size(c, "PM", _p.Fmt(c, 0.3f * F, 600, true)).X;
                w.Bounds = new Rect(x, y, ww, wheelsH);
                w.Size = 0.3f * F;
                x += ww;
            }
            else
            {
                w.Bounds = new Rect(x, y, _wheelGhostW, wheelsH);
                w.Size = F;
                x += _wheelGhostW;
            }
        }
        y += wheelsH + 14 * K;

        if (_mode == "timer")
        {
            float tw = 0;
            Span<float> cw = stackalloc float[4];
            for (int i = 0; i < 4; i++) { cw[i] = _p.Size(c, PresetText[i], fChip).X + 26 * K; tw += cw[i]; }
            tw += 3 * 6 * K;
            float cx = _ccx - tw / 2;
            for (int i = 0; i < 4; i++) { AddBtn(BPreset + i, new Rect(cx, y, cw[i], 28 * K)); cx += cw[i] + 6 * K; }
        }
        else
        {
            float tw = 7 * 30 * K + 6 * 6 * K, cx = _ccx - tw / 2;
            for (int i = 0; i < 7; i++) { AddBtn(BDay + DayOrder[i], new Rect(cx, y, 30 * K, 30 * K)); cx += 36 * K; }
        }
        y += extraH + 14 * K - 4 * K;

        _noteRect = new Rect(_ccx - noteS.X / 2, y, noteS.X, noteS.Y);
        y += noteS.Y + 14 * K;

        _goRect = new Rect(_ccx - goW / 2, y, goW, goH);
        AddBtn(BGo, _goRect, _goDisabled);

        float dw = Math.Max(Math.Max(pillW, wheelsW), Math.Max(goW, Math.Max(noteS.X, 7 * 36 * K)));
        _dialRect = new Rect(_ccx - dw / 2 - 12 * K, top - 12 * K, dw + 24 * K, total + 24 * K);
    }

    private IDWriteTextFormat WheelFormat(UiContext c, float F)
    {
        int weight = c.Settings.Str("clockWeight") switch { "thin" => 200, "light" => 300, "regular" => 400, "medium" => 500, _ => 650 };
        return _p.Fmt(c, F, weight, true);
    }

    // =================================================================================================
    // Maus
    // =================================================================================================

    public bool HitTest(UiContext c, Vector2 p)
    {
        if (_drag != null || _dialOpen) return true;   // offen: ein Klick daneben schließt es
        if (_ring != null && _ringR.Contains(p)) return true;
        if (_tItemA > 0.5f && (_tItem.Contains(p) || (_tCard > 0.5f && Grow(_tCardR, _tItem).Contains(p)))) return true;
        if (_aItemA > 0.5f && (_aItem.Contains(p) || (_aCard > 0.5f && Grow(_aCardR, _aItem).Contains(p)))) return true;
        return c.Settings.Bool("clockTap") && _clockOk && !c.Editing && _ring == null && _clock.Contains(p);
    }

    public bool OnPointer(UiContext c, PointerEvent e)
    {
        _ptr = e.Pos;
        switch (e.Kind)
        {
            case PointerKind.Move:
                if (_drag != null) { DragMove(e.Pos); return true; }
                return false;

            case PointerKind.Wheel:
                if (!_dialOpen) return false;
                Touch();
                for (int i = 0; i < _wheelCount; i++)
                {
                    if (!_wheels[i].Bounds.Contains(e.Pos)) continue;
                    // Eine Raste, ein Schritt; kleine Schritte (Touchpad) summieren sich
                    _wheelAcc += e.Wheel;
                    while (Math.Abs(_wheelAcc) >= 120)
                    {
                        int d = Math.Sign(_wheelAcc);
                        _wheels[i].Nudge(-d);
                        _wheelAcc -= d * 120;
                    }
                    return true;
                }
                return _dialRect.Contains(e.Pos);

            case PointerKind.Down:
                return Down(c, e);

            case PointerKind.Up:
                if (_drag != null) { DragUp(e.Pos); return true; }
                if (_press != 0)
                {
                    int id = _press;
                    _press = 0;
                    if (HitButton(e.Pos) == id) Activate(c, id);
                    return true;
                }
                return false;

            case PointerKind.Leave:
                if (_drag != null) DragUp(e.Pos);
                _press = 0;
                return false;
        }
        return false;
    }

    private bool Down(UiContext c, PointerEvent e)
    {
        var p = e.Pos;
        _press = 0;
        if (_dialOpen)
        {
            if (!_dialRect.Contains(p)) { CloseDial(); return false; }
            Touch();
            if (e.Button != 0) return true;
            for (int i = 0; i < _wheelCount; i++)
            {
                var w = _wheels[i];
                if (!w.Bounds.Contains(p)) continue;
                _drag = w;
                _dragY0 = _dragLastY = p.Y;
                _dragP0 = w.X;
                _dragV = 0;
                _dragLastT = Stopwatch.GetTimestamp();
                _dragMoved = false;
                return true;
            }
            _press = HitButton(p);
            return true;
        }
        if (e.Button != 0) return false;

        int id = HitButton(p);
        if (id != 0) { _press = id; return true; }
        if (_ring != null && _ringR.Contains(p)) return true;
        if (_tCard > 0.5f && _tCardR.Contains(p)) return true;
        if (_aCard > 0.5f && _aCardR.Contains(p)) return true;
        if (_tItemA > 0.5f && _tItem.Contains(p)) return true;
        if (_aItemA > 0.5f && _aItem.Contains(p)) return true;

        if (c.Settings.Bool("clockTap") && _clockOk && !c.Editing && _ring == null && _clock.Contains(p))
        {
            OpenDial("timer");
            return true;
        }
        return false;
    }

    private float RowPx(Wheel w) => w.Size * 0.9f;

    private void DragMove(Vector2 p)
    {
        var w = _drag!;
        if (Math.Abs(p.Y - _dragY0) > 4) _dragMoved = true;
        if (!_dragMoved) return;
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Max(0.001, (now - _dragLastT) / (double)Stopwatch.Frequency);
        float px = RowPx(w);
        _dragV = 0.7 * _dragV + 0.3 * (-(p.Y - _dragLastY) / px / dt);
        _dragLastY = p.Y;
        _dragLastT = now;
        double pos = _dragP0 - (p.Y - _dragY0) / px;
        if (!w.Wrap) pos = Math.Clamp(pos, -0.4, w.N - 0.6);
        w.Snap(pos);
        Touch();
    }

    private void DragUp(Vector2 p)
    {
        var w = _drag!;
        _drag = null;
        Touch();
        float px = RowPx(w);
        if (!_dragMoved)
        {
            // Ein Klick über oder unter der Mitte geht einen Schritt dorthin
            float dy = p.Y - (w.Bounds.Y + w.Bounds.Height / 2);
            if (Math.Abs(dy) > px * 0.45f) w.Nudge(Math.Sign(dy));
            return;
        }
        // Ein Schwung läuft ein wenig weiter und bleibt auf einem ganzen Wert stehen
        if ((Stopwatch.GetTimestamp() - _dragLastT) / (double)Stopwatch.Frequency > 0.09) _dragV = 0;
        double t = Math.Round(w.X + Math.Clamp(_dragV, -24, 24) * 0.1);
        if (!w.Wrap) t = Math.Clamp(t, 0, w.N - 1);
        w.Target = t;
    }

    private void Activate(UiContext c, int id)
    {
        Touch();
        switch (id)
        {
            case BTabTimer: _editId = 0; SetMode("timer"); return;
            case BTabAlarm: SetMode("alarm"); return;
            case BGo:
                if (_goDisabled) return;
                if (_mode == "timer") StartTimer(DialSpan());
                else { var (hh, mm) = DialHM(); SaveAlarm(hh, mm, _days); }
                CloseDial();
                return;
            case BPause: if (_timer.Status == "running") PauseTimer(); else ResumeTimer(); _tTitleSig = int.MinValue; return;
            case BAdd: AddTimer(Min); return;
            case BCancel: CancelTimer(); return;
            case BNew: _aCardLeave = -10; OpenDial("alarm"); return;
            case BRingA: Stop(); return;
            case BRingB: Second(c); return;
        }
        if (id >= BPreset && id < BPreset + 4)
        {
            _wheels[0].To(0, false);
            _wheels[1].To(Presets[id - BPreset], false);
            _wheels[2].To(0, false);
        }
        else if (id >= BDay && id < BDay + 7) _days ^= 1 << (id - BDay);
        else if (id >= BSwitch && id < BSwitch + MaxAlarms * 4 && id - BSwitch < _book.List.Count)
        {
            var a = _book.List[id - BSwitch];
            SetAlarmOn(a.Id, !a.On);
        }
        else if (id >= BRow && id < BRow + 10 && id - BRow < _book.List.Count)
        {
            _aCardLeave = -10;
            OpenDial("alarm", _book.List[id - BRow].Id);
        }
        else if (id >= BRemove && id < BRemove + 10 && id - BRemove < _book.List.Count) RemoveAlarm(_book.List[id - BRemove].Id);
    }

    // =================================================================================================
    // Zeichnen
    // =================================================================================================

    public void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        bool any = _dialA > 0 || _ringShown != null || _tItemA > 0 || _aItemA > 0 || _hintA > 0;
        if (!any) return;
        _p.Device(ctx, c);
        var prev = ctx.Transform;
        try
        {
            float sideA = 1 - Ease(_dialA);
            if (_hintA > 0 && _clockOk) DrawHint(ctx, c);
            if (sideA > 0) DrawSide(ctx, c, sideA, prev);
            if (_ringShown != null && _ringA > 0) DrawRing(ctx, c);
            if (_dialA > 0) DrawDial(ctx, c, prev);
        }
        finally { ctx.Transform = prev; }
    }

    private void DrawHint(ID2D1DeviceContext ctx, UiContext c)
    {
        float K = c.K;
        const string text = "Klicken, um einen Timer oder Wecker zu stellen";
        var f = _p.Fmt(c, 12.5f * K, 500);
        var s = _p.Size(c, text, f);
        _p.Text(ctx, c, text, f, _ccx - s.X / 2, _clock.Bottom + 2 * K, c.Fg, 0.5f * _hintA);
    }

    private void Pill(ID2D1DeviceContext ctx, Rect r, Vector3 rgb, float a)
    {
        if (a <= 0) return;
        float rad = Math.Min(r.Width, r.Height) / 2;
        ctx.FillRoundedRectangle(new RoundedRectangle((System.Drawing.RectangleF)r, rad, rad), _p.Brush(rgb, a));
    }

    private void PillLine(ID2D1DeviceContext ctx, Rect r, Vector3 rgb, float a, float w = 1)
    {
        if (a <= 0) return;
        float rad = Math.Min(r.Width, r.Height) / 2;
        var rr = new Rect(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
        ctx.DrawRoundedRectangle(new RoundedRectangle((System.Drawing.RectangleF)rr, rad - w / 2, rad - w / 2), _p.Brush(rgb, a), w);
    }

    private void CenterText(ID2D1DeviceContext ctx, UiContext c, string s, IDWriteTextFormat f, Rect r, Vector3 rgb, float a)
    {
        var sz = _p.Size(c, s, f);
        _p.Text(ctx, c, s, f, r.X + (r.Width - sz.X) / 2, r.Y + (r.Height - sz.Y) / 2, rgb, a);
    }

    private void DrawSide(ID2D1DeviceContext ctx, UiContext c, float sideA, Matrix3x2 baseT)
    {
        float K = c.K;
        var fSide = _p.Fmt(c, 16 * K, 550);
        float icon = 16 * K * 1.05f;
        var white = new Vector3(1, 1, 1);

        if (_tItemA > 0)
        {
            float a = sideA * _tItemA;
            float ix = _tItem.X, iy = _tItem.Y + (_tItem.Height - icon) / 2;
            float sc = icon / 20f;
            ctx.Transform = Matrix3x2.CreateScale(sc) * Matrix3x2.CreateTranslation(ix, iy) * baseT;
            ctx.DrawEllipse(new Ellipse(new Vector2(10, 10), 7.6f, 7.6f), _p.Brush(c.Fg, 0.2f * a), 1.7f, _p.Round);
            float frac = Math.Clamp(TimerLeft() / (float)Math.Max(1, _timer.Total), 0, 1);
            if (frac >= 0.999f) ctx.DrawEllipse(new Ellipse(new Vector2(10, 10), 7.6f, 7.6f), _p.Brush(c.Accent, a), 1.7f, _p.Round);
            else if (frac > 0.002f) ctx.DrawGeometry(Arc(c, frac), _p.Brush(c.Accent, a), 1.7f, _p.Round);
            ctx.Transform = baseT;
            float ta = _timer.Status == "paused" ? 0.5f : 1f;
            var s = _p.Size(c, _tText, fSide);
            _p.Text(ctx, c, _tText, fSide, ix + icon + 7 * K, _tItem.Y + (_tItem.Height - s.Y) / 2, c.Fg, 0.62f * a * ta);
        }
        if (_aItemA > 0)
        {
            float a = sideA * _aItemA;
            float ix = _aItem.X, iy = _aItem.Y + (_aItem.Height - icon) / 2;
            ctx.Transform = Matrix3x2.CreateScale(icon / 20f) * Matrix3x2.CreateTranslation(ix, iy) * baseT;
            var b = _p.Brush(c.Fg, 0.62f * a);
            var st = _p.Round;
            ctx.DrawEllipse(new Ellipse(new Vector2(10, 10.8f), 6.4f, 6.4f), b, 1.7f, st);
            ctx.DrawLine(new Vector2(10, 7.6f), new Vector2(10, 11), b, 1.7f, st);
            ctx.DrawLine(new Vector2(10, 11), new Vector2(12.2f, 12.4f), b, 1.7f, st);
            ctx.DrawLine(new Vector2(3.6f, 4.6f), new Vector2(6, 2.6f), b, 1.7f, st);
            ctx.DrawLine(new Vector2(16.4f, 4.6f), new Vector2(14, 2.6f), b, 1.7f, st);
            ctx.DrawLine(new Vector2(5.6f, 16.4f), new Vector2(4.4f, 17.8f), b, 1.7f, st);
            ctx.DrawLine(new Vector2(14.4f, 16.4f), new Vector2(15.6f, 17.8f), b, 1.7f, st);
            ctx.Transform = baseT;
            var s = _p.Size(c, _aText, fSide);
            _p.Text(ctx, c, _aText, fSide, ix + icon + 7 * K, _aItem.Y + (_aItem.Height - s.Y) / 2, c.Fg, 0.62f * a);
        }

        var fCard = _p.Fmt(c, 12.5f * K, 600);
        if (_tItemA > 0 && _tCard > 0)
        {
            float a = sideA * _tItemA * _tCard;
            c.Glass.Panel(ctx, c, _tCardR, 18 * K, a);
            _p.Text(ctx, c, _tTitle, fCard, _tCardR.X + 14 * K, _tCardR.Y + 12 * K, c.Fg, 0.9f * a);
            CardButton(ctx, c, BPause, _tPause, fCard, a);
            CardButton(ctx, c, BAdd, "+1 min", fCard, a);
            CardButton(ctx, c, BCancel, "Abbrechen", fCard, a);
        }
        if (_aItemA > 0 && _aCard > 0)
        {
            float a = sideA * _aItemA * _aCard;
            c.Glass.Panel(ctx, c, _aCardR, 18 * K, a);
            var fTime = _p.Fmt(c, 15 * K, 600);
            var fDays = _p.Fmt(c, 12.5f * K, 500);
            float timeW = 0;
            foreach (var al in _book.List) timeW = Math.Max(timeW, _p.Size(c, al.TimeText, fTime).X);
            for (int i = 0; i < _book.List.Count; i++)
            {
                var al = _book.List[i];
                if (!BtnRect(BRow + i, out var row, out _)) continue;
                float dim = al.On ? 1 : 0.45f;
                bool hov = _hover == BRow + i;
                var ts = _p.Size(c, al.TimeText, fTime);
                _p.Text(ctx, c, al.TimeText, fTime, row.X, row.Y + (row.Height - ts.Y) / 2, c.Fg, (hov ? 1f : 0.92f) * dim * a);
                var ds = _p.Size(c, al.DaysText, fDays);
                _p.Text(ctx, c, al.DaysText, fDays, row.X + timeW + 10 * K, row.Y + (row.Height - ds.Y) / 2, c.Fg, 0.7f * dim * a);

                if (BtnRect(BSwitch + i, out var sw, out _))
                {
                    float kn = Math.Clamp(al.Knob, 0, 1);
                    var bg = Vector3.Lerp(c.Fg, c.Accent, kn);
                    Pill(ctx, sw, bg, (0.16f + (0.6f - 0.16f) * kn) * a);
                    float r = 7 * K;
                    var center = new Vector2(sw.X + 2 * K + r + 12 * K * EaseSpring(kn), sw.Y + sw.Height / 2);
                    ctx.FillEllipse(new Ellipse(center, r, r), _p.Brush(new Vector3(1, 1, 1), a));
                }
                if (BtnRect(BRemove + i, out var rm, out _))
                {
                    bool h = _hover == BRemove + i;
                    if (h) ctx.FillEllipse(new Ellipse(new Vector2(rm.X + rm.Width / 2, rm.Y + rm.Height / 2), rm.Width / 2, rm.Height / 2), _p.Brush(c.Fg, 0.12f * a));
                    float q = rm.Width * 0.2f;
                    var m = new Vector2(rm.X + rm.Width / 2, rm.Y + rm.Height / 2);
                    var xb = _p.Brush(c.Fg, (h ? 0.85f : 0.4f) * a);
                    ctx.DrawLine(m + new Vector2(-q, -q), m + new Vector2(q, q), xb, 1.4f * K, _p.Round);
                    ctx.DrawLine(m + new Vector2(q, -q), m + new Vector2(-q, q), xb, 1.4f * K, _p.Round);
                }
            }
            CardButton(ctx, c, BNew, "Neuer Wecker", fCard, a);
        }
    }

    private static float EaseSpring(float t)
    {
        // cubic-bezier(.3, 1.3, .5, 1) grob: leichtes Überschwingen
        t = Math.Clamp(t, 0, 1);
        return t + 0.25f * MathF.Sin(MathF.PI * t) * (1 - t);
    }

    private void CardButton(ID2D1DeviceContext ctx, UiContext c, int id, string label, IDWriteTextFormat f, float a)
    {
        if (!BtnRect(id, out var r, out _)) return;
        bool h = _hover == id;
        Pill(ctx, r, c.Fg, (h ? 0.16f : 0.09f) * a);
        CenterText(ctx, c, label, f, r, c.Fg, 0.85f * a);
    }

    private ID2D1Geometry Arc(UiContext c, float frac)
    {
        float q = MathF.Round(frac * 600) / 600;
        if (_arc != null && q == _arcFrac && _arcFactory == c.D2D) return _arc;
        _arc?.Dispose();
        _arcFrac = q;
        _arcFactory = c.D2D;
        _arc = c.D2D.CreatePathGeometry();
        using var sink = _arc.Open();
        float th = q * MathF.Tau;
        sink.BeginFigure(new Vector2(10, 2.4f), FigureBegin.Hollow);
        sink.AddArc(new ArcSegment
        {
            Point = new Vector2(10 + 7.6f * MathF.Sin(th), 10 - 7.6f * MathF.Cos(th)),
            Size = new Size(7.6f, 7.6f),
            RotationAngle = 0,
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = q > 0.5f ? ArcSize.Large : ArcSize.Small,
        });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return _arc;
    }

    private void DrawRing(ID2D1DeviceContext ctx, UiContext c)
    {
        var ri = _ringShown!;
        float K = c.K, a = Ease(_ringA);
        c.Glass.Panel(ctx, c, _ringR, 22 * K, a);
        var fT = _p.Fmt(c, 16 * K, 650);
        var fS = _p.Fmt(c, 13 * K, 500);
        var fB = _p.Fmt(c, 13 * K, 650);
        var t = _p.Size(c, ri.Title, fT);
        _p.Text(ctx, c, ri.Title, fT, _ccx - t.X / 2, _ringR.Y + 14 * K, c.Fg, 0.95f * a);
        var s = _p.Size(c, ri.Sub, fS);
        _p.Text(ctx, c, ri.Sub, fS, _ccx - s.X / 2, _ringR.Y + 14 * K + t.Y + 3 * K, c.Fg, 0.7f * a);
        if (BtnRect(BRingA, out var ra, out _))
        {
            bool h = _hover == BRingA;
            Pill(ctx, ra, c.Accent, (h ? 0.4f : 0.3f) * a);
            PillLine(ctx, ra, c.Accent, 0.5f * a);
            CenterText(ctx, c, ri.A, fB, ra, c.Fg, a);
        }
        if (!ri.Quiet && BtnRect(BRingB, out var rb, out _))
        {
            bool h = _hover == BRingB;
            Pill(ctx, rb, c.Fg, (h ? 0.16f : 0.09f) * a);
            CenterText(ctx, c, ri.B, fB, rb, c.Fg, 0.85f * a);
        }
    }

    private void DrawDial(ID2D1DeviceContext ctx, UiContext c, Matrix3x2 baseT)
    {
        float K = c.K;
        float a = _dialA;
        float sc = 0.96f + 0.04f * Ease(_dialA);
        var dialT = Matrix3x2.CreateScale(sc, new Vector2(_ccx, _ccy)) * baseT;
        ctx.Transform = dialT;

        // Timer | Wecker
        Pill(ctx, _pillRect, c.Fg, 0.08f * a);
        PillLine(ctx, _pillRect, c.Fg, 0.06f * a);
        var fTab = _p.Fmt(c, 13 * K, 600);
        for (int id = BTabTimer; id <= BTabAlarm; id++)
        {
            if (!BtnRect(id, out var r, out _)) continue;
            bool on = (id == BTabTimer) == (_mode == "timer");
            if (on)
            {
                Pill(ctx, r, c.Fg, 0.16f * a);
                ctx.DrawLine(new Vector2(r.X + r.Height / 2, r.Y + 0.5f), new Vector2(r.Right - r.Height / 2, r.Y + 0.5f), _p.Brush(new Vector3(1, 1, 1), 0.14f * a), 1);
            }
            CenterText(ctx, c, id == BTabTimer ? "Timer" : "Wecker", fTab, r, c.Fg, (on || _hover == id ? 1f : 0.6f) * a);
        }

        // Räder
        var fWheel = WheelFormat(c, _F);
        var fAmPm = _p.Fmt(c, 0.3f * _F, 600, true);
        for (int i = 0; i < _wheelCount; i++)
        {
            var w = _wheels[i];
            bool amp = _ampm && i == 2;
            if (i > 0 && !amp)
            {
                // Doppelpunkt zwischen den Rädern
                var cs = _p.Size(c, ":", fWheel);
                float cx = w.Bounds.X - (cs.X + 0.12f * _F) / 2;
                _p.Text(ctx, c, ":", fWheel, cx - cs.X / 2, w.Bounds.Y + (w.Bounds.Height - cs.Y) / 2 - 0.07f * _F, c.Fg, 0.4f * a);
            }
            DrawWheel(ctx, c, w, amp ? fAmPm : fWheel, a, dialT);
        }
        ctx.Transform = dialT;

        // Vorschläge oder Tage
        var fChip = _p.Fmt(c, 12.5f * K, 600);
        if (_mode == "timer")
        {
            for (int i = 0; i < 4; i++)
            {
                if (!BtnRect(BPreset + i, out var r, out _)) continue;
                bool h = _hover == BPreset + i;
                Pill(ctx, r, c.Fg, (h ? 0.14f : 0.08f) * a);
                CenterText(ctx, c, PresetText[i], fChip, r, c.Fg, (h ? 1f : 0.78f) * a);
            }
        }
        else
        {
            var fDay = _p.Fmt(c, 12.5f * K, 650);
            for (int d = 0; d < 7; d++)
            {
                if (!BtnRect(BDay + d, out var r, out _)) continue;
                bool on = (_days & (1 << d)) != 0, h = _hover == BDay + d;
                if (on)
                {
                    Pill(ctx, r, c.Accent, 0.3f * a);
                    PillLine(ctx, r, c.Accent, 0.5f * a);
                }
                else Pill(ctx, r, c.Fg, (h ? 0.12f : 0.07f) * a);
                CenterText(ctx, c, DayLetter[d], fDay, r, c.Fg, (on || h ? 1f : 0.6f) * a);
            }
        }

        // Hinweis und Knopf
        var fNote = _p.Fmt(c, 12.5f * K, 500);
        _p.Text(ctx, c, _note, fNote, _noteRect.X, _noteRect.Y, c.Fg, 0.55f * a);
        var fGo = _p.Fmt(c, 14 * K, 650);
        float ga = (_goDisabled ? 0.35f : 1f) * a;
        bool gh = _hover == BGo && !_goDisabled;
        Pill(ctx, _goRect, c.Accent, (gh ? 0.38f : 0.28f) * ga);
        PillLine(ctx, _goRect, c.Accent, 0.45f * ga);
        CenterText(ctx, c, _goText, fGo, _goRect, c.Fg, ga);
    }

    private static readonly string[] DayLetter = ["S", "M", "D", "M", "D", "F", "S"];

    private void DrawWheel(ID2D1DeviceContext ctx, UiContext c, Wheel w, IDWriteTextFormat f, float alpha, Matrix3x2 baseT)
    {
        var b = w.Bounds;
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        double p = w.X;
        int baseI = (int)Math.Round(p);
        for (int j = 0; j < 5; j++)
        {
            int i = baseI + j - 2;
            double off = i - p, ab = Math.Abs(off);
            string? v = w.Wrap ? w.Values[Mod(i, w.N)] : i >= 0 && i < w.N ? w.Values[i] : null;
            if (v == null) continue;
            float yEm = (float)(Math.Sign(off) * (0.9 * Math.Min(ab, 1) + 0.62 * Math.Max(ab - 1, 0)));
            float s = (float)(1 - 0.4 * Math.Min(ab, 1) - 0.14 * Math.Max(ab - 1, 0));
            float o = (float)(ab <= 1 ? 1 - 0.66 * ab : Math.Max(0, 0.34 * (2 - ab)));
            float ry = cy + yEm * w.Size;
            // Weiche Maske oben und unten (12 %)
            float fy = (ry - b.Y) / b.Height;
            float m = Math.Clamp(Math.Min(fy, 1 - fy) / 0.12f, 0, 1);
            // Unschärfe der Nachbarn: durch etwas weniger Deckung angedeutet
            float blur = ab < 0.05 ? 1 : 1 - 0.18f * (float)Math.Min(ab, 1.6) / 1.6f;
            float a = alpha * o * m * blur;
            if (a <= 0.003f || s <= 0) continue;
            var sz = _p.Size(c, v, f);
            ctx.Transform = Matrix3x2.CreateScale(s, new Vector2(cx, ry)) * baseT;
            _p.Text(ctx, c, v, f, cx - sz.X / 2, ry - sz.Y / 2, c.Fg, a);
        }
        ctx.Transform = baseT;
    }

    public void Dispose()
    {
        try { _sound?.Dispose(); } catch { }
        _sound = null;
        _arc?.Dispose(); _arc = null;
        _p.Dispose();
    }
}

/// <summary>
/// Kleine Zeichenhilfe für Uhr-Ebenen: ein Pinsel und eine runde Strichart je Gerätekontext, Textformate je
/// Größe/Stärke (bei neuer Einheit oder Schrift verworfen) und zwischengespeicherte Textlayouts, die nach einer
/// Weile ohne Gebrauch freigegeben werden. So entsteht pro Bild nichts Neues.
/// </summary>
internal sealed class ClockUiPaint : IDisposable
{
    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private readonly Dictionary<(int, int, int, int), ID2D1SolidColorBrush> _brushes = new();
    private ID2D1DeviceContext? _ctxForBrushes;
    private ID2D1StrokeStyle? _round;
    private ID2D1Factory1? _strokeFactory;
    private readonly Dictionary<(int Size, int Weight, bool Display), IDWriteTextFormat> _formats = new();
    private readonly Dictionary<(IDWriteTextFormat, string), Entry> _texts = new();
    private readonly List<(IDWriteTextFormat, string)> _drop = new();
    private float _k = -1;
    private string _font = "";
    private int _frame;
    private IDWriteFactory? _dw;

    private sealed class Entry { public IDWriteTextLayout Layout = null!; public Vector2 Size; public int Used; }

    public ID2D1StrokeStyle Round => _round!;

    /// <summary>Einmal pro Bild (im Update): Formate bei neuer Einheit/Schrift verwerfen, alte Texte freigeben.</summary>
    public void Begin(UiContext c)
    {
        _frame++;
        string font = c.Settings.Str("font");
        if (c.K != _k || font != _font || !ReferenceEquals(c.DWrite, _dw))
        {
            ClearTexts();
            // Die Formate gehören UiFonts (gemeinsam genutzt): nur vergessen, nie freigeben
            _formats.Clear();
            _k = c.K; _font = font; _dw = c.DWrite;
        }
        if ((_frame & 63) == 0 && _texts.Count > 0)
        {
            foreach (var kv in _texts) if (_frame - kv.Value.Used > 600) _drop.Add(kv.Key);
            foreach (var k in _drop) { _texts[k].Layout.Dispose(); _texts.Remove(k); }
            _drop.Clear();
        }
    }

    /// <summary>Geräteressourcen für diesen Kontext sicherstellen.</summary>
    public void Device(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr != ctx.NativePointer || _brush == null)
        {
            _brush?.Dispose();
            _ctxPtr = ctx.NativePointer;
            _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            foreach (var x in _brushes.Values) x.Dispose();
            _brushes.Clear();
            _ctxForBrushes = ctx;
        }
        if (_round == null || !ReferenceEquals(_strokeFactory, c.D2D))
        {
            _round?.Dispose();
            _strokeFactory = c.D2D;
            _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
            });
        }
    }

    public ID2D1SolidColorBrush Brush(Vector3 rgb, float a)
    {
        // Ein Pinsel je Farbe: Direct2D zeichnet Text gebündelt erst später – ein umgefärbter gemeinsamer
        // Pinsel ließe Beschriftungen mit der Farbe des nächsten Knopfs (fast unsichtbar) erscheinen.
        var key = ((int)(rgb.X * 255), (int)(rgb.Y * 255), (int)(rgb.Z * 255), (int)(Math.Clamp(a, 0, 1) * 255));
        if (_brushes.TryGetValue(key, out var b)) return b;
        if (_brushes.Count > 256) { foreach (var x in _brushes.Values) x.Dispose(); _brushes.Clear(); }
        b = _ctxForBrushes!.CreateSolidColorBrush(new Color4(key.Item1 / 255f, key.Item2 / 255f, key.Item3 / 255f, key.Item4 / 255f));
        _brushes[key] = b;
        return b;
    }

    public IDWriteTextFormat Fmt(UiContext c, float px, int weight, bool display = false)
    {
        var key = ((int)MathF.Round(px * 8), weight, display);
        if (_formats.TryGetValue(key, out var f)) return f;
        f = c.Fonts.Format(c, Math.Max(1, px), weight, display);
        _formats[key] = f;
        return f;
    }

    public IDWriteTextLayout Layout(UiContext c, string s, IDWriteTextFormat f, out Vector2 size)
    {
        if (_texts.TryGetValue((f, s), out var e))
        {
            e.Used = _frame;
            size = e.Size;
            return e.Layout;
        }
        var l = c.DWrite.CreateTextLayout(s, f, 8192, 8192);
        // Ausrichtung am Layout festlegen: die Formate von UiFonts sind gemeinsam, andere Teile stellen dort
        // Trailing/Center ein – in einem 8192er-Kasten läge der Text sonst weit außerhalb des Bildes
        l.TextAlignment = TextAlignment.Leading;
        l.ParagraphAlignment = ParagraphAlignment.Near;
        l.WordWrapping = WordWrapping.NoWrap;
        var m = l.Metrics;
        e = new Entry { Layout = l, Size = new Vector2(m.WidthIncludingTrailingWhitespace, m.Height), Used = _frame };
        _texts[(f, s)] = e;
        size = e.Size;
        return l;
    }

    public Vector2 Size(UiContext c, string s, IDWriteTextFormat f) { Layout(c, s, f, out var sz); return sz; }

    public void Text(ID2D1DeviceContext ctx, UiContext c, string s, IDWriteTextFormat f, float x, float y, Vector3 rgb, float a)
    {
        if (a <= 0.002f || s.Length == 0) return;
        var l = Layout(c, s, f, out _);
        ctx.DrawTextLayout(new Vector2(x, y), l, Brush(rgb, a), DrawTextOptions.None);
    }

    private void ClearTexts()
    {
        foreach (var e in _texts.Values) e.Layout.Dispose();
        _texts.Clear();
    }

    public void Dispose()
    {
        ClearTexts();
        _formats.Clear();
        _brush?.Dispose(); _brush = null;
        foreach (var x in _brushes.Values) x.Dispose();
        _brushes.Clear();
        _round?.Dispose(); _round = null;
    }
}
