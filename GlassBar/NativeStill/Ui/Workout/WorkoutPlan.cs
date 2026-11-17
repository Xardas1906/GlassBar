using System.Globalization;

namespace GlassBar.NativeStill.Ui.Workout;

// =====================================================================================================
// Das Denken des Trainings, ohne Bildschirm (workout-plan.js): die Übungsleitern, wie sich ein Ziel bewegt,
// wenn sich etwas leicht oder schwer anfühlt, was heute vorgeschlagen wird und welche Einheit daraus wird.
// Alles mit dem eigenen Körpergewicht. Ein Vorschlag blockiert nie etwas.
// =====================================================================================================

/// <summary>Eine Stufe einer Leiter: Name, kurzer Hinweis, ob sie je Seite gemacht wird.</summary>
public sealed record Rung(string Name, string Cue, bool Sides = false);

/// <summary>Eine Bewegung: Art ("reps", "hold", "cardio"), Muskelgruppe und die Stufen, leichteste zuerst.</summary>
public sealed record Move(string Kind, string Group, Rung[] Rungs);

/// <summary>Ein Plan: Name, was er beansprucht (und daher erholt sein muss) und seine Übungen.</summary>
public sealed record PlanDef(string Label, string[] Primary, string[]? Moves = null, string[][]? Rotate = null, string[]? TableMoves = null, bool Circuit = false);

/// <summary>Spanne für Wiederholungen oder Sekunden und die Schritte, mit denen sich ein Ziel bewegt.</summary>
public sealed record Span(int Min, int Max, int Start, int Right, int Easy, int Hard, int Down);

/// <summary>Ein gespeichertes Ziel: Stufe, Anzahl (Wdh. oder Sekunden), Sätze.</summary>
public sealed class Target
{
    public int Rung { get; set; }
    public int N { get; set; }
    public int Sets { get; set; }
    public Target Copy() => new() { Rung = Rung, N = N, Sets = Sets };
}

/// <summary>Ein Schritt der Einheit: "ready", "work" oder "rest".</summary>
public sealed class Step
{
    public string K { get; set; } = "rest";
    public string M { get; set; } = "";
    public int Rung { get; set; }
    public int N { get; set; }
    public int Set { get; set; }
    public int Sets { get; set; }
    public int Sec { get; set; }
    public Step Copy() => (Step)MemberwiseClone();
}

/// <summary>Eine erledigte Übung in einem Eintrag des Verlaufs.</summary>
public sealed class DoneMove
{
    public string M { get; set; } = "";
    public int Rung { get; set; }
    public int N { get; set; }
    public int Sets { get; set; }
    /// <summary>"easy", "right", "hard" oder null.</summary>
    public string? Feel { get; set; }
}

/// <summary>Ein Training im Verlauf. Day = Tag des Beginns, At = Ende (ms seit 1970), ab dem Erholung zählt.</summary>
public sealed class HistoryEntry
{
    public string Day { get; set; } = "";
    public long At { get; set; }
    public string Plan { get; set; } = "full";
    public int Mins { get; set; }
    public string Focus { get; set; } = "muscle";
    public bool Early { get; set; }
    public List<DoneMove> Done { get; set; } = new();
}

/// <summary>Körperdaten für den BMI (bleiben auf diesem PC).</summary>
public sealed class BodyData
{
    public int Age { get; set; } = 25;
    public double Cm { get; set; } = 170;
    public double Kg { get; set; } = 65;
}

/// <summary>Alles Gespeicherte des Trainings (im Original "still.workout.v1").</summary>
public sealed class WorkoutData
{
    public bool Setup { get; set; }
    public int Level { get; set; } = 1;
    public int? PushUps { get; set; }
    public Dictionary<string, Target> Targets { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public bool FullB { get; set; }
    public BodyData? Body { get; set; }
    public string HUnit { get; set; } = "cm";
    public string WUnit { get; set; } = "kg";
    public long SavedAt { get; set; }
}

/// <summary>Was heute vorgeschlagen wird.</summary>
public sealed record Recommendation(string Kind, string? Plan, string Alt, string Reason, bool Gentle = false);

/// <summary>Optionen für eine Einheit.</summary>
public sealed record SessionOptions(string Focus, bool Table, bool FullB, double RestScale, int Level, bool Gentle);

public static class WorkoutPlan
{
    private static Rung L(string name, string cue, bool sides = false) => new(name, cue, sides);

    // Übersetzung (die Methode L oben verdeckt die Klasse GlassBar.L). Namen und Hinweise der Stufen, Pläne und
    // Muskelgruppen bleiben hier deutsch (sie sind zugleich Schlüssel) und werden erst beim Anzeigen übersetzt.
    private static string T(string de) => global::GlassBar.L.T(de);
    private static string F(string de, params object?[] args) => global::GlassBar.L.F(de, args);

    /* ---------- Leitern ----------
       Jede Bewegung ist eine Leiter aus Varianten, die leichteste zuerst. Fortschritt heißt klettern:
       mit dem eigenen Gewicht baut mehr Wiederholung allein vor allem Ausdauer auf. */
    public static readonly Dictionary<string, Move> Moves = new()
    {
        ["push"] = new("reps", "chest", [
            L("Wand-Liegestütz", "Hände an die Wand, Körper gestreckt. Brust zur Wand und zurück."),
            L("Erhöhter Liegestütz", "Hände auf einer Bett- oder Sofakante. Halte den Körper in einer Linie."),
            L("Knie-Liegestütz", "Knie am Boden, Hüfte vorn. Senke die Brust zwischen die Hände."),
            L("Liegestütz", "Körper in einer Linie. Senke dich, bis die Brust fast den Boden berührt."),
            L("Liegestütz mit erhöhten Füßen", "Füße auf einem Bett oder Sofa. Langsam senken, wieder hochdrücken."),
            L("Archer-Liegestütz", "Hände weit. Zu einer Hand absenken, der andere Arm bleibt gestreckt.", true),
        ]),
        ["wide"] = new("reps", "chest", [
            L("Breiter erhöhter Liegestütz", "Hände weit auf einer Bett- oder Sofakante. Ellbogen im 45-Grad-Winkel."),
            L("Breiter Knie-Liegestütz", "Hände weiter als schulterbreit, Knie am Boden."),
            L("Breiter Liegestütz", "Hände weiter als schulterbreit. Du spürst es quer über der Brust."),
        ]),
        ["close"] = new("reps", "arms", [
            L("Enge Knie-Liegestütze", "Hände unter den Schultern, Ellbogen streifen die Rippen."),
            L("Diamant-Liegestütz", "Daumen und Finger berühren sich. Ellbogen nach hinten, nicht nach außen."),
            L("Sphinx-Liegestütz", "Vom Unterarmstütz in den gestreckten Stütz und kontrolliert zurück."),
        ]),
        ["pike"] = new("reps", "shoulders", [
            L("Erhöhter Pike-Liegestütz", "Hände auf einer Sofakante, Hüfte hoch. Senke den Kopf zu den Händen."),
            L("Pike-Liegestütz", "Hüfte hoch, Beine lang. Senke den Kopf zum Boden."),
            L("Pike-Liegestütz mit erhöhten Füßen", "Füße auf einem Bett oder Sofa, Hüfte über den Schultern."),
        ]),
        ["pull"] = new("reps", "back", [
            L("Y-Heben in Bauchlage", "Bauchlage, Arme in Y-Form. Hebe sie mit dem oberen Rücken."),
            L("Umgekehrter Schnee-Engel", "Bauchlage, Brust angehoben. Führe die Arme von der Hüfte nach oben über den Kopf."),
            L("Superman-Zug", "Brust und Arme hoch. Ziehe die Ellbogen zu den Rippen."),
        ]),
        ["row"] = new("reps", "back", [
            L("Tischrudern mit gebeugten Knien", "Unter einem Tisch, der nicht kippen kann. Ziehe die Brust zur Kante."),
            L("Tischrudern mit gestreckten Beinen", "Fersen unten, Körper gestreckt. Ziehe die Brust zur Kante."),
            L("Tischrudern mit erhöhten Füßen", "Fersen auf einem Stuhl. Lass die Hüfte nicht durchhängen."),
        ]),
        ["curl"] = new("reps", "arms", [
            L("Selbstwiderstands-Curl", "Drücke mit einer Hand nach unten, während die andere dagegen beugt.", true),
            L("Langsamer Selbstwiderstands-Curl", "Fünf Sekunden hoch, fünf Sekunden runter, mit kräftigem Druck.", true),
        ]),
        ["squat"] = new("reps", "legs", [
            L("Kniebeuge mit Stuhl", "Langsam hinsetzen, kaum berühren und wieder aufstehen."),
            L("Kniebeuge", "Füße schulterbreit. Nach hinten setzen, Brust hoch, Fersen unten."),
            L("Split Squat", "Ein Fuß nach vorn. Senke das hintere Knie Richtung Boden.", true),
            L("Bulgarische Kniebeuge", "Hinterer Fuß auf einem Bett oder Sofa. Senke dich gerade nach unten.", true),
            L("Unterstützte einbeinige Kniebeuge", "An einem Türrahmen festhalten. Ein Bein nach vorn, auf das andere setzen.", true),
        ]),
        ["hinge"] = new("reps", "legs", [
            L("Glute Bridge", "Rückenlage, Knie gebeugt. Drücke die Hüfte hoch und spanne an."),
            L("Einbeinige Glute Bridge", "Ein Bein hoch. Halte die Hüfte beim Heben waagerecht.", true),
            L("Erhöhter Hip Thrust", "Schultern auf einer Sofakante, ein Bein hoch. Hüfte ganz nach oben.", true),
        ]),
        ["calf"] = new("reps", "legs", [
            L("Wadenheben", "Auf die Zehenspitzen, kurz halten, langsam absenken."),
            L("Einbeiniges Wadenheben", "Halte dich zum Gleichgewicht an einer Wand. Ganz hoch, ganz runter.", true),
        ]),
        ["abs"] = new("reps", "core", [
            L("Dead Bug", "Rücken auf den Boden gedrückt. Strecke den gegenüberliegenden Arm und das Bein weg."),
            L("Umgekehrter Crunch", "Rolle die Hüfte vom Boden, Knie zur Brust."),
            L("Beinheben im Liegen", "Beine lang, unteren Rücken am Boden. Langsam heben und senken."),
            L("V-up", "Arme und Beine heben sich gemeinsam. Berühre deine Schienbeine."),
        ]),
        ["plank"] = new("hold", "core", [
            L("Knie-Unterarmstütz", "Auf Unterarmen und Knien, Körper gerade vom Knie bis zum Kopf."),
            L("Unterarmstütz", "Auf Unterarmen und Zehen. Gesäß anspannen, nicht durchhängen."),
            L("Hohlkörper halten", "Rückenlage, unterer Rücken am Boden, Arme und Beine schweben."),
        ]),
        ["side"] = new("hold", "core", [
            L("Seitstütz auf den Knien", "Auf einem Unterarm und Knie. Hüfte angehoben, Körper gerade.", true),
            L("Seitstütz", "Auf einem Unterarm und den Füßen. Hüfte hoch und ruhig.", true),
        ]),
        ["cJack"] = new("cardio", "cardio", [
            L("Step Jack", "Einen Fuß zur Seite, während die Arme hochgehen. Kein Springen nötig."),
            L("Hampelmann", "Springe mit den Füßen auseinander, während die Arme hochgehen. Weich landen."),
        ]),
        ["cBurpee"] = new("cardio", "cardio", [
            L("Burpee mit Rückschritt", "Hände auf den Boden, in den Stütz zurücksteigen, heransteigen, aufstehen."),
            L("Burpee", "Hände auf den Boden, nach hinten springen, heranspringen, hochspringen."),
        ]),
        ["cClimb"] = new("cardio", "cardio", [
            L("Langsamer Mountain Climber", "Aus dem Unterarmstütz ein Knie nach dem anderen heranziehen."),
            L("Mountain Climber", "Gleicher Stütz, schnellere Knie. Hüfte tief halten."),
        ]),
        ["cLunge"] = new("cardio", "cardio", [
            L("Ausfallschritt rückwärts", "Schritt zurück, absenken, hochdrücken. Beine jedes Mal wechseln."),
            L("Sprungkniebeuge", "In die Hocke, hochspringen, weich landen und wieder."),
        ]),
    };

    /* ---------- Pläne ----------
       primary: was ein Plan verlangt, und was daher dafür erholt sein muss. */
    public static readonly Dictionary<string, PlanDef> Plans = new()
    {
        ["full"] = new("Ganzkörper", ["chest", "back", "legs", "core"], Rotate: [["push", "pull", "squat", "plank"], ["pike", "pull", "hinge", "abs"]]),
        ["chest"] = new("Brust", ["chest"], ["push", "wide", "close"]),
        ["back"] = new("Rücken", ["back"], ["pull", "hinge", "plank"], TableMoves: ["row", "pull", "hinge"]),
        ["arms"] = new("Arme", ["arms", "shoulders"], ["close", "curl", "pike"]),
        ["legs"] = new("Beine", ["legs"], ["squat", "hinge", "calf"]),
        ["abs"] = new("Bauch", ["core"], ["abs", "plank", "side"]),
        ["cardio"] = new("Ausdauer", [], ["cJack", "cBurpee", "cClimb", "cLunge"], Circuit: true),
    };
    public static readonly string[] PlanIds = ["full", "chest", "back", "arms", "legs", "abs", "cardio"];
    public static readonly Dictionary<string, string> GroupName = new()
    {
        ["chest"] = "Brust", ["back"] = "Rücken", ["arms"] = "Arme", ["shoulders"] = "Schultern", ["legs"] = "Beine", ["core"] = "Rumpf",
    };

    public static List<string> MovesFor(string planId, bool table, bool fullB)
    {
        var p = Plans[planId];
        var list = p.Rotate != null ? p.Rotate[fullB ? 1 : 0] : (table && p.TableMoves != null ? p.TableMoves : p.Moves!);
        if (!table) return list.ToList();
        bool hasRow = p.TableMoves != null && list.Contains("row");
        return list.Select(m => m == "pull" && !hasRow ? "row" : m).ToList();
    }

    /* ---------- Ausgangspunkte ----------
       Eine Frage („Wie viele Liegestütze am Stück?“) setzt jede Leiter. */
    public static int LevelFor(int pushUps) => pushUps <= 0 ? 0 : pushUps <= 5 ? 1 : pushUps <= 15 ? 2 : pushUps <= 30 ? 3 : 4;

    private static readonly Dictionary<string, int[]> Start = new()
    {
        ["push"] = [0, 2, 3, 3, 4], ["wide"] = [0, 1, 2, 2, 2], ["close"] = [0, 0, 1, 1, 2], ["pike"] = [0, 0, 1, 1, 2], ["pull"] = [0, 1, 1, 2, 2],
        ["row"] = [0, 0, 1, 1, 2], ["curl"] = [0, 0, 0, 1, 1], ["squat"] = [0, 1, 1, 2, 3], ["hinge"] = [0, 0, 1, 1, 2], ["calf"] = [0, 0, 0, 1, 1],
        ["abs"] = [0, 1, 1, 2, 3], ["plank"] = [0, 1, 1, 1, 2], ["side"] = [0, 0, 1, 1, 1], ["cJack"] = [0, 0, 1, 1, 1], ["cBurpee"] = [0, 0, 0, 1, 1],
        ["cClimb"] = [0, 0, 1, 1, 1], ["cLunge"] = [0, 0, 0, 1, 1],
    };

    /* ---------- Ziele ----------
       Muskelaufbau: 8 bis 20 Wdh.; darüber ein 4. Satz, dann die nächste Variante.
       Kraft: 3 bis 8 Wdh.; darüber, oder zu leicht, gleich die nächste Variante. */
    private static readonly Span MuscleReps = new(8, 20, 8, 1, 3, 2, 12);
    private static readonly Span MuscleHold = new(20, 60, 20, 5, 10, 5, 30);
    private static readonly Span StrengthReps = new(3, 8, 5, 1, 2, 1, 6);
    private static readonly Span StrengthHold = new(10, 30, 15, 5, 10, 5, 20);
    private static readonly Span Cardio = new(20, 60, 30, 5, 10, 5, 40);
    public const int DefaultSets = 3;

    public static Span RangeOf(string kind, string focus) =>
        kind == "cardio" ? Cardio
        : focus == "strength" ? (kind == "reps" ? StrengthReps : StrengthHold)
        : (kind == "reps" ? MuscleReps : MuscleHold);

    private static int Clamp(int v, int lo, int hi) => Math.Min(hi, Math.Max(lo, v));

    public static Target StartState(string id, int level, string focus)
    {
        var m = Moves[id];
        var r = RangeOf(m.Kind, focus);
        int[] s = Start[id];
        int rung = Math.Min(level >= 0 && level < s.Length ? s[level] : 0, m.Rungs.Length - 1);
        return new Target { Rung = rung, N = level >= 3 && m.Kind == "reps" && focus != "strength" ? 12 : r.Start, Sets = DefaultSets };
    }

    /// <summary>Ein gespeichertes Ziel, passend gemacht für den Fokus und die Leiter, wie sie jetzt sind.</summary>
    public static Target Fit(string id, Target st, string focus)
    {
        var m = Moves[id];
        var r = RangeOf(m.Kind, focus);
        return new Target { Rung = Clamp(st.Rung, 0, m.Rungs.Length - 1), N = Clamp(st.N, r.Min, r.Max), Sets = Clamp(st.Sets == 0 ? DefaultSets : st.Sets, 2, 5) };
    }

    /// <summary>Das Ziel fürs nächste Mal, aus einem Tipp nach dem Training. Auch „genau richtig“ bewegt es ein
    /// wenig: das ist die Steigerung. Über die Spanne hinaus geht es eine Stufe hoch.</summary>
    public static Target NextTarget(string id, Target st0, string focus, string feel)
    {
        var m = Moves[id];
        var r = RangeOf(m.Kind, focus);
        int top = m.Rungs.Length - 1;
        var st = Fit(id, st0, focus);
        bool strength = focus == "strength" && m.Kind == "reps";
        bool muscle = focus != "strength" && m.Kind == "reps";

        if (feel == "hard")
        {
            st.N -= r.Hard;
            if (st.N >= r.Min) return st;
            if (muscle && st.Sets > DefaultSets) return new Target { Rung = st.Rung, Sets = DefaultSets, N = r.Min + 2 };
            if (st.Rung > 0) return new Target { Rung = st.Rung - 1, N = r.Down, Sets = Math.Min(st.Sets, DefaultSets) };
            return new Target { Rung = st.Rung, N = r.Min, Sets = Math.Max(2, st.Sets - 1) };
        }
        if (strength && feel == "easy")
        {
            if (st.Rung < top) return new Target { Rung = st.Rung + 1, N = r.Start, Sets = st.Sets };
            st.N = Math.Min(r.Max, st.N + r.Easy);
            return st;
        }
        st.N += feel == "easy" ? r.Easy : r.Right;
        if (st.N <= r.Max) return st;
        if (muscle && st.Sets < 4) return new Target { Rung = st.Rung, Sets = 4, N = r.Max - 4 };
        if (st.Rung < top) return new Target { Rung = st.Rung + 1, N = r.Min, Sets = DefaultSets };
        st.N = r.Max; // oben auf der Leiter: hier gibt es nichts mehr hinzuzufügen
        return st;
    }

    /* ---------- Eine Einheit ---------- */

    private const double Pace = 3.5;  // Sekunden je Wiederholung
    private const int SetUp = 6;      // um in Position zu kommen
    public const int Ready = 20;      // von der Maus auf den Boden
    private const int RestMuscle = 75, RestStrength = 150, RestCardio = 20, RestRound = 60;
    private const int NextMove = 15;  // zusätzlich zur Pause, um die Übung zu wechseln

    public static int WorkSeconds(string id, int rung, int n)
    {
        var m = Moves[id];
        int sides = m.Rungs[rung].Sides ? 2 : 1;
        if (m.Kind == "reps") return (int)Math.Round(n * Pace * sides + SetUp * sides, MidpointRounding.AwayFromZero);
        return n * sides + 4 * sides;
    }

    private static Step WorkStep(string id, Target st, int set, int sets) =>
        new() { K = "work", M = id, Rung = st.Rung, N = st.N, Set = set, Sets = sets, Sec = WorkSeconds(id, st.Rung, st.N) };

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Die Liste der Schritte: bereit machen, dann Satz und Pause im Wechsel.</summary>
    public static List<Step> BuildSession(string planId, IReadOnlyDictionary<string, Target> targets, SessionOptions o)
    {
        var p = Plans[planId];
        double scale = o.RestScale > 0 ? o.RestScale : 1;
        var list = MovesFor(planId, o.Table, o.FullB);
        var steps = new List<Step> { new() { K = "ready", Sec = Ready } };
        Target St(string id)
        {
            var t = Fit(id, targets.TryGetValue(id, out var x) ? x : StartState(id, o.Level, o.Focus), o.Focus);
            if (o.Gentle) t.Sets = Math.Max(2, t.Sets - 1);
            return t;
        }
        if (p.Circuit)
        {
            int rounds = o.Gentle ? 2 : 3;
            for (int round = 0; round < rounds; round++)
                for (int i = 0; i < list.Count; i++)
                {
                    steps.Add(WorkStep(list[i], St(list[i]), round, rounds));
                    bool last = i == list.Count - 1;
                    if (!last) steps.Add(new Step { K = "rest", Sec = Round(RestCardio * scale) });
                    else if (round < rounds - 1) steps.Add(new Step { K = "rest", Sec = Round(RestRound * scale) });
                }
        }
        else
        {
            int rest = Round((o.Focus == "strength" ? RestStrength : RestMuscle) * scale);
            for (int i = 0; i < list.Count; i++)
            {
                var t = St(list[i]);
                for (int s = 0; s < t.Sets; s++)
                {
                    steps.Add(WorkStep(list[i], t, s, t.Sets));
                    if (s < t.Sets - 1) steps.Add(new Step { K = "rest", Sec = rest });
                    else if (i < list.Count - 1) steps.Add(new Step { K = "rest", Sec = rest + NextMove });
                }
            }
        }
        return steps;
    }

    public static int SessionSeconds(List<Step> steps) { int s = 0; foreach (var x in steps) s += x.Sec; return s; }
    public static int SessionMinutes(List<Step> steps) => Math.Max(1, Round(SessionSeconds(steps) / 60.0));

    // Die Satznummern folgen der Liste, nachdem ein Satz dazugekommen oder weggefallen ist.
    private static void Renumber(List<Step> steps, string id)
    {
        var work = steps.Where(s => s.K == "work" && s.M == id).ToList();
        for (int i = 0; i < work.Count; i++) { work[i].Set = i; work[i].Sets = work.Count; }
    }

    /// <summary>„Leichter“ oder „Schwerer“ mitten im Training. Ändert, was von dieser Übung heute übrig ist:
    /// Muskelaufbau bewegt Wiederholungen, dann Sätze, dann die Variante; Kraft zuerst die Variante.
    /// Gibt zurück, was sich geändert hat (oder null); changed = false, wenn nichts mehr ging.</summary>
    public static string? AdjustNow(List<Step> steps, int at, int dir, string focus, out bool changed)
    {
        changed = false;
        if (at < 0 || at >= steps.Count || steps[at].K != "work") return null;
        var here = steps[at];
        string id = here.M;
        var m = Moves[id];
        var r = RangeOf(m.Kind, focus);
        int top = m.Rungs.Length - 1;
        List<Step> Left() => steps.Skip(at).Where(s => s.K == "work" && s.M == id).ToList();
        void Apply(int rg, int n) { foreach (var s in Left()) { s.Rung = rg; s.N = n; s.Sec = WorkSeconds(id, rg, n); } }
        string amountFmt = m.Kind == "reps" ? "{0} Wdh." : "{0} s";
        int step = m.Kind == "reps" ? (focus == "strength" ? 1 : 2) : 5;
        bool circuit = m.Kind == "cardio";

        bool AddSet()
        {
            var work = steps.Where(s => s.K == "work" && s.M == id).ToList();
            if (circuit || work.Count >= 5) return false;
            int lastIdx = steps.LastIndexOf(work[^1]);
            int restBefore = lastIdx > 0 && steps[lastIdx - 1].K == "rest" ? steps[lastIdx - 1].Sec : 75;
            var copy = here.Copy();
            copy.Sec = WorkSeconds(id, here.Rung, here.N);
            steps.InsertRange(lastIdx + 1, [new Step { K = "rest", Sec = restBefore }, copy]);
            Renumber(steps, id);
            return true;
        }
        bool DropSet()
        {
            var remaining = Left();
            if (circuit || remaining.Count < 2) return false;
            int i = steps.LastIndexOf(remaining[^1]);
            steps.RemoveRange(i - 1, 2);
            Renumber(steps, id);
            return true;
        }

        // Das Anwenden ändert auch diesen Schritt, daher wird die Stufe zuerst gelesen.
        int rung = here.Rung;
        bool rungFirst = focus == "strength" && m.Kind == "reps";
        changed = true;
        if (dir > 0)
        {
            if (rungFirst && rung < top) { Apply(rung + 1, r.Start); return F("Schwerer: {0}", T(m.Rungs[rung + 1].Name)); }
            if (here.N < r.Max) { int n = Math.Min(r.Max, here.N + step); Apply(rung, n); return F("Schwerer: {0}", F(amountFmt, n)); }
            if (AddSet()) return T("Schwerer: ein Satz mehr");
            if (rung < top) { Apply(rung + 1, r.Min); return F("Schwerer: {0}", T(m.Rungs[rung + 1].Name)); }
            changed = false;
            return T("Das ist die oberste Stufe");
        }
        if (rungFirst && rung > 0) { Apply(rung - 1, r.Start); return F("Leichter: {0}", T(m.Rungs[rung - 1].Name)); }
        if (here.N > r.Min) { int n = Math.Max(r.Min, here.N - step); Apply(rung, n); return F("Leichter: {0}", F(amountFmt, n)); }
        if (DropSet()) return T("Leichter: ein Satz weniger");
        if (rung > 0) { Apply(rung - 1, r.Down); return F("Leichter: {0}", T(m.Rungs[rung - 1].Name)); }
        changed = false;
        return T("Das ist die leichteste Version");
    }

    /* ---------- Tage ---------- */

    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateTime ParseDay(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
    /// <summary>Ganze Tage, damit eine Zeitumstellung keinen Tag 23 Stunden lang machen kann.</summary>
    public static int DayNum(string key) => (int)(ParseDay(key).Date - DateTime.UnixEpoch.Date).TotalDays;
    public static string AddDays(string key, int n) => DayKey(ParseDay(key).AddDays(n));
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTime LocalOf(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static HashSet<string> GroupsOf(HistoryEntry e)
    {
        var o = new HashSet<string>();
        foreach (var d in e.Done) if (Moves.TryGetValue(d.M, out var m)) o.Add(m.Group);
        return o;
    }

    /* Erholung zählt in Stunden seit einer Einheit, nie nach dem Datum: Mitternacht bedeutet einem Muskel
       nichts. 44 statt 48 Stunden, damit Training jeden zweiten Tag zur etwa gleichen Zeit als erholt zählt. */
    private const double Hour = 3600000;
    private const double Rested = 44, RestedHard = 68, JustTrained = 16, InARow = 36;

    private static long EntryTime(HistoryEntry e)
    {
        if (e.At > 0) return e.At;
        var d = ParseDay(e.Day).AddHours(12);
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }
    private static double? HoursSince(HistoryEntry e, long now)
    {
        double h = (now - EntryTime(e)) / Hour;
        return h < 0 ? null : h;
    }

    private sealed class Recovery
    {
        public readonly Dictionary<string, double> Since = new(), Needs = new();
    }

    private static Recovery RecoveryOf(List<HistoryEntry> history, long now)
    {
        var r = new Recovery();
        foreach (var e in history)
        {
            if (HoursSince(e, now) is not double h) continue;
            bool FeltHard(string g) => e.Done.Any(d => Moves.TryGetValue(d.M, out var m) && m.Group == g && d.Feel == "hard");
            foreach (var g in GroupsOf(e))
            {
                if (!r.Since.TryGetValue(g, out var s) || h < s)
                {
                    r.Since[g] = h;
                    r.Needs[g] = FeltHard(g) ? RestedHard : Rested;
                }
                else if (h == s && FeltHard(g)) r.Needs[g] = RestedHard;
            }
        }
        return r;
    }

    private static bool IsRested(string g, Recovery r) => !r.Since.TryGetValue(g, out var s) || s >= r.Needs[g];
    private static bool PlanRested(string id, Recovery r) => Plans[id].Primary.All(g => IsRested(g, r));

    private static string AgoText(double hours)
    {
        if (hours < 1) return T("vor weniger als einer Stunde");
        if (hours < 48)
        {
            int h = Round(hours);
            return h == 1 ? T("vor 1 Stunde") : F("vor {0} Stunden", h);
        }
        return F("vor {0} Tagen", (int)Math.Floor(hours / 24));
    }

    /* Trainingstage in Folge bis jetzt: Einheiten unter 16 Stunden Abstand sind dasselbe Training, egal wie
       die Daten fallen, und eine Lücke über 36 Stunden beendet die Reihe. */
    private static int DaysInARow(List<(HistoryEntry E, double H)> past)
    {
        if (past.Count == 0 || past[0].H > InARow) return 0;
        int run = 1;
        for (int i = 1; i < past.Count; i++)
        {
            double gap = past[i].H - past[i - 1].H;
            if (gap > InARow) break;
            if (gap >= JustTrained) run++;
        }
        return run;
    }

    /// <summary>Was heute vorgeschlagen wird. An einem Ruhetag ist Alt das, was man stattdessen machen kann.</summary>
    public static Recommendation Recommend(List<HistoryEntry> history, long now, int goal)
    {
        var r = RecoveryOf(history, now);
        var past = history.Select(e => (E: e, H: HoursSince(e, now))).Where(x => x.H != null)
            .Select(x => (x.E, H: x.H!.Value)).OrderBy(x => x.H).ToList();
        double? lastH = past.Count > 0 ? past[0].H : null;

        // Das Wochenziel bleibt beim Datum: genaue Stunden würden den nächsten Montag verschieben.
        int today = DayNum(DayKey(LocalOf(now)));
        int last7 = past.Select(x => DayNum(x.E.Day)).Where(d => today - d >= 0 && today - d <= 6).Distinct().Count();
        int run = DaysInARow(past);

        const double Fortnight = 14 * 24;
        double Score(string id)
        {
            var gs = Plans[id].Primary;
            if (id == "full") return double.PositiveInfinity;
            if (gs.Length == 0) return 0;
            return gs.Sum(g => Math.Min(r.Since.TryGetValue(g, out var s) ? s : Fortnight, Fortnight)) / gs.Length;
        }
        var ready = PlanIds.Where(id => id != "cardio" && PlanRested(id, r)).OrderByDescending(Score).ToList();
        string alt = ready.Count > 0 ? ready[0] : "cardio";
        Recommendation Rest(string reason) => new("rest", null, alt, reason);

        if (lastH is not double last) return new("train", "full", "full", T("Eine ausgewogene erste Einheit, von allem etwas."));
        if (last < JustTrained) return Rest(F("Du hast {0} trainiert. Muskeln wachsen, während du dich erholst.", AgoText(last)));
        if (last >= Fortnight)
            return new("train", "full", "full", F("{0} Tage seit dem letzten Mal. Eine leichtere Einheit zum Wiedereinstieg.", (int)Math.Floor(last / 24)), true);
        if (run >= 3) return Rest(F("{0} Tage in Folge. Ein Ruhetag hilft der nächsten Einheit.", run));
        if (last7 >= goal)
            return Rest(last7 == 1
                ? F("1 Training diese Woche, dein Ziel ist {0}. Die Pause hast du dir verdient.", goal)
                : F("{0} Trainings diese Woche, dein Ziel ist {1}. Die Pause hast du dir verdient.", last7, goal));
        var fullBody = past.FirstOrDefault(x => x.E.Plan == "full");
        if (fullBody.E != null && fullBody.H < Rested) return Rest(F("Ganzkörper {0}. Mehr Erholung macht das nächste Mal besser.", AgoText(fullBody.H)));
        bool hardLately = past.Any(x => x.H < InARow && x.E.Done.Any(d => d.Feel == "hard"));
        if (hardLately) return Rest(T("Deine letzte Einheit war anstrengend. Gönn dir etwas mehr Zeit."));
        if (ready.Count == 0) return Rest(T("Alles wurde kürzlich trainiert. Gönn ihm Erholung."));

        string plan = ready[0];
        if (plan == "full")
        {
            string why = last >= 7 * 24 ? F("{0} Tage seit dem letzten Mal. Alles ist erholt.", (int)Math.Floor(last / 24)) : T("Alles ist erholt, heute kann alles trainiert werden.");
            return new("train", plan, plan, why);
        }
        string g0 = Plans[plan].Primary[0];
        string reason = r.Since.TryGetValue(g0, out var since)
            ? F("{0}: zuletzt trainiert {1}.", T(GroupName[g0]), AgoText(since))
            : F("{0} hatte noch keine Einheit.", T(Plans[plan].Label));
        return new("train", plan, plan, reason);
    }

    /// <summary>Ein Wort zu etwas selbst Gewähltem; nie eine Warnung, die es aufhält.</summary>
    public static string NoteFor(string planId, List<HistoryEntry> history, long now)
    {
        var r = RecoveryOf(history, now);
        var tired = Plans[planId].Primary.Where(g => !IsRested(g, r)).ToList();
        if (tired.Count == 0) return "";
        string names = string.Join(T(" und "), tired.Select(x => T(GroupName[x])));
        return F("{0}: zuletzt trainiert {1}. Du entscheidest.", names, AgoText(r.Since[tired[0]]));
    }

    /* ---------- Body-Mass-Index ----------
       Erwachsenenwerte beginnen bei 19; darunter nutzt die WHO Kurven nach Alter und Geschlecht. */
    public static (double Value, string Label)? Bmi(BodyData? b)
    {
        if (b == null || b.Cm <= 0 || b.Kg <= 0) return null;
        double v = b.Kg / Math.Pow(b.Cm / 100, 2);
        if (b.Age < 19) return (v, T("Erwachsenenwerte gelten ab 19"));
        return (v, T(v < 18.5 ? "Unter dem üblichen Bereich" : v < 25 ? "Im üblichen Bereich" : "Über dem üblichen Bereich"));
    }
}
