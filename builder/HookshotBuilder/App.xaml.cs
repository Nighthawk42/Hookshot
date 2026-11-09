using System.Windows;

namespace HookshotBuilder;

public partial class App : Application
{
    /// --launch <game folder> (hookshot.exe in a game folder, builder/setup/setup.c): the launcher. Anything else: the builder.
    void OnStartup(object sender, StartupEventArgs e)
    {
        var i = Array.IndexOf(e.Args, "--launch");
        if (i >= 0 && i + 1 < e.Args.Length && !e.Args.Contains("--update"))
        {
            var launcher = new Launcher.LauncherWindow(e.Args[i + 1].TrimEnd('\\', '/'));
            MainWindow = launcher;
            launcher.Show();
            return;
        }
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }
}
