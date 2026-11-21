using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NetWatch.Alerts;
using NetWatch.Collectors;
using NetWatch.Core.Configuration;
using NetWatch.Data;
using NetWatch.Service.Bootstrap;
using NetWatch.Web;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace NetWatch.Service;

/// <summary>
/// NetWatch service entry point. Runs as the Windows service "NetWatch" or interactively from a console.
/// <para>Command line:</para>
/// <code>
///   NetWatch.Service.exe                       run (service or console)
///   NetWatch.Service.exe --simulate            also seed ~180 simulated devices (test without hardware)
///   NetWatch.Service.exe --data-dir D:\NW      use another data folder (default C:\ProgramData\NetWatch)
///   NetWatch.Service.exe --reset-admin         generate a new password for 'admin' and exit
///   NetWatch.Service.exe --set-certificate F   store the password of a .pfx (asked on the console) and exit
///   NetWatch.Service.exe --restore F.zip       restore a backup (service must be stopped) and exit
///   NetWatch.Service.exe --version             print the version and exit
///   NetWatch.Service.exe --self-test           start everything, run a few seconds, stop; exit 0 on success
///   --Https:Port=8444 (or NETWATCH_Https__Port) overrides any setting of appsettings.json
/// </code>
/// </summary>
public static class Program
{
    public const string ServiceName = "NetWatch";

    public static async Task<int> Main(string[] args)
    {
        var cli = CommandLine.Parse(args);
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
        if (cli.Version)
        {
            Console.WriteLine($"NetWatch {version}");
            return 0;
        }

        var paths = new NetWatchPaths(cli.DataDir ?? Environment.GetEnvironmentVariable("NETWATCH_DATA") ?? NetWatchPaths.DefaultRoot);
        paths.EnsureCreated();
        EnsureSettingsFile(paths);

        var fileConfig = new ConfigurationBuilder().AddJsonFile(paths.SettingsFile, optional: true).Build();
        Log.Logger = CreateLogger(paths, fileConfig);
        var startupLogger = new SerilogLoggerFactory(Log.Logger).CreateLogger("NetWatch.Startup");

        try
        {
            if (cli.SetCertificate is { } pfx)
            {
                return SetCertificate(pfx, paths);
            }

            // One running instance per data folder; the restore command checks this lock.
            using var instanceLock = InstanceLock.TryAcquire(paths);
            if (cli.Restore is { } backupFile)
            {
                if (instanceLock is null)
                {
                    Console.Error.WriteLine("NetWatch is running on this data folder. Stop the service first: Stop-Service NetWatch");
                    return 3;
                }

                try
                {
                    Console.WriteLine(Data.Backup.BackupRestore.Restore(Path.GetFullPath(backupFile), paths));
                    Console.WriteLine("Start the service again: Start-Service NetWatch");
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"Restore failed, nothing was changed: {ex.Message}");
                    return 4;
                }
            }

            if (instanceLock is null && !cli.ResetAdmin)
            {
                Log.Fatal("Another NetWatch instance is already running on data folder {DataDir}", paths.Root);
                return 5;
            }

            Log.Information("NetWatch {Version} starting (data folder {DataDir}, PID {Pid})", version, paths.Root, Environment.ProcessId);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = cli.Remaining,
                ContentRootPath = AppContext.BaseDirectory,
            });
            // Precedence (lowest → highest): built-in defaults, data-folder appsettings.json, NETWATCH_* environment
            // variables (NETWATCH_Https__Port=8444), command line (--Https:Port=8444). The settings file is added after
            // the host defaults, so environment and command line are re-added on top of it.
            builder.Configuration.AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false);
            builder.Configuration.AddEnvironmentVariables("NETWATCH_");
            builder.Configuration.AddCommandLine(cli.Remaining);
            builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
            builder.Host.UseSerilog();
            builder.Host.ConfigureHostOptions(o =>
            {
                o.ShutdownTimeout = TimeSpan.FromSeconds(20);
                o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            });

            var simulate = cli.Simulate || builder.Configuration.GetValue("Simulator:Enabled", false);
            builder.Services.AddNetWatchData(paths);
            builder.Services.AddNetWatchAlerts();
            builder.Services.AddNetWatchCollectors(simulate && !cli.ResetAdmin);
            builder.Services.AddNetWatchWeb();

            var port = builder.Configuration.GetValue("Https:Port", 8443);
            var certificate = cli.ResetAdmin ? null : CertificateManager.Load(builder.Configuration, paths, startupLogger);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
                k.Limits.MaxConcurrentConnections = 1000;
                if (certificate is not null)
                {
                    k.ListenAnyIP(port, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(certificate, https => https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
                    });
                }
            });

            builder.Services.AddSingleton(new ServerCertificateInfo(certificate?.Subject, certificate?.NotAfter.ToUniversalTime(), certificate?.Thumbprint));
            var app = builder.Build();
            await app.Services.InitialiseDatabasesAsync(startupLogger);

            if (cli.ResetAdmin)
            {
                var password = await Seeder.ResetAdminAsync(app.Services, CancellationToken.None);
                Console.WriteLine("The 'admin' account was reset. New one-time password:");
                Console.WriteLine();
                Console.WriteLine($"    {password}");
                Console.WriteLine();
                Console.WriteLine($"It was also written to {paths.InitialAdminPasswordFile}. Restart the NetWatch service if it is running.");
                return 0;
            }

            await Seeder.SeedAsync(app.Services, simulate, startupLogger, CancellationToken.None);

            app.UseNetWatchWeb();
            if (cli.SelfTest)
            {
                // Full start-up (databases, seeding, every hosted service, Kestrel), a short run, then a clean stop.
                // Used by the automated start-up test and after installation to prove the build can boot.
                await app.StartAsync();
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("SelfTest:Seconds", 5), 1, 300)));
                await app.StopAsync();
                Log.Information("Self-test passed");
                return 0;
            }

            Log.Information("Web console listening on https://{Host}:{Port}/", Environment.MachineName, port);
            await app.RunAsync();
            Log.Information("NetWatch stopped");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Fatal(ex, "NetWatch terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static Serilog.ILogger CreateLogger(NetWatchPaths paths, IConfiguration config)
    {
        var level = Enum.TryParse<LogEventLevel>(config["Logging:MinimumLevel"], true, out var l) ? l : LogEventLevel.Information;
        var retained = config.GetValue("Logging:RetainedDays", 30);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "netwatch-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Clamp(retained, 1, 365),
                fileSizeLimitBytes: 100 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        if (Environment.UserInteractive)
        {
            logger.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        return logger.CreateLogger();
    }

    /// <summary>Writes the default appsettings.json into the data folder on first start (kept on upgrade).</summary>
    private static void EnsureSettingsFile(NetWatchPaths paths)
    {
        if (File.Exists(paths.SettingsFile))
        {
            return;
        }

        using var resource = typeof(Program).Assembly.GetManifestResourceStream("appsettings.default.json");
        if (resource is null)
        {
            return;
        }

        using var file = File.Create(paths.SettingsFile);
        resource.CopyTo(file);
    }

    private static int SetCertificate(string pfx, NetWatchPaths paths)
    {
        if (!File.Exists(pfx))
        {
            Console.Error.WriteLine($"File not found: {pfx}");
            return 2;
        }

        Console.Write("PFX password: ");
        var password = ReadHidden();
        CertificateManager.StorePfxPassword(Path.GetFullPath(pfx), password);
        Console.WriteLine();
        Console.WriteLine("Password stored (DPAPI, machine scope). Now set Https:CertificatePath in");
        Console.WriteLine($"  {paths.SettingsFile}");
        Console.WriteLine($"to \"{Path.GetFullPath(pfx).Replace("\\", "\\\\")}\" and restart the NetWatch service.");
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return new string(chars.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }
}

/// <summary>Parsed command line.</summary>
internal sealed record CommandLine(bool Simulate, bool ResetAdmin, bool Version, string? DataDir, string? SetCertificate, string[] Remaining, string? Restore = null, bool SelfTest = false)
{
    public static CommandLine Parse(string[] args)
    {
        bool simulate = false, reset = false, version = false, selfTest = false;
        string? dataDir = null, cert = null, restore = null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--simulate":
                    simulate = true;
                    break;
                case "--reset-admin":
                    reset = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "--console":
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--restore" when i + 1 < args.Length:
                    restore = args[++i];
                    break;
                case "--set-certificate" when i + 1 < args.Length:
                    cert = args[++i];
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        return new CommandLine(simulate, reset, version, dataDir, cert, remaining.ToArray(), restore, selfTest);
    }
}

/// <summary>A machine-wide named mutex per data folder: held while NetWatch runs.</summary>
internal static class InstanceLock
{
    /// <summary>Returns the held mutex, or null when another process holds it (or it cannot be opened).</summary>
    public static Mutex? TryAcquire(NetWatchPaths paths)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        try
        {
            var mutex = new Mutex(initiallyOwned: false, $@"Global\NetWatch-{hash}");
            try
            {
                if (mutex.WaitOne(0))
                {
                    return mutex;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed: the lock is ours now.
                return mutex;
            }

            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Created by the service account with a DACL we may not open: it is running.
            return null;
        }
    }
}
