namespace GlassBar.Tests;

[Collection("Datenordner")]
public class LogTests
{
    [Fact]
    public void GleicheMeldung_WirdNurEinmalProMinuteGeschrieben()
    {
        string msg = "Testmeldung " + Guid.NewGuid();
        Log.Info(msg);
        Log.Info(msg);
        Log.Error("Testort", new InvalidOperationException(msg));
        Log.Error("Testort", new InvalidOperationException(msg));

        string all = File.ReadAllText(Log.FilePath);
        Assert.Equal(2, all.Split(msg).Length - 1); // Info einmal, Fehler einmal
        Assert.StartsWith(Config.Folder, Log.FilePath);
    }
}
