using System;
using System.IO;
using System.Windows.Forms;

namespace GwtInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.Exists(args, a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            var opts = new InstallOptions
            {
                LaunchAfterInstall = false,
            };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    opts.InstallDir = args[++i];
                else if (a.Equals("--nick", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    opts.Nickname = args[++i];
                else if (a.Equals("--settings-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    opts.SettingsLocalAppDataRoot = args[++i];
                else if (a.Equals("--desktop-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    opts.DesktopDir = args[++i];
                else if (a.Equals("--programs-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    opts.StartMenuProgramsDir = args[++i];
                else if (a.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase))
                {
                    opts.CreateDesktopShortcut = false;
                    opts.CreateStartMenuShortcut = false;
                }
            }

            string exe = InstallerCore.RunInstallAsync(opts).GetAwaiter().GetResult();
            Console.WriteLine($"INSTALL_SILENT_OK Exe={exe} Size={new FileInfo(exe).Length}");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }
}
