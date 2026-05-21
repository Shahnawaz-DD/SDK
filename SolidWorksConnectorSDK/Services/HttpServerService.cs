using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolidWorksConnectorSDK.Models;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksConnectorSDK.Services
{
    public class HttpServerService : BackgroundService
    {
        private readonly ILogger<HttpServerService> _logger;
        private readonly AppSettings _settings;
        private readonly JobQueueManager _queueManager;
        private HttpListener _listener;

        public HttpServerService(
            ILogger<HttpServerService> logger, 
            AppSettings settings, 
            JobQueueManager queueManager)
        {
            _logger = logger;
            _settings = settings;
            _queueManager = queueManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_settings.SolidWorks.InputDirectory);
            Directory.CreateDirectory(_settings.SolidWorks.OutputDirectory);

            string prefix = $"http://localhost:{_settings.SolidWorks.ServerPort}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);

            try
            {
                _listener.Start();
                _logger.LogInformation("✅ HTTP Server listening on {Prefix}", prefix);
                _logger.LogInformation("Endpoints: POST /api/dealdox, GET /api/dimensions, GET /api/health");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to start HTTP listener. Run as admin?");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // GetContextAsync can't easily accept a cancellation token natively in older .NET,
                    // but we can register a callback to stop the listener.
                    using (stoppingToken.Register(() => _listener.Stop()))
                    {
                        if (!_listener.IsListening) break;
                        
                        var context = await _listener.GetContextAsync();
                        _ = Task.Run(() => HandleRequestAsync(context), stoppingToken);
                    }
                }
                catch (HttpListenerException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Ignored
                }
                catch (Exception ex)
                {
                    if (!stoppingToken.IsCancellationRequested)
                        _logger.LogError(ex, "Error accepting HTTP request");
                }
            }

            _logger.LogInformation("HTTP Server shut down cleanly.");
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant().TrimEnd('/');
                string method = ctx.Request.HttpMethod.ToUpperInvariant();

                // CORS
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Api-Key");

                if (method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                    return;
                }

                if (path == "/api/health" && method == "GET")
                {
                    await SendJsonAsync(ctx, 200, "{\"status\":\"ok\"}");
                    return;
                }

                if (path == "/api/dealdox" && method == "POST")
                {
                    await _queueManager.EnqueueDealdoxJobAsync(ctx);
                    return;
                }

                await SendJsonAsync(ctx, 404, "{\"error\":\"Not found\"}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing request");
                await SendJsonAsync(ctx, 500, "{\"error\":\"Internal server error\"}");
            }
        }

        public static async Task SendJsonAsync(HttpListenerContext ctx, int code, string json)
        {
            byte[] buf = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = code;
            await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            ctx.Response.Close();
        }
    }
}
