namespace ThemeSwitch;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            ConsoleAttach.EnsureOutput();
            Console.Error.WriteLine($"error: {ex.Message}{Environment.NewLine}{Environment.NewLine}{CliOptions.Usage}");
            return 2;
        }

        switch (options.Command)
        {
            case Command.Help:
                ConsoleAttach.EnsureOutput();
                Console.Out.WriteLine(CliOptions.Usage);
                return 0;
            case Command.Version:
                ConsoleAttach.EnsureOutput();
                Console.Out.WriteLine(AppInfo.Version);
                return 0;
            case Command.Report or Command.ApplyOnce or Command.Toggle:
                ConsoleAttach.EnsureOutput();
                // Redirected output would otherwise use the OEM code page and mangle "·".
                Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                return Cli.RunAsync(options).GetAwaiter().GetResult();
        }

        using var mutex = new Mutex(initiallyOwned: true, options.MutexName, out bool first);
        if (!first) return 0; // already running (e.g. autostart + manual launch)

        ApplicationConfiguration.Initialize();
        using var host = new AppHost(options, broadcastInBackground: true);
        Application.Run(new TrayApp(host));
        return 0;
    }
}
