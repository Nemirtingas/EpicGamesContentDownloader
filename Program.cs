using EpicKit.Manifest;
using log4net.Layout;
using Microsoft.Extensions.Logging;

namespace EpicGamesContentDownloader;

public static class Program
{
    static void InitializeLogger()
    {
        PatternLayout patternLayout = new PatternLayout();
        patternLayout.ConversionPattern = "%date %-5level %logger - %message%newline";
        patternLayout.ActivateOptions();

        var rootLogger = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
        var colorConsole = new log4net.Appender.ManagedColoredConsoleAppender
        {
            Layout = patternLayout,
            Target = "Console.Out"
        };
        colorConsole.AddMapping(new log4net.Appender.ManagedColoredConsoleAppender.LevelColors { Level = log4net.Core.Level.Info, ForeColor = ConsoleColor.White });
        colorConsole.AddMapping(new log4net.Appender.ManagedColoredConsoleAppender.LevelColors { Level = log4net.Core.Level.Debug, ForeColor = ConsoleColor.DarkGray });
        colorConsole.AddMapping(new log4net.Appender.ManagedColoredConsoleAppender.LevelColors { Level = log4net.Core.Level.Warn, ForeColor = ConsoleColor.Yellow });
        colorConsole.AddMapping(new log4net.Appender.ManagedColoredConsoleAppender.LevelColors { Level = log4net.Core.Level.Error, ForeColor = ConsoleColor.Red });
        colorConsole.AddMapping(new log4net.Appender.ManagedColoredConsoleAppender.LevelColors { Level = log4net.Core.Level.Fatal, ForeColor = ConsoleColor.Black, BackColor = ConsoleColor.DarkRed });
        colorConsole.ActivateOptions();
        rootLogger.Root.AddAppender(colorConsole);

        rootLogger.Root.Level = log4net.Core.Level.Debug;
        rootLogger.Configured = true;

        Utils.Logger = new Log4NetLogger("Program");
    }

    public static async Task<int> MainAsync(string[] argv)
    {
        InitializeLogger();

        AccountSettingsStore.Instance.LoadFromFile("EpicGamesContentDownloader.store");

        if (!await ContentDownloader.LoginAsync())
        {
            Console.WriteLine("EGL authcode (get it at: https://www.epicgames.com/id/api/redirect?clientId=34a02cf8f4414e29b15921876da36f9a&responseType=code): ");
            // SID is not working anymore.
            //Console.WriteLine("EGL sid (get it at: https://www.epicgames.com/id/login?redirectUrl=https://www.epicgames.com/id/api/redirect): ");
            if (!await ContentDownloader.LoginWithAuthorizationCodeAsync(Console.ReadLine().Trim()))
            {
                Utils.Logger.LogInformation("Failed to login using cached credentials and authorization code, exiting now.");
                return -1;
            }
        }

        // 1. Example to list your owned applications (Login is mandatory here).

        // TODO: Find values of platform and label other than Windows and Live.
        var ownedApplications = await ContentDownloader.DownloadAppListAsync("Windows", "Live");

        if (ownedApplications.Count <= 0)
        {
            Utils.Logger.LogInformation("The user doesn't have any application, exiting now.");
            return -1;
        }
        var applicationAsset = ownedApplications.First().Value;

        // 2. Example on how to get extended details on an asset

        var storeApplicationInfos = await ContentDownloader.GetGameInfosAsync(applicationAsset.Namespace, applicationAsset.CatalogItemId, true);

        // 3. Download the manifest and the download details.

        var manifestDownloadInfos = await ContentDownloader.GetManifestDownloadInfosAsync(applicationAsset.Namespace, applicationAsset.CatalogItemId, applicationAsset.AssetId, "Windows", "Live");

        // 4. Parse manifest

        var manifest = new Manifest();
        using (var ms = new MemoryStream(manifestDownloadInfos.ManifestData))
            manifest.Read(ms);

        // 5. Create download configuration

        var downloadConfiguration = new DownloadConfiguration
        {
            OutputDirectory = "download",
            ParallelClientCount = 4,
        };
        downloadConfiguration.BaseUrls.AddRange(manifestDownloadInfos.BaseUrls);

        // 6. Build the download plan

        var downloadPlan = await ContentDownloader.BuildDownloadPlanAsync(manifest, downloadConfiguration);

        // 7. Run the download plan (download the files in the manifest).

        var cts = new CancellationTokenSource();
        await downloadPlan.RunDownloadPlanAsync(cts, (globalCounter, fileCounter) =>
        {
            // The downloadReportCallback is optional, it will be called whenever a chunk is downloaded, a file is downloaded, a file is validated. Usefull to keep track of your download.
        });

        return 0;
    }

    public static int Main(string[] argv)
        => MainAsync(argv).GetAwaiter().GetResult();
}
