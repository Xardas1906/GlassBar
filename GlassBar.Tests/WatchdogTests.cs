namespace GlassBar.Tests;

public class WatchdogTests
{
    [Theory]
    [InlineData(Watchdog.RestartExitCode, true)]   // zu viele Fehler: GlassBar bittet selbst um Neustart
    [InlineData(unchecked((int)0xC0000005), true)] // Zugriffsverletzung
    [InlineData(unchecked((int)0xC00000FD), true)] // Stapelüberlauf
    [InlineData(unchecked((int)0xE0434352), true)] // unbehandelte .NET-Ausnahme
    [InlineData(unchecked((int)0x80131623), true)] // Environment.FailFast
    [InlineData(0, false)]                         // normal beendet
    [InlineData(1, false)]                         // Task-Manager
    [InlineData(-1, false)]                        // taskkill /F, Stop-Process
    public void NurAbstürzeFührenZumNeustart(int exitCode, bool crash)
    {
        Assert.Equal(crash, Watchdog.IsCrash(exitCode));
    }
}
