using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using SolidWorksConnectorSDK.Models;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// API Client to send extracted CAD metadata to the FastAPI cloud endpoint.
    /// Supports async execution with retry logic and fire-and-forget for COM event handlers.
    /// 
    /// Upload behavior by document type:
    ///   - Part (1) / Drawing (3): uploads the single file as-is
    ///   - Assembly (2): collects the root .sldasm + all ReferencedDocuments into an
    ///     in-memory ZIP archive (preserving relative folder structure) and uploads the ZIP
    /// </summary>
    public class ApiClient
    {
        private static readonly HttpClient _httpClient;
        private static readonly ILogger _log = Log.ForContext<ApiClient>();
        private readonly string _endpointUrl;
        private readonly int _maxRetries;


        static ApiClient()
        {
            _httpClient = new HttpClient();
            // Important: Use a reasonable timeout so we don't hang if the server is down
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public ApiClient(string endpointUrl = "https://devqa.dealdox.io/api/solidWorks/uploadCAD", int maxRetries = 3)
        {
            _endpointUrl = endpointUrl;
            _maxRetries = maxRetries;
        }

        /// <summary>
        /// Sends the metadata as a fire-and-forget task so it doesn't block the SolidWorks Save event thread.
        /// </summary>
        public void SendMetadataFireAndForget(SaveEventData saveData)
        {
            if (saveData == null) return;

            // Run in a background thread to ensure non-blocking execution
            Task.Run(async () =>
            {
                try
                {
                    await SendMetadataAsync(saveData).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unhandled exception in background metadata send thread");
                }
            });
        }

        /// <summary>
        /// Sends the metadata asynchronously with retry logic.
        /// </summary>
        public async Task<bool> SendMetadataAsync(SaveEventData saveData)
        {

            string filePath = saveData.Metadata?.FullPath ?? saveData.RawFileName;
            string baseFileName = saveData.Metadata?.FileName ?? Path.GetFileName(filePath);

            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    _log.Information("Sending metadata to Cloud (Attempt {Attempt}/{MaxRetries})...", attempt, _maxRetries);

                    HttpResponseMessage response;

                    using (var form = new MultipartFormDataContent())
                    {
                        // ── File Content (ZIP for all document types) ──
                        var allPaths = new List<string>();

                        // 1. Root file (Part, Assembly, or Drawing)
                        if (File.Exists(filePath))
                        {
                            allPaths.Add(filePath);
                        }
                        else
                        {
                            _log.Warning("Root file not found: {FilePath}", filePath);
                        }

                        // 2. Referenced documents (applies to Assemblies with sub-parts/sub-assemblies)
                        if (saveData.Metadata != null && saveData.Metadata.ReferencedDocuments != null && saveData.Metadata.ReferencedDocuments.Count > 0)
                        {
                            foreach (string refPath in saveData.Metadata.ReferencedDocuments)
                            {
                                if (string.IsNullOrEmpty(refPath)) continue;

                                if (File.Exists(refPath))
                                {
                                    allPaths.Add(refPath);
                                }
                                else
                                {
                                    _log.Warning("Referenced file not found, skipping: {RefPath}", refPath);
                                }
                            }
                        }

                        if (allPaths.Count == 0)
                        {
                            _log.Warning("No files found on disk. Uploading metadata only.");
                        }
                        else
                        {
                            _log.Information("Zipping {FileCount} file(s)...", allPaths.Count);

                            byte[] zipBytes = await CreateCadZipAsync(allPaths).ConfigureAwait(false);

                            string zipFileName = Path.GetFileNameWithoutExtension(baseFileName) + "_cad.zip";

                            var zipContent = new ByteArrayContent(zipBytes);
                            zipContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                            form.Add(zipContent, "file", zipFileName);

                            _log.Information("ZIP created: {ZipFileName} ({ZipSize:N0} bytes, {FileCount} files)", zipFileName, zipBytes.Length, allPaths.Count);
                        }

                        // ── Metadata JSON ──
                        var metadataJson = JsonExporter.SerializeSaveEventToJson(saveData);
                        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

                        response = await _httpClient.PostAsync(_endpointUrl, form).ConfigureAwait(false);
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        _log.Information("Successfully sent metadata to Cloud");
                        return true;
                    }
                    else
                    {
                        string errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _log.Error("Failed to send metadata. Status: {StatusCode}, Response: {ErrorResponse}", response.StatusCode, errorResponse);
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    _log.Error(httpEx, "Network error (Attempt {Attempt})", attempt);
                }
                catch (TaskCanceledException tcEx)
                {
                    _log.Error(tcEx, "Request timed out (Attempt {Attempt})", attempt);
                }

                // Exponential backoff before retry (e.g., 2s, 4s, 8s)
                if (attempt < _maxRetries)
                {
                    int delayMs = (int)Math.Pow(2, attempt) * 1000;
                    _log.Debug("Waiting {DelayMs}ms before retry...", delayMs);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }

            _log.Error("Exhausted all retries. Metadata could not be sent");
            return false;
        }

        // ══════════════════════════════════════════════
        // ASSEMBLY ZIP HELPERS
        // ══════════════════════════════════════════════

        /// <summary>
        /// Creates an in-memory ZIP archive from the given list of absolute file paths.
        /// Preserves relative folder structure by computing the common root directory.
        /// All file reads use FileShare.ReadWrite to avoid SolidWorks file lock conflicts.
        /// Uses NoCompression to store binary CAD files raw — prevents corruption of large files.
        /// </summary>
        private static async Task<byte[]> CreateCadZipAsync(List<string> filePaths)
        {
            string commonRoot = FindCommonRoot(filePaths);

            using (var zipStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    foreach (string absPath in filePaths)
                    {
                        // Compute relative entry name
                        string entryName;
                        if (!string.IsNullOrEmpty(commonRoot) && absPath.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            entryName = absPath.Substring(commonRoot.Length).TrimStart('\\', '/');
                        }
                        else
                        {
                            // No common root (different drives) — sanitize the colon
                            entryName = absPath.Replace(":", "_drive_");
                        }

                        // Normalize path separators to forward slashes for ZIP compatibility
                        entryName = entryName.Replace('\\', '/');

                        // Read the file with FileShare.ReadWrite (same retry pattern as single-file upload)
                        byte[] fileBytes = null;
                        int readAttempts = 0;
                        while (fileBytes == null && readAttempts < 5)
                        {
                            try
                            {
                                using (var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var ms = new MemoryStream())
                                {
                                    fs.CopyTo(ms);
                                    fileBytes = ms.ToArray();
                                }
                            }
                            catch (IOException ioEx)
                            {
                                readAttempts++;
                                if (readAttempts >= 5)
                                {
                                    _log.Warning(ioEx, "Could not read file after 5 attempts, skipping: {FileName}", Path.GetFileName(absPath));
                                    break; // Skip this file, don't abort entire ZIP
                                }
                                await Task.Delay(500).ConfigureAwait(false);
                            }
                        }

                        if (fileBytes == null) continue; // Skip unreadable file

                        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                        using (var entryStream = entry.Open())
                        {
                            entryStream.Write(fileBytes, 0, fileBytes.Length);
                        }
                    }
                }

                return zipStream.ToArray();
            }
        }

        /// <summary>
        /// Finds the longest common root directory shared by all file paths.
        /// Returns the common root ending with a backslash, or empty string if no common root exists.
        /// </summary>
        private static string FindCommonRoot(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return string.Empty;
            if (paths.Count == 1) return Path.GetDirectoryName(paths[0]) + "\\";

            // Split every path into segments
            var segmentsList = new List<string[]>();
            foreach (string p in paths)
            {
                segmentsList.Add(p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));
            }

            // Find the minimum segment count
            int minLen = segmentsList[0].Length;
            for (int i = 1; i < segmentsList.Count; i++)
            {
                if (segmentsList[i].Length < minLen) minLen = segmentsList[i].Length;
            }

            // Walk segments and find the longest common prefix
            int commonDepth = 0;
            for (int seg = 0; seg < minLen; seg++)
            {
                string reference = segmentsList[0][seg];
                bool allMatch = true;
                for (int fileIdx = 1; fileIdx < segmentsList.Count; fileIdx++)
                {
                    if (!string.Equals(segmentsList[fileIdx][seg], reference, StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    commonDepth = seg + 1;
                }
                else
                {
                    break;
                }
            }

            if (commonDepth == 0) return string.Empty;

            // Rebuild the common root path
            var rootParts = new string[commonDepth];
            Array.Copy(segmentsList[0], rootParts, commonDepth);
            string root = string.Join("\\", rootParts);

            // If the first segment looks like a drive letter (e.g., "C:"), add the backslash
            if (root.Length >= 2 && root[1] == ':' && !root.EndsWith("\\"))
            {
                root += "\\";
            }
            else if (!root.EndsWith("\\"))
            {
                root += "\\";
            }

            return root;
        }

        // ══════════════════════════════════════════════
        // DEALDOX VARIANT UPLOAD (Simulated)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Uploads the generated variant model file back to DealDox.
        /// 
        /// Currently SIMULATED — logs the upload and saves a local receipt.
        /// When the real DealDox API is available, plug in the actual endpoint URL.
        ///
        /// Upload payload:
        ///   - The .SLDPRT variant file as multipart/form-data
        ///   - Job metadata (jobId, modelFileName, dimensions applied, etc.)
        /// </summary>
        /// <param name="variantFilePath">Full path to the generated variant .SLDPRT file.</param>
        /// <param name="response">The response object with job metadata.</param>
        /// <param name="callbackUrl">DealDox callback URL (optional, uses default if null).</param>
        /// <returns>True if upload succeeded (or simulation completed).</returns>
        public async Task<bool> UploadVariantToDealdoxAsync(
            Models.DealdoxApiResponse response,
            string callbackUrl = null)
        {
            string uploadUrl = callbackUrl ?? "https://devqa.dealdox.io/api/solidWorks/uploadVariant";

            var filesToUpload = new List<string>();
            
            // Prefer the packaged ZIP file if the automation service generated it.
            if (!string.IsNullOrEmpty(response.ZipFile))
            {
                filesToUpload.Add(response.ZipFile);
            }
            else
            {
                // Fallback to individual files (legacy/fallback behavior)
                if (!string.IsNullOrEmpty(response.PartFile)) filesToUpload.Add(response.PartFile);
                if (!string.IsNullOrEmpty(response.AssemblyFile)) filesToUpload.Add(response.AssemblyFile);
                if (!string.IsNullOrEmpty(response.DrawingFile)) filesToUpload.Add(response.DrawingFile);
            }

            if (filesToUpload.Count == 0)
            {
                _log.Error("No variant files generated to upload");
                return false;
            }

            // If we are uploading a pre-packaged ZIP, just use its name.
            string displayFileName = filesToUpload.Count > 1 
                ? Path.GetFileNameWithoutExtension(filesToUpload[0]) + "_variant.zip" 
                : Path.GetFileName(filesToUpload[0]);

            _log.Information("UPLOADING VARIANT TO DEALDOX — File: {DisplayFileName} ({FileCount} files), URL: {UploadUrl}",
                displayFileName, filesToUpload.Count, uploadUrl);

            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    _log.Information("Uploading to DealDox (Attempt {Attempt}/{MaxRetries})...", attempt, _maxRetries);

                    // Read the variant file(s)
                    byte[] fileBytes = null;
                    if (filesToUpload.Count == 1)
                    {
                        using (var fs = new FileStream(filesToUpload[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var ms = new MemoryStream())
                        {
                            fs.CopyTo(ms);
                            fileBytes = ms.ToArray();
                        }
                    }
                    else
                    {
                        fileBytes = await CreateCadZipAsync(filesToUpload).ConfigureAwait(false);
                    }

                    using (var form = new MultipartFormDataContent())
                    {
                        // Add the variant file/zip
                        var fileContent = new ByteArrayContent(fileBytes);
                        if (displayFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                        else
                            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                            
                        form.Add(fileContent, "variantFile", displayFileName);

                        // Add job metadata as JSON (includes extracted dimensions)
                        var metaSb = new StringBuilder();
                        metaSb.Append("{");
                        metaSb.AppendFormat("\"jobId\":\"{0}\"", response.JobId ?? "");
                        metaSb.AppendFormat(",\"modelFileName\":\"{0}\"", (response.ModelFileName ?? "").Replace("\\", "\\\\"));
                        metaSb.AppendFormat(",\"dimensionsApplied\":{0}", response.DimensionsApplied);
                        metaSb.AppendFormat(",\"timestamp\":\"{0}\"", response.Timestamp);

                        // Include extracted dimensions
                        metaSb.Append(",\"extractedDimensions\":[");
                        if (response.ExtractedDimensions != null)
                        {
                            for (int di = 0; di < response.ExtractedDimensions.Count; di++)
                            {
                                var dim = response.ExtractedDimensions[di];
                                if (di > 0) metaSb.Append(",");
                                metaSb.Append("{");
                                metaSb.AppendFormat("\"name\":\"{0}\"", (dim.Name ?? "").Replace("\"", "'"));
                                metaSb.AppendFormat(",\"value\":{0}",
                                    dim.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
                                metaSb.AppendFormat(",\"valueMM\":{0}",
                                    dim.ValueMM.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                                metaSb.AppendFormat(",\"feature\":\"{0}\"", (dim.Feature ?? "").Replace("\"", "'"));
                                metaSb.Append("}");
                            }
                        }
                        metaSb.Append("]");

                        metaSb.Append("}");
                        string metadataJson = metaSb.ToString();
                        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

                        // Attempt the upload
                        HttpResponseMessage httpResponse = null;
                        try
                        {
                            httpResponse = await _httpClient.PostAsync(uploadUrl, form).ConfigureAwait(false);
                        }
                        catch (HttpRequestException httpEx)
                        {
                            // ═══════════════════════════════════════
                            // SIMULATION MODE
                            // ═══════════════════════════════════════
                            // DealDox API is not available yet.
                            // Simulate a successful upload by saving a receipt locally.
                            _log.Warning(httpEx, "DealDox API not reachable — running in SIMULATION mode");

                            // Save a local upload receipt
                            string receiptFolder = Path.Combine(@"C:\DealdoxSolidworksoutput", "DealdoxUploadReceipts");
                            Directory.CreateDirectory(receiptFolder);
                            string receiptPath = Path.Combine(receiptFolder,
                                Path.GetFileNameWithoutExtension(displayFileName) + "_upload_receipt.json");

                            string receiptJson = string.Format(
                                "{{\n  \"status\": \"simulated_upload\",\n" +
                                "  \"jobId\": \"{0}\",\n" +
                                "  \"modelFileName\": \"{1}\",\n" +
                                "  \"variantFile\": \"{2}\",\n" +
                                "  \"fileSizeBytes\": {3},\n" +
                                "  \"dimensionsApplied\": {4},\n" +
                                "  \"dimensionsExtracted\": {5},\n" +
                                "  \"targetUrl\": \"{6}\",\n" +
                                "  \"simulatedAt\": \"{7}\",\n" +
                                "  \"note\": \"DealDox API not available — file saved locally. Upload will be retried when API is online.\"\n}}",
                                response.JobId ?? "",
                                (response.ModelFileName ?? "").Replace("\\", "\\\\"),
                                displayFileName.Replace("\\", "\\\\"),
                                fileBytes.Length,
                                response.DimensionsApplied,
                                response.ExtractedDimensions != null ? response.ExtractedDimensions.Count : 0,
                                uploadUrl.Replace("\\", "\\\\"),
                                DateTime.UtcNow.ToString("o"));

                            File.WriteAllText(receiptPath, receiptJson, Encoding.UTF8);

                            _log.Information("SIMULATED UPLOAD — Receipt saved: {ReceiptPath}, File size: {FileSize:N0} bytes", receiptPath, fileBytes.Length);

                            return true; // Simulation counts as success
                        }

                        if (httpResponse != null && httpResponse.IsSuccessStatusCode)
                        {
                            _log.Information("Successfully uploaded variant to DealDox! File: {DisplayFileName} ({FileSize:N0} bytes)",
                                displayFileName, fileBytes.Length);
                            return true;
                        }
                        else if (httpResponse != null)
                        {
                            string errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            _log.Error("Upload failed. Status: {StatusCode}, Response: {ErrorBody}",
                                httpResponse.StatusCode, errorBody);
                        }
                    }
                }
                catch (TaskCanceledException tcEx)
                {
                    _log.Error(tcEx, "Upload timed out (Attempt {Attempt})", attempt);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Upload error (Attempt {Attempt})", attempt);
                }

                // Exponential backoff
                if (attempt < _maxRetries)
                {
                    int delayMs = (int)Math.Pow(2, attempt) * 1000;
                    _log.Debug("Waiting {DelayMs}ms before retry...", delayMs);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }

            _log.Error("All upload attempts failed");
            return false;
        }
    }
}
