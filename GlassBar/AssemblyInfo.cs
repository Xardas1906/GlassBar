using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Tests (GlassBar.Tests) dürfen interne Teile wie SafeFile, Log und Updater.ParseRelease prüfen
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GlassBar.Tests")]
