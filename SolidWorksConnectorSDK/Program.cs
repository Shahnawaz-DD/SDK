using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using SolidWorksConnectorSDK.Models;
using Microsoft.Extensions.Configuration;
using SolidWorksConnectorSDK.Services;

namespace SolidWorksConnectorSDK
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File("logs/solidworks-sdk-.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            try
            {
                Log.Information("Starting SolidWorks Connector SDK...");

                var host = CreateHostBuilder(args).Build();
                host.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application start-up failed");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog() // Use Serilog for standard logging
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Bind settings
                    var appSettings = new AppSettings();
                    hostContext.Configuration.Bind(appSettings);
                    services.AddSingleton(appSettings);

                    // Register Core Services
                    services.AddSingleton<ComLifetimeManager>();
                    services.AddSingleton<FeatureTraversalService>();
                    services.AddSingleton<SolidWorksAutomationService>();
                    
                    // Register Background Job Queue
                    services.AddSingleton<JobQueueManager>();

                    // Register HTTP Server as a Hosted Service
                    services.AddHostedService<HttpServerService>();
                });
    }
}
