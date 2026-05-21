using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksConnectorSDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SolidWorksConnectorSDK.Services
{
    public class SolidWorksAutomationService
    {
        private readonly ILogger<SolidWorksAutomationService> _logger;
        private readonly ComLifetimeManager _comManager;
        private readonly FeatureTraversalService _featureService;
        private readonly AppSettings _settings;

        public SolidWorksAutomationService(
            ILogger<SolidWorksAutomationService> logger,
            ComLifetimeManager comManager,
            FeatureTraversalService featureService,
            AppSettings settings)
        {
            _logger = logger;
            _comManager = comManager;
            _featureService = featureService;
            _settings = settings;
        }

        public DealdoxApiResponse ExecuteJob(DealdoxApiPayload payload, string rawJson)
        {
            var response = new DealdoxApiResponse 
            { 
                Timestamp = DateTime.UtcNow.ToString("O"),
                JobId = payload.JobId ?? Guid.NewGuid().ToString("N").Substring(0, 12),
                ModelFileName = payload.ModelFileName
            };
            
            try
            {
                ISldWorks swApp = _comManager.GetSolidWorksApp();

                // ── Resolve source files (flat layout in InputDirectory) ──
                string baseName = Path.GetFileNameWithoutExtension(payload.ModelFileName); // e.g. "SINGLE GIRDER"
                string inputDir = _settings.SolidWorks.InputDirectory;

                // Find ALL files in the input directory whose name (without extension) matches the base name
                string[] matchingFiles = Directory.GetFiles(inputDir, baseName + ".*");

                if (matchingFiles.Length == 0)
                {
                    _logger.LogError("No source files found matching '{BaseName}.*' in {InputDirectory}. Make sure the DealDox payload modelFileName matches files in {InputDirectory}", baseName, inputDir, inputDir);
                    response.Status = "error";
                    response.Error = $"No source files found matching '{baseName}.*' in {inputDir}";
                    return response;
                }

                _logger.LogInformation("Found {Count} source file(s) for '{BaseName}': {Files}",
                    matchingFiles.Length, baseName, string.Join(", ", Array.ConvertAll(matchingFiles, Path.GetFileName)));

                // ── Isolate the Job (Copy matching files to output folder) ──
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string jobFolderName = $"{baseName}_{timestamp}";
                string outputFolder = Path.Combine(_settings.SolidWorks.OutputDirectory, jobFolderName);

                Directory.CreateDirectory(outputFolder);
                foreach (string srcFile in matchingFiles)
                {
                    string destFile = Path.Combine(outputFolder, Path.GetFileName(srcFile));
                    File.Copy(srcFile, destFile, true);
                }
                _logger.LogInformation("Job isolated to folder: {OutputFolder}", outputFolder);

                // Need to clean parameters to double for mapping
                var cleanParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (payload.Parameters != null)
                {
                    foreach (var kvp in payload.Parameters)
                    {
                        if (kvp.Value is decimal || kvp.Value is double || kvp.Value is int || kvp.Value is long || kvp.Value is float)
                            cleanParams[kvp.Key] = kvp.Value;
                        else if (double.TryParse(kvp.Value?.ToString() ?? "", out double dv))
                            cleanParams[kvp.Key] = dv;
                    }
                }

                // ── Step 1: Process ALL .SLDPRT files ──
                string[] partFiles = Directory.GetFiles(outputFolder, "*.SLDPRT");
                foreach (string partFile in partFiles)
                {
                    _logger.LogInformation("Processing part: {Part}", Path.GetFileName(partFile));
                    
                    int oe = 0, ow = 0;
                    ModelDoc2 partDoc = swApp.OpenDoc6(partFile, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow);
                    
                    if (partDoc != null)
                    {
                        // Load or generate mapping profile for this specific part
                        string profilePath = MappingEngine.FindProfileForModel(partFile);
                        ModelProfile profile = null;
                        if (profilePath != null)
                        {
                            profile = MappingEngine.LoadProfile(profilePath);
                        }
                        else
                        {
                            // Generate from the original master so we don't pollute output with mappings if they don't want it, 
                            // but actually generating it in output is fine too.
                            profile = MappingEngine.GenerateProfileFromModel(partDoc, Path.GetFileNameWithoutExtension(partFile), partFile);
                            profilePath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(partFile) + ".mapping.json");
                            MappingEngine.SaveProfile(profile, profilePath);
                        }

                        // Apply MAPPED parameters (fuzzy matching)
                        if (profile != null && cleanParams.Count > 0)
                        {
                            var mr = MappingEngine.ApplyParameters(partDoc, profile, cleanParams);
                            response.DimensionsApplied += mr.DimensionsChanged;
                            response.DimensionsFailed += mr.DimensionsFailed;
                        }

                        // Rebuild and save Part
                        ParameterModifier.Rebuild(partDoc);
                        int se = 0, sw2 = 0;
                        partDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw2);
                        response.FilesGenerated++;

                        // Extract dimensions (optional, could be noisy if many parts, but keeping it for DealDox)
                        ExtractDimensionsToResponse(partDoc, response);

                        swApp.CloseDoc(Path.GetFileName(partFile));
                        _comManager.ReleaseComObject(partDoc);
                    }
                }

                // ── Step 2: Process ALL .SLDASM files ──
                string[] asmFiles = Directory.GetFiles(outputFolder, "*.SLDASM");
                foreach (string asmFile in asmFiles)
                {
                    _logger.LogInformation("Rebuilding assembly: {Asm}", Path.GetFileName(asmFile));
                    
                    int oe = 0, ow = 0;
                    ModelDoc2 asmDoc = swApp.OpenDoc6(asmFile, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow);
                    
                    if (asmDoc != null)
                    {
                        // Assembly automatically loads the updated parts from its local folder!
                        ParameterModifier.Rebuild(asmDoc);
                        int se = 0, sw2 = 0;
                        asmDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw2);
                        response.FilesGenerated++;
                        
                        // We record the first assembly we find as the "AssemblyFile" for the API
                        if (string.IsNullOrEmpty(response.AssemblyFile))
                            response.AssemblyFile = asmFile;

                        swApp.CloseDoc(Path.GetFileName(asmFile));
                        _comManager.ReleaseComObject(asmDoc);
                    }
                }

                // ── Step 3: Process ALL .SLDDRW files and Export to DWG ──
                string[] drwFiles = Directory.GetFiles(outputFolder, "*.SLDDRW");
                foreach (string drwFile in drwFiles)
                {
                    _logger.LogInformation("Rebuilding drawing and exporting DWG: {Drw}", Path.GetFileName(drwFile));
                    
                    int oe = 0, ow = 0;
                    ModelDoc2 drwDoc = swApp.OpenDoc6(drwFile, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow);
                    
                    if (drwDoc != null)
                    {
                        // ── Clean up drawing views: hide tangent edges, sketches, set display mode ──
                        CleanupDrawingViews(drwDoc);

                        ParameterModifier.Rebuild(drwDoc);
                        int se = 0, sw2 = 0;
                        drwDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw2);
                        response.FilesGenerated++;

                        // Export to DWG
                        string dwgPath = Path.ChangeExtension(drwFile, ".dwg");
                        drwDoc.Extension.SaveAs(dwgPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref se, ref sw2);
                        
                        if (File.Exists(dwgPath))
                        {
                            response.DwgFile = dwgPath;
                            response.FilesGenerated++;
                            _logger.LogInformation("✅ DWG Exported: {Dwg}", Path.GetFileName(dwgPath));
                        }

                        swApp.CloseDoc(Path.GetFileName(drwFile));
                        _comManager.ReleaseComObject(drwDoc);
                    }
                }

                // Ensure ALL documents (including referenced parts) are closed to release file locks
                swApp.CloseAllDocuments(true);
                
                // ── Step 4: Zip the Output Folder ──
                string zipPath = Path.Combine(_settings.SolidWorks.OutputDirectory, $"{jobFolderName}.zip");
                ZipFile.CreateFromDirectory(outputFolder, zipPath, CompressionLevel.Fastest, false);
                
                if (File.Exists(zipPath))
                {
                    response.ZipFile = zipPath;
                    _logger.LogInformation("✅ Job packaged into ZIP: {Zip}", Path.GetFileName(zipPath));
                }

                response.Status = "success";
                _logger.LogInformation("JOB COMPLETE — {Count} file(s) generated. Dims applied: {App}, Failed: {Fail}",
                    response.FilesGenerated, response.DimensionsApplied, response.DimensionsFailed);

                // Upload to DealDox (simulated)
                try
                {
                    var apiClient = new ApiClient();
                    bool uploaded = Task.Run(async () =>
                        await apiClient.UploadVariantToDealdoxAsync(
                            response, payload.CallbackUrl)
                    ).GetAwaiter().GetResult();
                    response.UploadedToDealdox = uploaded;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to upload variant to DealDox"); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job Execution Failed");
                response.Status = "error";
                response.Error = ex.Message;
            }

            return response;
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        private void ExtractDimensionsToResponse(ModelDoc2 doc, DealdoxApiResponse response)
        {
            if (doc == null) return;
            var seen = new HashSet<string>();

            try
            {
                Feature feat = (Feature)doc.FirstFeature();
                while (feat != null)
                {
                    try
                    {
                        string featName = feat.Name ?? "(unnamed)";
                        DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                        while (dispDim != null)
                        {
                            try
                            {
                                Dimension dim = (Dimension)dispDim.GetDimension2(0);
                                if (dim != null)
                                {
                                    string shortName = dim.Name + "@" + featName;
                                    if (!string.IsNullOrEmpty(shortName) && seen.Add(shortName))
                                    {
                                        double valSI = dim.SystemValue;
                                        response.ExtractedDimensions.Add(new ExtractedDimension
                                        {
                                            Name = shortName,
                                            Value = valSI,
                                            ValueMM = Math.Round(valSI * 1000, 4),
                                            Feature = featName
                                        });
                                    }
                                    _comManager.ReleaseComObject(dim);
                                }
                            }
                            catch (Exception ex) { _logger.LogDebug(ex, "Error extracting dimension from display dimension"); }

                            DisplayDimension nextDispDim = null;
                            try { nextDispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim); } catch (Exception ex) { _logger.LogDebug(ex, "Error getting next display dimension"); }
                            _comManager.ReleaseComObject(dispDim);
                            dispDim = nextDispDim;
                        }
                    }
                        catch (Exception ex) { _logger.LogDebug(ex, "Error iterating display dimensions for feature"); }

                    Feature nextFeat = null;
                    try { nextFeat = (Feature)feat.GetNextFeature(); } catch (Exception ex) { _logger.LogDebug(ex, "Error getting next feature"); }
                    _comManager.ReleaseComObject(feat);
                    feat = nextFeat;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Error during dimension extraction traversal"); }
        }
        /// <summary>
        /// Cleans up all drawing views to remove tangent edges, hide sketches,
        /// and set display mode to Hidden Lines Removed. This prevents unwanted
        /// circles and construction geometry from appearing in output drawings.
        /// </summary>
        private void CleanupDrawingViews(ModelDoc2 drwDoc)
        {
            try
            {
                DrawingDoc drawing = (DrawingDoc)drwDoc;
                if (drawing == null) return;

                // Get all sheet names and iterate through each sheet
                string[] sheetNames = (string[])drawing.GetSheetNames();
                if (sheetNames == null) return;

                foreach (string sheetName in sheetNames)
                {
                    drawing.ActivateSheet(sheetName);
                    var sheet = (Sheet)drawing.GetCurrentSheet();
                    if (sheet == null) continue;

                    // Get all views on this sheet
                    object[] viewsObj = (object[])sheet.GetViews();
                    if (viewsObj == null) continue;

                    foreach (object vObj in viewsObj)
                    {
                        View view = vObj as View;
                        if (view == null) continue;

                        try
                        {
                            // Remove tangent edges (the main cause of extra circles)
                            // swEdgesTangentEdgeDisplayRemoved = hide tangent edges completely
                            view.SetDisplayTangentEdges2(
                                (int)swEdgesTangentEdgeDisplay_e.swEdgesTangentEdgeDisplayRemoved);

                            // Set display mode to Hidden Lines Removed
                            // This prevents back-side edges from projecting through
                            view.SetDisplayMode3(
                                false,  // UseParent = false (override per view)
                                (int)swViewDisplayMode_e.swViewDisplayMode_HiddenLinesRemoved,
                                false,  // Facetted
                                false   // Edges
                            );

                            _logger.LogDebug("Cleaned view '{ViewName}' on sheet '{SheetName}': tangent edges removed, HLR mode",
                                view.Name, sheetName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error cleaning view '{ViewName}' on sheet '{SheetName}'",
                                view.Name ?? "unknown", sheetName);
                        }
                    }
                }

                // Hide all sketches in the drawing
                drwDoc.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swViewDisplayHideAllTypes, true);

                _logger.LogInformation("✅ Drawing views cleaned: tangent edges removed, sketches hidden, HLR display mode set");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fully clean drawing views — output may contain extra geometry");
            }
        }
    }
}
