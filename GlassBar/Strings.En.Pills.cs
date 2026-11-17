namespace GlassBar;

public static partial class Strings
{
    /// <summary>Timer und Wecker an der Uhr, Pomodoro- und Wasser-Pille, Aufgabenzeile und „Schon gewusst?“-Tipps.</summary>
    internal static readonly Dictionary<string, string> EnPills = new()
    {
        // ---------- Timer und Wecker (AlarmsOverlay) ----------
        ["So"] = "Sun",
        ["Mo"] = "Mon",
        ["Di"] = "Tue",
        ["Mi"] = "Wed",
        ["Do"] = "Thu",
        ["Fr"] = "Fri",
        ["Sa"] = "Sat",
        ["Einmalig"] = "Once",
        ["Jeden Tag"] = "Every day",
        ["Wochentags"] = "Weekdays",
        ["Wochenende"] = "Weekends",
        ["Timer lief ab, während du weg warst"] = "Timer ended while you were away",
        ["Timer abgelaufen"] = "Time's up",
        ["{0} · beendet um {1}"] = "{0} · ended at {1}",
        ["Verpasster Wecker"] = "Missed alarm",
        ["Wecker"] = "Alarm",
        ["Stopp"] = "Stop",
        ["Schlummern {0} Min."] = "Snooze {0} min",
        ["Endet um {0}"] = "Ends at {0}",
        ["Dreh an den Rädern oder wähle eine Zeit"] = "Spin the wheels or pick a time",
        ["Speichern"] = "Save",
        ["Stellen"] = "Set",
        ["klingelt in {0}"] = "rings in {0}",
        ["klingelt {0} {1}"] = "rings {0} {1}",
        ["Schon {0} Wecker. Entferne einen in der Liste neben der Uhr."] = "Already {0} alarms. Remove one from the list next to the clock.",
        ["pausiert"] = "paused",
        ["Pause"] = "Pause",
        ["Fortsetzen"] = "Resume",
        ["Abbrechen"] = "Cancel",
        ["Neuer Wecker"] = "New alarm",
        ["Klicken, um einen Timer oder Wecker zu stellen"] = "Click to set a timer or alarm",

        // ---------- Pomodoro-Pille ----------
        ["Fokus"] = "Focus",
        ["Kurze Pause"] = "Short break",
        ["Lange Pause"] = "Long break",
        ["{0} von {1} heute"] = "{0} of {1} today",

        // ---------- Wasser-Pille ----------
        ["Wasser"] = "Water",
        ["Später"] = "Later",
        ["{0} von {1} oz"] = "{0} of {1} oz",
        ["{0} von {1} L"] = "{0} of {1} L",
        ["Ziel erreicht. Schön gemacht."] = "Goal reached. Nicely done.",
        ["Zeit für ein Glas Wasser."] = "Time for a glass of water.",
        ["Heute noch {0}."] = "{0} to go today.",
        ["Erinnerungen gehen um {0:00}:00 weiter."] = "Reminders resume at {0:00}:00.",
        ["Nächste Erinnerung in {0}."] = "Next reminder in {0}.",

        // ---------- Aufgabenzeile (TasksOverlay) ----------
        ["Gut gemacht."] = "Well done.",
        ["Wunderbar gemacht."] = "Wonderfully done.",
        ["Super."] = "Great.",
        ["Schön gemacht."] = "Nicely done.",
        ["Eins weniger."] = "One down.",
        ["Wohlverdient."] = "Well earned.",
        ["Genau so."] = "That's the way.",
        ["{0} von {1} erledigt"] = "{0} of {1} done",
        ["Das ist alles. Wunderbar gemacht."] = "That's everything. Wonderfully done.",
        ["Weitere in GlassBar → Einstellungen → Heutige Aufgaben"] = "Add more in GlassBar → Settings → Today's tasks",

        // ---------- „Schon gewusst?“ (TipsOverlay) ----------
        ["Schon gewusst?"] = "Did you know?",
        ["Verstanden"] = "Got it",
        ["Weiter"] = "Next",
        ["Zeig es mir"] = "Show me",
        ["Ausprobieren"] = "Try it",
        ["Deine Uhr ist auch ein Timer"] = "Your clock is also a timer",
        ["Klicke auf die Uhrzeit, um direkt an ihren Ziffern einen Timer oder Wecker zu stellen."] = "Click the time to set a timer or alarm right on its digits.",
        ["Platziere alles, wo du möchtest"] = "Put everything where you like",
        ["Unter „Pillen und Uhr anordnen“ in den GlassBar-Einstellungen ziehst du die Pillen und die Uhr, wohin du möchtest."] = "With “Arrange pills and clock” in the GlassBar settings, you can drag the pills and the clock wherever you like.",
        ["Klänge zum Konzentrieren"] = "Sounds to help you focus",
        ["Regen, Wellen, ein Feuer und mehr, live erzeugt. Kombiniere so viele, wie du magst."] = "Rain, waves, a crackling fire and more, generated live. Mix as many as you like.",
        ["Ein Training ohne Geräte"] = "A workout with no equipment",
        ["Es schlägt vor, was heute trainiert wird, läuft von selbst und passt sich an, wenn du stärker wirst."] = "It suggests what to train today, runs on its own and adapts as you get stronger.",
        ["Ein Glas, das sich füllt"] = "A glass that fills up",
        ["Die Wasser-Pille begleitet deinen Tag und erinnert dich sanft."] = "The water pill keeps you company through the day and gently reminds you to drink.",
        ["Ein Fokus-Timer"] = "A focus timer",
        ["Die Pomodoro-Pille steuert Fokus-Einheiten und Pausen."] = "The Pomodoro pill runs your focus sessions and breaks.",
        ["Gebetszeiten"] = "Prayer times",
        ["Wenn du betest, kann eine Pille die fünf Gebete begleiten, mit Erinnerungen und Verlauf."] = "If you pray, a pill can follow the five daily prayers, with reminders and history.",
    };
}
