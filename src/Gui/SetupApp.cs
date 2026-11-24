// Couch Companion Setup - the graphical installer / uninstaller (WPF).
// Built by Setup.cmd with the C# compiler that ships with Windows (.NET Framework 4.x), so it is
// C# 5 and the repository carries no binaries. Runs elevated (see app.manifest).
//
// Usage: "Couch Companion Setup.exe" [--src <path to the src folder>]

using System;
using System.Security.Principal;
using System.Windows;

static class Program
{
    public const string Version = "2.0.0";

    [STAThread]
    static int Main(string[] args)
    {
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(S.NotAdmin, S.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        if (!Backend.FindSources(args))
        {
            MessageBox.Show(S.ErrSources, S.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        return app.Run(new MainWindow());
    }
}
