































































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
    public class JobQueueManager
    {
        private readonly ILogger<JobQueueManager> _logger;
        private readonly SolidWorksAutomationService _automationService;
        private readonly SemaphoreSlim _jobSemaphore = new SemaphoreSlim(1, 1); // Ensure 1 job at a time

        public JobQueueManager(
            ILogger<JobQueueManager> logger,
            SolidWorksAutomationService automationService)
        {
            _logger = logger;
            _automationService = automationService;
        }

        public async Task EnqueueDealdoxJobAsync(HttpListenerContext ctx)
        {
            _logger.LogInformation("Job received from {ClientIp}. Waiting for available slot...", ctx.Request.RemoteEndPoint);
            
            bool acquired = await _jobSemaphore.WaitAsync(TimeSpan.FromSeconds(30)); // 30s timeout to get lock
            if (!acquired)
            {
                _logger.LogWarning("Server is too busy. Job rejected.");
                await HttpServerService.SendJsonAsync(ctx, 429, "{\"error\":\"Server is currently busy processing another file. Please try again later.\"}");
                return;
            }

            try
            {
                _logger.LogInformation("Processing job...");
                
                // We keep the HTTP connection alive and process synchronously because DealDox
                // expects a synchronous JSON response with the generated file paths.
                await ProcessJobCoreAsync(ctx);
            }
            finally
            {
                _jobSemaphore.Release();
                _logger.LogInformation("Job slot released.");
            }
        }

        private async Task ProcessJobCoreAsync(HttpListenerContext ctx)
        {
            string responseJson;
            int statusCode;

            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                // Parse payload
                var settings = new System.Runtime.Serialization.Json.DataContractJsonSerializerSettings
                    { UseSimpleDictionaryFormat = true };
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                    typeof(DealdoxApiPayload), settings);
                
                DealdoxApiPayload payload;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    payload = (DealdoxApiPayload)serializer.ReadObject(ms);
                }

                if (string.IsNullOrEmpty(payload.ModelFileName))
                {
                    await HttpServerService.SendJsonAsync(ctx, 400, "{\"status\":\"error\",\"error\":\"modelFileName is required\"}");
                    return;
                }

                _logger.LogInformation("Processing job with payload: {Payload}", body);

                // Delegate to automation service
                var response = _automationService.ExecuteJob(payload, body);
                
                // Build JSON using serializer to include all response fields (extracted dimensions, file paths, etc)
                var outSettings = new System.Runtime.Serialization.Json.DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
                var outSerializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(DealdoxApiResponse), outSettings);
                using (var msOut = new MemoryStream())
                {
                    outSerializer.WriteObject(msOut, response);
                    responseJson = Encoding.UTF8.GetString(msOut.ToArray());
                }
                
                statusCode = response.Status == "success" ? 200 : 500;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process job.");
                responseJson = $"{{\"status\":\"error\",\"error\":\"{ex.Message.Replace("\"", "'").Replace("\\", "\\\\")}\"}}";
                statusCode = 500;
            }

            await HttpServerService.SendJsonAsync(ctx, statusCode, responseJson);
        }
    }
}
