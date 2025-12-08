using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Services;

namespace ScanWithWeb;

/// <summary>
/// Application entry point
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        // Setup dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();

        // Run application
        var mainForm = serviceProvider.GetRequiredService<MainForm>();
        Application.Run(mainForm);
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        services.AddSingleton<IConfiguration>(configuration);

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddConsole();
            builder.AddDebug();
        });

        // Certificate Manager (auto-generates SSL certificate)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CertificateManager>>();
            var certPath = configuration.GetValue("WebSocket:CertificatePath", "scanwithweb.pfx");
            var certPassword = configuration.GetValue("WebSocket:CertificatePassword", "scanwithweb");
            var validityDays = configuration.GetValue("WebSocket:CertificateValidityDays", 365);
            var autoInstall = configuration.GetValue("WebSocket:AutoInstallCertificate", true);

            // Use AppData folder for certificate to ensure write permissions
            // (Program Files is read-only for non-admin users)
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScanWithWeb");
            Directory.CreateDirectory(appDataPath);

            var fullPath = Path.Combine(appDataPath, certPath!);

            return new CertificateManager(logger, fullPath!, certPassword!, "localhost", validityDays, autoInstall);
        });

        // Session Manager
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SessionManager>>();
            var tokenExpiration = configuration.GetValue("Session:TokenExpirationMinutes", 60);
            var maxSessions = configuration.GetValue("Session:MaxConcurrentSessions", 10);
            return new SessionManager(logger, tokenExpiration, maxSessions);
        });

        // Scanner Service
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ScannerService>>();
            return new ScannerService(logger);
        });

        // Dual WebSocket Service (supports both WS and WSS)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DualWebSocketService>>();
            var sessionManager = sp.GetRequiredService<SessionManager>();
            var scannerService = sp.GetRequiredService<ScannerService>();
            var certificateManager = sp.GetRequiredService<CertificateManager>();

            var wssPort = configuration.GetValue("WebSocket:WssPort", 8181);
            var wsPort = configuration.GetValue("WebSocket:WsPort", 8180);

            return new DualWebSocketService(logger, sessionManager, scannerService, certificateManager, wssPort, wsPort);
        });

        // Keep legacy WebSocketService for backwards compatibility (optional)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WebSocketService>>();
            var sessionManager = sp.GetRequiredService<SessionManager>();
            var scannerService = sp.GetRequiredService<ScannerService>();
            var certificateManager = sp.GetRequiredService<CertificateManager>();

            var port = configuration.GetValue("WebSocket:Port", 8181);
            var useSsl = configuration.GetValue("WebSocket:UseSsl", true);
            var certPath = configuration.GetValue("WebSocket:CertificatePath", "scanwithweb.pfx");
            var certPassword = configuration.GetValue("WebSocket:CertificatePassword", "scanwithweb");

            // Use AppData folder for certificate (same as CertificateManager)
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScanWithWeb");
            var fullPath = Path.Combine(appDataPath, certPath!);

            return new WebSocketService(logger, sessionManager, scannerService, port, useSsl, fullPath, certPassword);
        });

        // Main form
        services.AddSingleton<MainForm>();
    }
}
