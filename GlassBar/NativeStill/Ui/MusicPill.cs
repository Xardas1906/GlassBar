using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace GlassBar.NativeStill.Ui;

/// <summary>
/// Die Musik-Insel (app.js „isl“): als Pille ein kleines rundes Cover mit Balken und Fortschrittsring am Rand,
/// geöffnet eine Karte mit großem Cover, Titel/Interpret/Album, Fortschritt mit Zeiten und den drei Knöpfen.
/// Das Glas dahinter zeichnet die Oberfläche; hier entsteht nur der Inhalt, abhängig von <see cref="Pill.Open"/>.
/// </summary>
public sealed class MusicPill : Pill
{
    // Maße in Entwurfseinheiten (1 = 1 px bei 1080p, mal c.K) – wie C und X im Original.
    private const float CH = 40, CArt = 28, CInset = 6, BarsW = 21, BarsH = 16, CW = 84;
    private const float XW = 400, XPad = 18, XArt = 60, XArtR = 12, XR = 30, XGap = 14, XProgY = 92, XProgH = 14, XCtlH = 40;
    private const float InfoW = 228, RowW = 364, TimeW = 34, TimeGap = 10, FadeW = 18;
    private const float TitleSize = 16, ArtistSize = 13.5f, AlbumSize = 12, TimeSize = 11.5f;
    private const float TitleLine = 16 * 1.28f, ArtistLine = 13.5f * 1.35f, AlbumLine = 12 * 1.4f;
    // Knöpfe: Mitten in der Steuerzeile (Breite 364, Abstand 30, 38/44/38 groß, mittig)
    private static readonly float[] BtnX = { 129 - XPad, 200 - XPad, 271 - XPad };
    private static readonly float[] BtnR = { 19, 22, 19 };
    private const double AnnounceSeconds = 4.2, HideGrace = 2.5;
    private const float MarqueeSpeed = 30, MarqueeHold = 3, MarqueeGap = 40;
    // Fortschrittsbalken als Ziel: Zugabe um die Linie (reicht weder ans Cover noch an die Knöpfe)
    private const float SeekHitX = 6, SeekHitY = 10;
    private const double SeekPendingSeconds = 0.6;

    private enum Bars { Live, Pause, None }

    public override string Id => "music";

    public MusicPill() { }

    // ---------- Zustand ----------

    private double _lastActive = double.NegativeInfinity;
    private bool _wasShown;
    private string? _shownKey;                 // Lied, das gerade angezeigt wird (null = neu ankündigen)
    private double _announceLeft;
    private bool _announceSet;

    // Angezeigter Text (wechselt beim Liedwechsel erst nach dem Ausblenden)
    private string _title = "", _artist = "", _album = "";

    // Lautstärke nach dem Mausrad kurz anzeigen (statt der Album-Zeile); gesetzt aus dem UI-Thread
    private volatile float _volume;
    private DateTime _volumeUntil;
    private int _swapPhase;                    // 0 = ruhig, 1 = ausblenden, 2 = einblenden
    private float _swapT, _swapFrom = 1, _swapAlpha = 1, _swapDy;

    // Öffnen: Inhalte blenden eigenständig (wie oi/op/oc im Original)
    private float _prevOpen;
    private bool _opening;
    private float _oi, _op, _oc;

    // Balken
    private readonly float[] _barLv = new float[4];
    private float _outerA = 1, _barsA = 1;

    // Wiedergabe
    private bool _paused;
    private bool? _optPaused;                  // sofort umgeschaltet nach Klick, bis der Player bestätigt
    private double _optUntil;
    private float _pp;                         // 0 = Pause-Symbol (spielt), 1 = Play-Symbol (pausiert)
    private float _pausedA, _glowA = 0.42f;
    private double _position, _duration;

    // Springen im Lied: Ziehen zeigt nur die Vorschau, gesprungen wird beim Loslassen
    private bool _seekHover, _seekDrag;
    private double _seekFrac, _seekHoverFrac;
    private string _seekSong = "";
    private double? _seekPending;              // Ziel, bis die Wiedergabe-Seite den Sprung übernommen hat
    private double _seekPendingUntil;
    private int _seekPendingGen;
    private float _seekHot;

    // Cover
    private float _coverFade = 1;
    private float _nudgeT = 1, _nudgeDir;

    // Knöpfe
    private int _hoverBtn = -1, _pressBtn = -1;
    private readonly float[] _btnHover = new float[3];
    private readonly float[] _btnPress = new float[3];

    // Laufschrift für lange Zeilen
    private readonly float[] _mqT = new float[3];
    private bool _busy;

    // ---------- DirectWrite (geräteunabhängig) ----------

    private float _fmtK = -1;
    private string _fmtFont = "";
    private UiFonts? _fmtFonts;
    private IDWriteTextFormat? _fTitle, _fArtist, _fAlbum, _fTime, _fIcon, _fIconMain;
    private readonly IDWriteTextLayout?[] _lines = new IDWriteTextLayout?[3];
    private readonly string?[] _lineText = new string?[3];
    private readonly float[] _lineW = new float[3], _lineH = new float[3];
    private IDWriteTextLayout? _lElapsed, _lRemain;
    private string _sElapsed = "", _sRemain = "";
    private float _wElapsed, _wRemain, _hTime;
    private IDWriteTextLayout? _icPrev, _icNext, _icPlay, _icPause;

    // ---------- Direct2D (je Gerätekontext) ----------

    private nint _ctxPtr;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private ID2D1LinearGradientBrush? _maskRight, _maskBoth;
    private ID2D1RadialGradientBrush? _glow, _artEmpty;
    private Vector3 _glowColor = new(-1), _artColor = new(-1);
    private ID2D1Bitmap1? _cover;
    private ID2D1BitmapBrush1? _coverBrush;
    private int _coverVersion = int.MinValue;
    private byte[]? _coverBytes;
    private Vector3 _coverAvg;
    private bool _hasCoverAvg;
    private ID2D1PathGeometry? _ring;
    private Vector4 _ringKey = new(-1);
    private float _ringLen = -1;

    // =================================================================================================
    // Pill
    // =================================================================================================

    public override bool Shown(UiContext c)
    {
        var m = c.Media;
        bool active = c.Settings.Bool("showPill") && m.HasMedia && (m.Playing || m.Paused);
        if (active) { _lastActive = c.Time; return true; }
        // Manche Player melden zwischen zwei Liedern kurz „gestoppt“ – nicht gleich zusammenzucken.
        return c.Settings.Bool("showPill") && _wasShown && c.Time - _lastActive < HideGrace;
    }

    private static Bars BarsMode(UiContext c, bool paused) =>
        paused ? Bars.Pause : c.Settings.Bool("visualizer") ? Bars.Live : Bars.None;

    private static bool Controls(UiContext c) => c.Settings.Bool("showControls");

    public override Vector2 CompactSize(UiContext c)
    {
        float w = BarsMode(c, IsPaused(c)) == Bars.None ? CH : CW;
        return new Vector2(w, CH) * c.K;
    }

    public override Vector2 CardSize(UiContext c) => new Vector2(XW, CardLayout(c, out _)) * c.K;

    /// <summary>Höhe der Karte und Lage der Knopfzeile (cardLayout im Original, ohne Liedtext in der Karte).</summary>
    private float CardLayout(UiContext c, out float ctlY)
    {
        bool prog = Duration(c) > 0, ctl = Controls(c);
        float y = prog ? XProgY + XProgH : XPad + XArt;
        ctlY = 0;
        if (ctl) { ctlY = y + (prog ? 8 : 10); y = ctlY + XCtlH; }
        return y + (ctl ? 12 : XPad);
    }

    private double Duration(UiContext c) => c.Media.HasMedia ? c.Media.Duration : _duration;

    private bool IsPaused(UiContext c)
    {
        if (_optPaused is bool o) return o;
        var m = c.Media;
        return m.HasMedia ? !m.Playing : _paused;
    }

    public override bool Animating => _busy;

    public override void Update(UiContext c, double dt)
    {
        float t = (float)Math.Clamp(dt, 0, 0.1);
        var m = c.Media;
        var s = c.Settings;
        bool shown = Shown(c);

        // Kurz nach einem Klick gilt der vorweggenommene Zustand, bis der Player ihn bestätigt.
        if (_optPaused is bool op && (c.Time > _optUntil || (m.HasMedia && m.Playing == !op))) _optPaused = null;
        if (m.HasMedia) { _paused = !m.Playing; _position = m.CurrentPosition; _duration = m.Duration; }
        UpdateSeek(c, t);
        bool paused = IsPaused(c);

        // ---- Liedwechsel und Ankündigung ----
        if (!shown)
        {
            _wasShown = false;
            _shownKey = null;
            _announceLeft = 0;
        }
        else if (m.HasMedia)
        {
            string album = m.Album.Length > 0 && m.Album != m.Title ? m.Album : "";
            string key = m.Artist + "\u0001" + m.Title + "\u0001" + album;
            if (!_wasShown || _shownKey == null)
            {
                // Erscheint gerade: Text sofort, ohne Überblendung
                SetText(m.Title, m.Artist, album);
                _swapPhase = 0; _swapAlpha = 1; _swapDy = 0;
                _coverFade = 1;
                _shownKey = key;
                Announce(s);
            }
            else if (key != _shownKey)
            {
                _shownKey = key;
                if (_swapPhase != 1) { _swapPhase = 1; _swapT = 0; _swapFrom = _swapAlpha; }
                Announce(s);
            }
            _wasShown = true;
        }

        if (s.Str("pillStyle") == "expanded" && shown || _seekDrag)
        {
            // Immer offen (oder gerade gezogen): die Ankündigung einfach am Leben halten
            AnnounceFor = 0.5;
            _announceSet = true;
        }
        else if (_announceLeft > 0)
        {
            _announceLeft -= t;
            AnnounceFor = Math.Max(0, _announceLeft);
            _announceSet = true;
        }
        else if (_announceSet)
        {
            AnnounceFor = 0;
            _announceSet = false;
        }

        // ---- Textwechsel: erst weich hinaus (nach oben), dann neu herein (von unten) ----
        if (_swapPhase == 1)
        {
            _swapT += t;
            float p = Math.Clamp(_swapT / 0.2f, 0, 1);
            float e = p * p * p;                          // wie cubic-bezier(.4, 0, 1, 1)
            _swapAlpha = _swapFrom * (1 - e);
            _swapDy = -4 * e;
            if (p >= 1)
            {
                if (m.HasMedia) SetText(m.Title, m.Artist, m.Album.Length > 0 && m.Album != m.Title ? m.Album : "");
                _swapPhase = 2; _swapT = 0;
                Array.Clear(_mqT);
            }
        }
        else if (_swapPhase == 2)
        {
            _swapT += t;
            float p = Math.Clamp((_swapT - 0.06f) / 0.48f, 0, 1);
            float e = 1 - (1 - p) * (1 - p) * (1 - p);    // wie cubic-bezier(.2, .8, .2, 1)
            _swapAlpha = e;
            _swapDy = 4 * (1 - e);
            if (p >= 1) { _swapPhase = 0; _swapAlpha = 1; _swapDy = 0; }
        }

        // ---- Öffnen: Inhalte kommen beim Öffnen früh, gehen beim Schließen zuerst ----
        float open = Open;
        if (open > _prevOpen + 1e-4f) _opening = true;
        else if (open < _prevOpen - 1e-4f) _opening = false;
        _prevOpen = open;
        float k22 = 1 - MathF.Exp(-t * 22);
        bool hasDur = Duration(c) > 0;
        _oi += ((_opening ? Smooth(0.25f, 0.8f, open) : Smooth(0.62f, 0.95f, open)) - _oi) * k22;
        _op += ((hasDur ? (_opening ? Smooth(0.4f, 0.88f, open) : Smooth(0.7f, 0.97f, open)) : 0) - _op) * k22;
        _oc += ((Controls(c) ? (_opening ? Smooth(0.5f, 0.95f, open) : Smooth(0.75f, 1f, open)) : 0) - _oc) * k22;

        // ---- Balken: tief → hoch; pausiert steht das mittlere Paar still als Pausezeichen ----
        var mode = BarsMode(c, paused);
        for (int i = 0; i < 4; i++)
        {
            float target = mode == Bars.Pause ? (i == 1 || i == 2 ? 0.7f : 0) : Band(c, i);
            float rate = target > _barLv[i] ? 30 : 10;
            _barLv[i] += (target - _barLv[i]) * (1 - MathF.Exp(-t * rate));
        }
        float k35 = 1 - MathF.Exp(-t / 0.12f);           // ~ transition .35s
        _outerA += ((mode == Bars.Pause ? 0 : 1) - _outerA) * k35;
        _barsA += ((mode == Bars.None ? 0 : 1) - _barsA) * k35;

        // ---- Pause: Symbol dreht sich weich um, Cover wird matter, Glanz leiser ----
        _pp += ((paused ? 1 : 0) - _pp) * (1 - MathF.Exp(-t * 12));
        _pausedA += ((paused ? 1 : 0) - _pausedA) * (1 - MathF.Exp(-t / 0.25f));
        float glowTarget = paused ? 0.2f : c.LightTheme ? 0.3f : 0.42f;
        _glowA += (glowTarget - _glowA) * (1 - MathF.Exp(-t / 0.28f));

        if (_coverFade < 1) _coverFade = Math.Min(1, _coverFade + t / 0.6f);
        if (_nudgeT < 1) _nudgeT = Math.Min(1, _nudgeT + t / 0.46f);

        // ---- Knöpfe ----
        float k20 = 1 - MathF.Exp(-t * 20);
        for (int i = 0; i < 3; i++)
        {
            bool on = _hoverBtn == i && _oc > 0.6f;
            _btnHover[i] += ((on ? 1 : 0) - _btnHover[i]) * (1 - MathF.Exp(-t * 14));
            _btnPress[i] += ((_pressBtn == i && on ? 1 : 0) - _btnPress[i]) * k20;
        }

        // ---- Text und Zeiten ----
        EnsureText(c);
        if (hasDur)
        {
            double pos = Math.Clamp(_position, 0, _duration);
            SetTimes(c, FmtTime(pos), "-" + FmtTime(_duration - pos));
        }

        bool marquee = false;
        for (int i = 0; i < 3; i++)
        {
            if (_lines[i] != null && _lineW[i] > InfoW * c.K + 0.5f && _oi > 0.9f && _swapPhase == 0)
            {
                _mqT[i] += t;
                marquee = true;
            }
            else _mqT[i] = 0;
        }

        _busy = (m.Playing && shown) || _swapPhase != 0 || marquee || _coverFade < 1 || _nudgeT < 1
            || Math.Abs(_pp - (paused ? 1 : 0)) > 0.002f || Math.Abs(_pausedA - (paused ? 1 : 0)) > 0.002f
            || Math.Abs(_glowA - glowTarget) > 0.002f || Math.Abs(_outerA - (mode == Bars.Pause ? 0 : 1)) > 0.002f
            || Math.Abs(_barsA - (mode == Bars.None ? 0 : 1)) > 0.002f || BarsMoving(mode, c)
            || Unsettled(_oi) || Unsettled(_op) || Unsettled(_oc) || ButtonsMoving()
            || _seekDrag || _seekPending != null || Unsettled(_seekHot);
    }

    private static string SongKey(MediaState m) => m.Artist + "\u0001" + m.Title;

    private void UpdateSeek(UiContext c, float t)
    {
        var m = c.Media;
        string song = SongKey(m);
        if (_seekDrag && (!SeekAllowed(c) || song != _seekSong)) _seekDrag = false;
        if (_seekPending != null && (!m.HasMedia || song != _seekSong || m.SeekGeneration != _seekPendingGen || c.Time >= _seekPendingUntil))
            _seekPending = null;

        if (_seekDrag) _position = MediaTimeline.SeekTarget(_seekFrac, m.Duration);
        else if (_seekPending is double pending) _position = pending;

        if (!SeekReady(c)) _seekHover = false;
        _seekHot += ((_seekHover || _seekDrag ? 1 : 0) - _seekHot) * (1 - MathF.Exp(-t * 14));
    }

    private static bool Unsettled(float v) => v > 0.002f && v < 0.998f;

    private bool ButtonsMoving()
    {
        for (int i = 0; i < 3; i++)
            if (Unsettled(_btnHover[i]) || Unsettled(_btnPress[i])) return true;
        return false;
    }

    private bool BarsMoving(Bars mode, UiContext c)
    {
        for (int i = 0; i < 4; i++)
        {
            float target = mode == Bars.Pause ? (i == 1 || i == 2 ? 0.7f : 0) : Band(c, i);
            if (Math.Abs(target - _barLv[i]) > 0.003f) return true;
        }
        return false;
    }

    private static float Band(UiContext c, int i)
    {
        var b = c.Bands;
        if (b != null && b.Length > i) return Math.Clamp(b[i], 0, 1);
        // Ersatzweise grob aus dem Rohspektrum (64 Werte je Kanal: vier Bereiche)
        var sp = c.Spectrum;
        if (sp == null || sp.Length < 64) return 0;
        int from = i switch { 0 => 0, 1 => 4, 2 => 12, _ => 28 }, to = i switch { 0 => 4, 1 => 12, 2 => 28, _ => 64 };
        float sum = 0;
        for (int j = from; j < to; j++) sum += sp[j];
        return Math.Clamp(sum / (to - from) * 1.5f, 0, 1);
    }

    private void Announce(StillValues s)
    {
        if (s.Bool("announce") && s.Str("pillStyle") == "compact") _announceLeft = AnnounceSeconds;
    }

    private void SetText(string title, string artist, string album)
    {
        _title = title; _artist = artist; _album = album;
    }

    private static string FmtTime(double sec)
    {
        int s = (int)Math.Max(0, Math.Floor(sec));
        return $"{s / 60}:{s % 60:00}";
    }

    // =================================================================================================
    // Maus
    // =================================================================================================

    public override bool OnPointer(UiContext c, PointerEvent e)
    {
        switch (e.Kind)
        {
            case PointerKind.Leave:
                _hoverBtn = -1; _pressBtn = -1; _seekHover = false;
                return false;
            case PointerKind.Move:
                if (_seekDrag) { _seekFrac = SeekFraction(c, e.Pos.X); return true; }
                _hoverBtn = HitButton(c, e.Pos);
                UpdateSeekHover(c, e.Pos);
                return _hoverBtn >= 0 || _seekHover;
            case PointerKind.Down:
                // Noch am Ziehen? Dann ging das Loslassen verloren: abbrechen, nicht springen.
                if (_seekDrag) { _seekDrag = false; return true; }
                if (e.Button == 2)
                {
                    // Mittelklick: Musikprogramm stumm bzw. wieder laut (wie im Dock); kurz den Stand zeigen
                    c.Controls.ToggleMute((muted, vol) => { _volume = muted ? 0 : vol; _volumeUntil = DateTime.UtcNow.AddSeconds(1.6); });
                    return true;
                }
                _hoverBtn = HitButton(c, e.Pos);
                if (e.Button != 0) return false;
                if (_hoverBtn < 0)
                {
                    if (!HitSeek(c, e.Pos)) return false;
                    _seekDrag = true;
                    _seekHover = false;
                    _seekSong = SongKey(c.Media);
                    _seekFrac = SeekFraction(c, e.Pos.X);
                    _seekPending = null;
                    return true;
                }
                _pressBtn = _hoverBtn;
                return true;
            case PointerKind.Up:
            {
                if (_seekDrag)
                {
                    // Wo die Maus senkrecht steht, ist egal: Es zählt die Stelle auf dem Balken.
                    _seekDrag = false;
                    if (e.Button == 0 && SeekAllowed(c) && SongKey(c.Media) == _seekSong)
                        CommitSeek(c, SeekFraction(c, e.Pos.X));
                    UpdateSeekHover(c, e.Pos);
                    return true;
                }
                int hit = HitButton(c, e.Pos);
                int pressed = _pressBtn;
                _pressBtn = -1;
                if (e.Button != 0 || pressed < 0) return false;
                if (hit == pressed) Press(c, hit);
                return true;
            }
            case PointerKind.Wheel when e.Wheel != 0:
                // Mausrad: Lautstärke des Programms, das gerade spielt (Spotify, Browser …)
                c.Controls.Volume(Math.Sign(e.Wheel), v => { _volume = v; _volumeUntil = DateTime.UtcNow.AddSeconds(1.6); });
                return true;
            default:
                return false;
        }
    }

    private void Press(UiContext c, int btn)
    {
        if (btn == 1)
        {
            // Sofort umschalten; der Player bestätigt einen Moment später.
            _optPaused = !IsPaused(c);
            _optUntil = c.Time + 1.5;
            c.Controls.PlayPause();
        }
        else
        {
            // Das Cover rückt ein Stück zum Sprung hin, als zöge das nächste schon daran.
            _nudgeDir = btn == 2 ? -1 : 1;
            _nudgeT = 0;
            if (btn == 2) c.Controls.Next(); else c.Controls.Previous();
        }
        _busy = true;
    }

    private void CommitSeek(UiContext c, double fraction)
    {
        double target = MediaTimeline.SeekTarget(fraction, c.Media.Duration);
        _seekPending = target;
        _seekPendingUntil = c.Time + SeekPendingSeconds;
        _seekPendingGen = c.Media.SeekGeneration;
        _position = target;
        c.Controls.Seek(target);
        _busy = true;
    }

    private static bool SeekAllowed(UiContext c)
    {
        var m = c.Media;
        return m.HasMedia && m.CanSeek && m.Duration >= 1 && !c.Editing;
    }

    /// <summary>Darf der Balken gerade bedient werden (Karte offen, Balken steht)?</summary>
    private bool SeekReady(UiContext c) => SeekAllowed(c) && Open > 0.9f && _op > 0.6f;

    /// <summary>Lage des Balkens – dieselbe fürs Zeichnen und für die Maus.</summary>
    private void SeekTrack(UiContext c, out float x0, out float x1, out float cy)
    {
        float k = c.K, px = (float)Bounds.X + XPad * k;
        x0 = px + (TimeW + TimeGap) * k;
        x1 = px + (RowW - TimeW - TimeGap) * k;
        cy = (float)Bounds.Y + (XProgY + (1 - _op) * 6 + XProgH / 2) * k;
    }

    private bool HitSeek(UiContext c, Vector2 p)
    {
        if (!SeekReady(c)) return false;
        SeekTrack(c, out float x0, out float x1, out float cy);
        float k = c.K;
        return p.X >= x0 - SeekHitX * k && p.X <= x1 + SeekHitX * k && Math.Abs(p.Y - cy) <= SeekHitY * k;
    }

    private double SeekFraction(UiContext c, float x)
    {
        SeekTrack(c, out float x0, out float x1, out _);
        return x1 > x0 ? Math.Clamp((x - x0) / (x1 - x0), 0, 1) : 0;
    }

    private void UpdateSeekHover(UiContext c, Vector2 p)
    {
        _seekHover = _pressBtn < 0 && _hoverBtn < 0 && HitSeek(c, p);
        if (_seekHover) _seekHoverFrac = SeekFraction(c, p.X);
    }

    private int HitButton(UiContext c, Vector2 p)
    {
        if (_oc <= 0.6f || !Controls(c)) return -1;
        float k = c.K;
        CardLayout(c, out float ctlY);
        float sc = 0.94f + 0.06f * _oc;
        float left = (float)Bounds.X, top = (float)Bounds.Y;
        var pivot = new Vector2(left + (XPad + RowW / 2) * k, top + (ctlY + (1 - _oc) * 8) * k);
        for (int i = 0; i < 3; i++)
        {
            var ctr = new Vector2(left + (XPad + BtnX[i]) * k, pivot.Y + XCtlH / 2 * k);
            ctr = pivot + (ctr - pivot) * sc;
            float r = BtnR[i] * k * sc;
            if (Vector2.DistanceSquared(p, ctr) <= r * r) return i;
        }
        return -1;
    }

    // =================================================================================================
    // Zeichnen
    // =================================================================================================

    public override void Draw(ID2D1DeviceContext ctx, UiContext c)
    {
        EnsureDevice(ctx, c);
        EnsureText(c);
        EnsureCover(ctx, c);

        float k = c.K;
        float L = (float)Bounds.X, T = (float)Bounds.Y, W = (float)Bounds.Width, H = (float)Bounds.Height;
        if (W <= 1 || H <= 1) return;
        float e = Open, ec = Math.Clamp(e, 0, 1);
        float R = Math.Min(H / 2, Lerp(CH / 2, XR, ec) * k);
        var fg = c.Fg;
        var brush = _brush!;
        var prev = ctx.Transform;

        ctx.PushAxisAlignedClip(new RawRectF(L, T, L + W, T + H), AntialiasMode.PerPrimitive);

        // ---- Albumfarbe, die hinter dem Cover ins Glas blutet (in der Form der Insel) ----
        float aSize = Lerp(CArt, XArt, e) * k;
        float ap = Lerp(CInset, XPad, ec) * k;
        float ar = Math.Min(aSize / 2, Lerp(CArt / 2, XArtR, ec) * k);
        var artCenter = new Vector2(L + ap + aSize / 2, T + ap + aSize / 2);
        var glowCol = _cover != null && _hasCoverAvg ? _coverAvg : c.Accent;
        EnsureGlow(ctx, glowCol);
        if (_glow != null && _glowA > 0.005f)
        {
            _glow.Center = artCenter;
            _glow.RadiusX = _glow.RadiusY = aSize * 1.2f + 26 * k;
            _glow.Opacity = _glowA;
            ctx.FillRoundedRectangle(RRect(L, T, W, H, R), _glow);
        }

        // ---- Cover: das kleine runde wächst zum großen abgerundeten Quadrat ----
        DrawArt(ctx, c, L + ap, T + ap, aSize, ar);

        // ---- Fortschrittsring am Rand der Pille (übergibt beim Öffnen an den Balken) ----
        double dur = _duration;
        float pct = dur > 0 ? (float)Math.Clamp(_position / dur, 0, 1) : 0;
        // Kurz nach dem Mausrad zeigt der Ring der zugeklappten Pille die Lautstärke (in Schriftfarbe statt Akzent)
        bool volRing = DateTime.UtcNow < _volumeUntil;
        float ringPct = volRing ? Math.Clamp(_volume, 0, 1) : pct;
        float ringA = dur > 0 || volRing ? 1 - Smooth(0, 0.3f, e) : 0;
        if (ringA > 0.01f && ringPct > 0)
        {
            float sw = Math.Max(1.25f, 1.5f * k), x = sw / 2;
            float rw = Math.Max(0, W - sw), rh = Math.Max(0, H - sw);
            float rr = Math.Max(0, Math.Min(Math.Min(R - x, rw / 2), rh / 2));
            BuildRing(c, L + x, T + x, rw, rh, rr, ringPct);
            if (_ring != null)
            {
                var a = volRing ? fg : c.Accent;
                brush.Color = new Color4(a.X, a.Y, a.Z, 0.22f * ringA);
                ctx.DrawGeometry(_ring, brush, sw * 3, _round);
                brush.Color = new Color4(a.X, a.Y, a.Z, 0.9f * ringA);
                ctx.DrawGeometry(_ring, brush, sw, _round);
            }
        }

        // ---- Balken: offen rücken sie nach links neben die Ecke ----
        if (_barsA > 0.01f)
        {
            float bx = L + W - Lerp(14, XPad + 32, ec) * k - BarsW * k;
            float by = T + Lerp((CH - BarsH) / 2, XPad + 4, ec) * k;
            float minH = 3 * k, maxH = BarsH * k, bw = 3 * k, mid = by + maxH / 2;
            var a = c.Accent;
            for (int i = 0; i < 4; i++)
            {
                float alpha = _barsA * (i == 0 || i == 3 ? _outerA : 1);
                if (alpha < 0.01f) continue;
                float h = minH + (maxH - minH) * _barLv[i];
                float x0 = bx + i * 6 * k;
                brush.Color = new Color4(a.X, a.Y, a.Z, alpha);
                ctx.FillRoundedRectangle(RRect(x0, mid - h / 2, bw, h, bw / 2), brush);
            }
        }

        // ---- Titel, Interpret, Album ----
        float infoA = _oi * _swapAlpha;
        if (infoA > 0.01f)
        {
            float ix = L + (XPad + XArt + XGap) * k;
            float iy = T + (XPad + 2 + (1 - _oi) * 6 + _swapDy) * k;
            DrawLine(ctx, c, 0, ix, iy, TitleLine * k, new Color4(fg.X, fg.Y, fg.Z, 0.97f * infoA));
            iy += TitleLine * k;
            if (_artist.Length > 0)
            {
                DrawLine(ctx, c, 1, ix, iy, ArtistLine * k, new Color4(fg.X, fg.Y, fg.Z, 0.64f * infoA));
                iy += ArtistLine * k;
            }
            if (DateTime.UtcNow < _volumeUntil)
            {
                // Lautstärke als Balken an Stelle des Albums
                float bw = (InfoW - 4) * k, bh = 4 * k, by = iy + (AlbumLine * k - bh) / 2;
                brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.16f * infoA);
                ctx.FillRoundedRectangle(RRect(ix, by, bw, bh, bh / 2), brush);
                brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.85f * infoA);
                ctx.FillRoundedRectangle(RRect(ix, by, Math.Max(bh, bw * _volume), bh, bh / 2), brush);
            }
            else if (_album.Length > 0)
                DrawLine(ctx, c, 2, ix, iy, AlbumLine * k, new Color4(fg.X, fg.Y, fg.Z, 0.4f * infoA));
        }

        // ---- Fortschritt: Zeit, Balken, Rest ----
        if (_op > 0.01f && dur > 0)
        {
            SeekTrack(c, out float tx0, out float tx1, out float cy);
            float px = L + XPad * k;
            brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.5f * _op);
            if (_lElapsed != null) ctx.DrawTextLayout(new Vector2(px, cy - _hTime / 2), _lElapsed, brush, DrawTextOptions.NoSnap);
            if (_lRemain != null) ctx.DrawTextLayout(new Vector2(px + RowW * k - _wRemain, cy - _hTime / 2), _lRemain, brush, DrawTextOptions.NoSnap);

            float th = (4 + 2 * _seekHot) * k;
            brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.14f * _op);
            ctx.FillRoundedRectangle(RRect(tx0, cy - th / 2, tx1 - tx0, th, th / 2), brush);
            if (_seekHover)
            {
                // Bis zur Maus leicht aufgehellt: dorthin ginge der Sprung
                brush.Color = new Color4(fg.X, fg.Y, fg.Z, 0.16f * _seekHot * _op);
                ctx.FillRoundedRectangle(RRect(tx0, cy - th / 2, Math.Max(th, (tx1 - tx0) * (float)_seekHoverFrac), th, th / 2), brush);
            }
            float fw = (tx1 - tx0) * pct;
            if (fw > 0.5f)
            {
                var a = c.Accent;
                float gl = 3 * k;
                brush.Color = new Color4(a.X, a.Y, a.Z, 0.18f * _op);
                ctx.FillRoundedRectangle(RRect(tx0 - gl / 2, cy - th / 2 - gl / 2, fw + gl, th + gl, (th + gl) / 2), brush);
                brush.Color = new Color4(a.X, a.Y, a.Z, _op);
                ctx.FillRoundedRectangle(RRect(tx0, cy - th / 2, Math.Max(th, fw), th, th / 2), brush);
            }
            if (_seekHot > 0.01f)
            {
                float knob = 6 * k * _seekHot;
                brush.Color = new Color4(fg.X, fg.Y, fg.Z, _op);
                ctx.FillEllipse(new Ellipse(new Vector2(tx0 + fw, cy), knob, knob), brush);
            }
        }

        // ---- Knöpfe ----
        if (_oc > 0.01f && Controls(c)) DrawControls(ctx, c, L, T, prev);

        ctx.PopAxisAlignedClip();
        ctx.Transform = prev;
    }

    private void DrawArt(ID2D1DeviceContext ctx, UiContext c, float x, float y, float size, float r)
    {
        var box = RRect(x, y, size, size, r);
        var brush = _brush!;

        // Ohne Cover (und darunter, solange es einblendet): ein Schimmer in der Akzentfarbe
        EnsureArtEmpty(ctx, c.Accent);
        if (_artEmpty != null)
        {
            _artEmpty.Center = new Vector2(x + size * 0.32f, y + size * 0.28f);
            _artEmpty.RadiusX = _artEmpty.RadiusY = size * 0.99f;
            ctx.FillRoundedRectangle(box, _artEmpty);
        }

        if (_cover != null && _coverBrush != null)
        {
            // object-fit: cover, dazu das kurze Verrutschen beim Springen
            float cw = _cover.PixelSize.Width, ch = _cover.PixelSize.Height;
            float sc = size / Math.Max(1, Math.Min(cw, ch));
            float nudge = 0, nudgeA = 1;
            if (_nudgeT < 1)
            {
                float p = 1 - (1 - _nudgeT) * (1 - _nudgeT);
                float tri = p < 0.5f ? p * 2 : (1 - p) * 2;
                nudge = _nudgeDir * 0.14f * size * tri;
                nudgeA = 1 - 0.65f * tri;
            }
            float ox = x + (size - cw * sc) / 2 + nudge, oy = y + (size - ch * sc) / 2;
            _coverBrush.Transform = Matrix3x2.CreateScale(sc) * Matrix3x2.CreateTranslation(ox, oy);
            _coverBrush.Opacity = _coverFade * nudgeA;
            ctx.FillRoundedRectangle(box, _coverBrush);

            // Pausiert: matter und etwas dunkler
            if (_pausedA > 0.01f)
            {
                brush.Color = new Color4(0, 0, 0, 0.18f * _pausedA * _coverFade);
                ctx.FillRoundedRectangle(box, brush);
            }
        }

        // Feiner Rand innen
        brush.Color = c.LightTheme ? new Color4(0, 0, 0, 0.08f) : new Color4(1, 1, 1, 0.14f);
        ctx.DrawRoundedRectangle(RRect(x + 0.5f, y + 0.5f, size - 1, size - 1, Math.Max(0, r - 0.5f)), brush, 1);
    }

    private void DrawLine(ID2D1DeviceContext ctx, UiContext c, int i, float x, float y, float lineH, Color4 col)
    {
        var layout = _lines[i];
        if (layout == null) return;
        var brush = _brush!;
        brush.Color = col;
        float k = c.K, boxW = InfoW * k;
        float ty = y + (lineH - _lineH[i]) / 2;
        float w = _lineW[i];

        if (w <= boxW + 0.5f)
        {
            ctx.DrawTextLayout(new Vector2(x, ty), layout, brush, DrawTextOptions.NoSnap);
            return;
        }

        // Zu lang: rechts weich ausblenden; offen läuft die Zeile langsam durch und kommt wieder herein.
        float off = 0;
        float scroll = (w + MarqueeGap * k) / (MarqueeSpeed * k);
        float tt = _mqT[i] % (MarqueeHold + scroll);
        if (tt > MarqueeHold) off = (tt - MarqueeHold) * MarqueeSpeed * k;

        var mask = off > 0 ? _maskBoth! : _maskRight!;
        mask.StartPoint = new Vector2(x, 0);
        mask.EndPoint = new Vector2(x + boxW, 0);
        var lp = new LayerParameters1
        {
            ContentBounds = new RawRectF(x, y, x + boxW, y + lineH),
            GeometricMask = null,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1,
            OpacityBrush = mask,
            LayerOptions = LayerOptions1.None,
        };
        ctx.PushLayer(lp, null!);
        ctx.DrawTextLayout(new Vector2(x - off, ty), layout, brush, DrawTextOptions.NoSnap);
        if (off > 0) ctx.DrawTextLayout(new Vector2(x - off + w + MarqueeGap * k, ty), layout, brush, DrawTextOptions.NoSnap);
        ctx.PopLayer();
    }

    private void DrawControls(ID2D1DeviceContext ctx, UiContext c, float L, float T, Matrix3x2 prev)
    {
        float k = c.K;
        CardLayout(c, out float ctlY);
        float sc = 0.94f + 0.06f * _oc;
        var pivot = new Vector2(L + (XPad + RowW / 2) * k, T + (ctlY + (1 - _oc) * 8) * k);
        var row = Matrix3x2.CreateScale(sc, pivot);
        var fg = c.Fg;
        var brush = _brush!;
        float cy = pivot.Y + XCtlH / 2 * k;

        for (int i = 0; i < 3; i++)
        {
            float hov = _btnHover[i];
            float press = 1 - 0.14f * _btnPress[i];
            var ctr = new Vector2(L + (XPad + BtnX[i]) * k, cy);
            ctx.Transform = Matrix3x2.CreateScale(press, ctr) * row * prev;
            float r = BtnR[i] * k;

            // Hintergrund: der mittlere Knopf hat immer einen, die äußeren nur beim Drüberfahren
            float bgA = i == 1 ? 0.1f + 0.06f * hov : 0.09f * hov;
            if (bgA > 0.005f)
            {
                brush.Color = new Color4(fg.X, fg.Y, fg.Z, bgA * _oc);
                ctx.FillEllipse(new Ellipse(ctr, r, r), brush);
            }
            if (i == 1)
            {
                // feines Licht oben (inset 0 1px 0 weiß .1)
                brush.Color = new Color4(1, 1, 1, 0.1f * _oc);
                ctx.DrawEllipse(new Ellipse(ctr + new Vector2(0, 0.5f), r - 0.5f, r - 0.5f), brush, 1);
            }

            float fa = (i == 1 ? 1 : 0.78f + 0.22f * hov) * _oc;
            brush.Color = new Color4(fg.X, fg.Y, fg.Z, fa);
            if (i == 1)
            {
                // Play und Pause tauschen mit einer kleinen Dreh-Skalierung, nie hart
                var baseM = Matrix3x2.CreateScale(press, ctr) * row * prev;
                DrawIcon(ctx, _icPause, ctr, Lerp(1, 0.5f, _pp), 30 * _pp, (1 - _pp) * fa, baseM);
                DrawIcon(ctx, _icPlay, ctr, Lerp(0.5f, 1, _pp), -30 * (1 - _pp), _pp * fa, baseM);
            }
            else
            {
                DrawIcon(ctx, i == 0 ? _icPrev : _icNext, ctr, 1, 0, fa, ctx.Transform);
            }
        }
        ctx.Transform = prev;
    }

    private void DrawIcon(ID2D1DeviceContext ctx, IDWriteTextLayout? icon, Vector2 ctr, float scale, float rotDeg, float alpha, Matrix3x2 baseM)
    {
        if (icon == null || alpha < 0.01f) return;
        var brush = _brush!;
        var col = brush.Color;
        brush.Color = new Color4(col.R, col.G, col.B, alpha);
        ctx.Transform = Matrix3x2.CreateRotation(rotDeg * MathF.PI / 180, ctr) * Matrix3x2.CreateScale(scale, ctr) * baseM;
        float bw = icon.MaxWidth, bh = icon.MaxHeight;
        ctx.DrawTextLayout(new Vector2(ctr.X - bw / 2, ctr.Y - bh / 2), icon, brush, DrawTextOptions.NoSnap);
        ctx.Transform = baseM;
        brush.Color = col;
    }

    // ---------- Fortschrittsring: Umfang ab oben Mitte im Uhrzeigersinn ----------

    private Vector2 _rp;
    private float _rrem;

    private void BuildRing(UiContext c, float ox, float oy, float w, float h, float r, float pct)
    {
        float sx = Math.Max(0, w - 2 * r), sy = Math.Max(0, h - 2 * r);
        float perim = 2 * sx + 2 * sy + 2 * MathF.PI * r;
        float len = perim * pct;
        var key = new Vector4(ox + oy * 0.001f, w, h, r);
        if (_ring != null && key == _ringKey && Math.Abs(len - _ringLen) < 0.3f) return;
        _ringKey = key;
        _ringLen = len;
        _ring?.Dispose();
        _ring = null;
        if (len <= 0.01f) return;

        var geo = c.D2D.CreatePathGeometry();
        var sink = geo.Open();
        _rp = new Vector2(ox + w / 2, oy);
        _rrem = len;
        sink.BeginFigure(_rp, FigureBegin.Hollow);
        _ = Seg(sink, new Vector2(ox + w - r, oy))
            && Arc(sink, new Vector2(ox + w - r, oy + r), r, -MathF.PI / 2)
            && Seg(sink, new Vector2(ox + w, oy + h - r))
            && Arc(sink, new Vector2(ox + w - r, oy + h - r), r, 0)
            && Seg(sink, new Vector2(ox + r, oy + h))
            && Arc(sink, new Vector2(ox + r, oy + h - r), r, MathF.PI / 2)
            && Seg(sink, new Vector2(ox, oy + r))
            && Arc(sink, new Vector2(ox + r, oy + r), r, MathF.PI)
            && Seg(sink, new Vector2(ox + w / 2, oy));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        sink.Dispose();
        _ring = geo;
    }

    private bool Seg(ID2D1GeometrySink sink, Vector2 to)
    {
        float d = Vector2.Distance(_rp, to);
        if (d <= 0.0001f) return _rrem > 0;
        if (_rrem >= d) { sink.AddLine(to); _rp = to; _rrem -= d; return _rrem > 0; }
        sink.AddLine(_rp + (to - _rp) * (_rrem / d));
        _rrem = 0;
        return false;
    }

    private bool Arc(ID2D1GeometrySink sink, Vector2 center, float r, float a0)
    {
        if (r <= 0.0001f) return _rrem > 0;
        float full = r * MathF.PI / 2;
        float f = Math.Min(1, _rrem / full);
        float a = a0 + MathF.PI / 2 * f;
        var to = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        sink.AddArc(new ArcSegment(to, new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
        _rp = to;
        _rrem -= full * f;
        return _rrem > 0.0001f;
    }

    // =================================================================================================
    // Ressourcen
    // =================================================================================================

    private void EnsureText(UiContext c)
    {
        string font = c.Settings.Str("font");
        if (_fmtK != c.K || _fmtFont != font || !ReferenceEquals(_fmtFonts, c.Fonts))
        {
            DisposeText();
            _fmtK = c.K; _fmtFont = font; _fmtFonts = c.Fonts;
            float k = c.K;
            // Textformate gehören UiFonts (dort zwischengespeichert) – hier nur geholt, nicht freigegeben.
            _fTitle = c.Fonts.Format(c, TitleSize * k, 600);
            _fArtist = c.Fonts.Format(c, ArtistSize * k, 500);
            _fAlbum = c.Fonts.Format(c, AlbumSize * k, 500);
            _fTime = c.Fonts.Format(c, TimeSize * k, 600);
            _fIcon = c.Fonts.Icons(c, 15 * k);
            _fIconMain = c.Fonts.Icons(c, 18 * k);
            _icPrev = IconLayout(c, "", _fIcon, 38 * k);
            _icNext = IconLayout(c, "", _fIcon, 38 * k);
            _icPause = IconLayout(c, "", _fIconMain, 44 * k);
            _icPlay = IconLayout(c, "", _fIconMain, 44 * k);
        }
        SetLine(c, 0, _title, _fTitle!);
        SetLine(c, 1, _artist, _fArtist!);
        SetLine(c, 2, _album, _fAlbum!);
    }

    private static IDWriteTextLayout IconLayout(UiContext c, string glyph, IDWriteTextFormat fmt, float box)
    {
        var l = c.DWrite.CreateTextLayout(glyph, fmt, box, box);
        l.TextAlignment = TextAlignment.Center;
        l.ParagraphAlignment = ParagraphAlignment.Center;
        l.WordWrapping = WordWrapping.NoWrap;
        return l;
    }

    private void SetLine(UiContext c, int i, string text, IDWriteTextFormat fmt)
    {
        if (_lines[i] != null && _lineText[i] == text) return;
        _lines[i]?.Dispose();
        _lines[i] = null;
        _lineText[i] = text;
        _mqT[i] = 0;
        if (text.Length == 0) return;
        var l = c.DWrite.CreateTextLayout(text, fmt, 100000, 1000);
        l.WordWrapping = WordWrapping.NoWrap;
        var m = l.Metrics;
        _lineW[i] = m.WidthIncludingTrailingWhitespace;
        _lineH[i] = m.Height;
        _lines[i] = l;
    }

    private void SetTimes(UiContext c, string elapsed, string remain)
    {
        if (_fTime == null) return;
        if (_lElapsed == null || elapsed != _sElapsed)
        {
            _lElapsed?.Dispose();
            _sElapsed = elapsed;
            _lElapsed = c.DWrite.CreateTextLayout(elapsed, _fTime, 1000, 1000);
            _lElapsed.WordWrapping = WordWrapping.NoWrap;
            var m = _lElapsed.Metrics;
            _wElapsed = m.WidthIncludingTrailingWhitespace;
            _hTime = m.Height;
        }
        if (_lRemain == null || remain != _sRemain)
        {
            _lRemain?.Dispose();
            _sRemain = remain;
            _lRemain = c.DWrite.CreateTextLayout(remain, _fTime, 1000, 1000);
            _lRemain.WordWrapping = WordWrapping.NoWrap;
            _wRemain = _lRemain.Metrics.WidthIncludingTrailingWhitespace;
        }
    }

    private void EnsureDevice(ID2D1DeviceContext ctx, UiContext c)
    {
        if (_ctxPtr == ctx.NativePointer && _brush != null) return;
        DisposeDevice();
        _ctxPtr = ctx.NativePointer;
        _brush = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _round = c.D2D.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
            MiterLimit = 10, DashStyle = DashStyle.Solid,
        });

        // Weiche Kanten für zu lange Zeilen (Breite FadeW von InfoW, unabhängig von K)
        float f = FadeW / InfoW;
        using (var right = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0, new Color4(1, 1, 1, 1)), new GradientStop(1 - f, new Color4(1, 1, 1, 1)), new GradientStop(1, new Color4(1, 1, 1, 0)),
        }))
            _maskRight = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX), right);
        using (var both = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0, new Color4(1, 1, 1, 0)), new GradientStop(f, new Color4(1, 1, 1, 1)),
            new GradientStop(1 - f, new Color4(1, 1, 1, 1)), new GradientStop(1, new Color4(1, 1, 1, 0)),
        }))
            _maskBoth = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.UnitX), both);

        _coverVersion = int.MinValue;
        _glowColor = _artColor = new Vector3(-1);
    }

    private void EnsureGlow(ID2D1DeviceContext ctx, Vector3 col)
    {
        if (_glow != null && Vector3.DistanceSquared(col, _glowColor) < 1e-5f) return;
        _glow?.Dispose();
        _glowColor = col;
        using var stops = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0, new Color4(col.X, col.Y, col.Z, 1f)),
            new GradientStop(0.45f, new Color4(col.X, col.Y, col.Z, 0.55f)),
            new GradientStop(1, new Color4(col.X, col.Y, col.Z, 0f)),
        });
        _glow = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
    }

    private void EnsureArtEmpty(ID2D1DeviceContext ctx, Vector3 col)
    {
        if (_artEmpty != null && Vector3.DistanceSquared(col, _artColor) < 1e-5f) return;
        _artEmpty?.Dispose();
        _artColor = col;
        using var stops = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop(0, new Color4(col.X, col.Y, col.Z, 1f)),
            new GradientStop(1, new Color4(col.X * 0.25f, col.Y * 0.25f, col.Z * 0.25f, 0.25f)),
        });
        _artEmpty = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
    }

    private void EnsureCover(ID2D1DeviceContext ctx, UiContext c)
    {
        var m = c.Media;
        // Während der kurzen Gnadenfrist ohne Medien bleibt das letzte Cover stehen.
        if (!m.HasMedia || m.CoverVersion == _coverVersion) return;
        bool first = _coverVersion == int.MinValue || _cover == null;
        _coverVersion = m.CoverVersion;
        _coverBrush?.Dispose(); _coverBrush = null;
        _cover?.Dispose(); _cover = null;
        _hasCoverAvg = false;

        var bytes = m.CoverBgra;
        int w = m.CoverWidth, h = m.CoverHeight;
        if (bytes == null || w <= 0 || h <= 0 || bytes.Length < w * h * 4) { _coverBytes = null; return; }

        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96, BitmapOptions.None);
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { _cover = ctx.CreateBitmap(new SizeI(w, h), pin.AddrOfPinnedObject(), (uint)(w * 4), props); }
        finally { pin.Free(); }
        _coverBrush = ctx.CreateBitmapBrush(_cover,
            new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.HighQualityCubic), null);

        if (!ReferenceEquals(bytes, _coverBytes)) _coverAvg = AverageColor(bytes, w, h);
        _hasCoverAvg = true;
        // Ein neues Cover blendet weich ein (nicht beim ersten Erscheinen oder nur neuem Gerät)
        if (!first && !ReferenceEquals(bytes, _coverBytes)) _coverFade = 0;
        _coverBytes = bytes;
    }

    /// <summary>Mittlere Farbe des Covers, etwas satter (wie saturate(1.4) im Original).</summary>
    private static Vector3 AverageColor(byte[] px, int w, int h)
    {
        double r = 0, g = 0, b = 0, a = 0;
        int step = Math.Max(1, (int)Math.Sqrt((double)w * h / 4096));
        for (int y = 0; y < h; y += step)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x += step)
            {
                int i = row + x * 4;
                b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3];
            }
        }
        if (a <= 0) return new Vector3(0.5f);
        var col = new Vector3((float)(r / a), (float)(g / a), (float)(b / a));
        float lum = Vector3.Dot(col, new Vector3(0.2126f, 0.7152f, 0.0722f));
        col = new Vector3(lum) + (col - new Vector3(lum)) * 1.4f;
        return Vector3.Clamp(col, Vector3.Zero, Vector3.One);
    }

    private void DisposeText()
    {
        for (int i = 0; i < 3; i++) { _lines[i]?.Dispose(); _lines[i] = null; _lineText[i] = null; }
        _lElapsed?.Dispose(); _lElapsed = null;
        _lRemain?.Dispose(); _lRemain = null;
        _icPrev?.Dispose(); _icPrev = null;
        _icNext?.Dispose(); _icNext = null;
        _icPlay?.Dispose(); _icPlay = null;
        _icPause?.Dispose(); _icPause = null;
        _fTitle = _fArtist = _fAlbum = _fTime = _fIcon = _fIconMain = null;
        _fmtK = -1;
    }

    private void DisposeDevice()
    {
        _ring?.Dispose(); _ring = null;
        _ringKey = new Vector4(-1);
        _coverBrush?.Dispose(); _coverBrush = null;
        _cover?.Dispose(); _cover = null;
        _glow?.Dispose(); _glow = null;
        _artEmpty?.Dispose(); _artEmpty = null;
        _maskRight?.Dispose(); _maskRight = null;
        _maskBoth?.Dispose(); _maskBoth = null;
        _round?.Dispose(); _round = null;
        _brush?.Dispose(); _brush = null;
        _ctxPtr = 0;
    }

    public override void Dispose()
    {
        DisposeText();
        DisposeDevice();
    }

    // ---------- Hilfen ----------

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static RoundedRectangle RRect(float x, float y, float w, float h, float r) =>
        new() { Rect = new RawRectF(x, y, x + w, y + h), RadiusX = r, RadiusY = r };
}
