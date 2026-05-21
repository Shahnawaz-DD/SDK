using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SolidWorksConnectorSDK.Models
{
    // ══════════════════════════════════════════════
    // DEALDOX API PAYLOAD
    // ══════════════════════════════════════════════
    // Represents the incoming request from DealDox cloud API.
    //
    // DealDox sends business property names as Q&A key-value pairs.
    // The SDK resolves these to SolidWorks dimension names using
    // the mapping profile (.mapping.json) for the specified model.
    //
    // Example payload from DealDox:
    // {
    //   "modelFileName": "CC11001_ADJUSTABLE STRICKER.SLDPRT",
    //   "parameters": {
    //     "massKg": 0.108932871739391,
    //     "plateLength": 100,
    //     "plateWidth": 200,
    //     "thickness": 4
    //   }
    // }
    //
    // The SDK will:
    //   1. Find the model in C:\DealdoxSolidworksmodels
    //   2. Load/generate the mapping profile
    //   3. Map each property name to the corresponding SW dimension
    //   4. Apply values, rebuild, save the variant
    //   5. Upload the output file back to DealDox
    // ══════════════════════════════════════════════

    /// <summary>
    /// Incoming API request from DealDox cloud.
    /// Contains the model file name and Q&A parameter key-value pairs
    /// where keys are business property names (not SW dimension names).
    /// </summary>
    [DataContract]
    public class DealdoxApiPayload
    {
        /// <summary>
        /// File name of the model to modify (e.g., "CC11001_ADJUSTABLE STRICKER.SLDPRT").
        /// The SDK resolves this to the full path in C:\DealdoxSolidworksmodels.
        /// </summary>
        [DataMember(Name = "modelFileName")]
        public string ModelFileName { get; set; }

        /// <summary>
        /// Q&A key-value pairs from DealDox.
        /// Keys are business property names (e.g., "massKg", "plateLength", "thickness").
        /// Values are the target values for those properties.
        /// The SDK maps these to SolidWorks dimensions using the mapping profile.
        /// </summary>
        [DataMember(Name = "parameters")]
        public Dictionary<string, object> Parameters { get; set; }

        /// <summary>
        /// Optional output folder override. Defaults to C:\DealdoxSolidworksoutput.
        /// </summary>
        [DataMember(Name = "outputFolder")]
        public string OutputFolder { get; set; }

        /// <summary>
        /// Optional callback URL to send the generated model file back to DealDox.
        /// If not provided, uses the default DealDox upload endpoint.
        /// </summary>
        [DataMember(Name = "callbackUrl")]
        public string CallbackUrl { get; set; }

        /// <summary>
        /// Optional job ID from DealDox for tracking/correlation.
        /// </summary>
        [DataMember(Name = "jobId")]
        public string JobId { get; set; }

        /// <summary>
        /// Direct SW dimension → value mappings extracted from rich format parameters.
        /// These bypass the mapping profile entirely — 100% accurate.
        /// Populated automatically when DealDox sends: { "param": { "value": 250, "swDimension": "verplatelen@skBrkt" } }
        /// </summary>
        public Dictionary<string, double> DirectDimensions { get; set; }

        public DealdoxApiPayload()
        {
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            DirectDimensions = new Dictionary<string, double>();
        }
    }

    /// <summary>
    /// A single dimension extracted from a SolidWorks model.
    /// Returned in the API response so DealDox knows exactly what
    /// dimensions are available and their current values.
    /// </summary>
    [DataContract]
    public class ExtractedDimension
    {
        /// <summary>
        /// Full SolidWorks dimension name (e.g., "D1@Boss-Extrude1").
        /// Use this exact name in the "swDimension" field to modify this dimension.
        /// </summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>
        /// Current value in SI units (meters for length, radians for angles).
        /// </summary>
        [DataMember(Name = "value")]
        public double Value { get; set; }

        /// <summary>
        /// Current value converted to millimeters (for convenience).
        /// </summary>
        [DataMember(Name = "valueMM")]
        public double ValueMM { get; set; }

        /// <summary>
        /// The feature this dimension belongs to (e.g., "Boss-Extrude1", "Sketch1").
        /// </summary>
        [DataMember(Name = "feature")]
        public string Feature { get; set; }
    }

    /// <summary>
    /// Response sent back to DealDox after processing a model modification request.
    /// Includes paths for all three generated output file types (Part, Assembly, Drawing).
    /// </summary>
    [DataContract]
    public class DealdoxApiResponse
    {
        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "jobId")]
        public string JobId { get; set; }

        [DataMember(Name = "modelFileName")]
        public string ModelFileName { get; set; }

        /// <summary>
        /// Output path for the .SLDPRT (Part) variant. Null if no Part file was found.
        /// </summary>
        [DataMember(Name = "partFile")]
        public string PartFile { get; set; }

        /// <summary>
        /// Output path for the .SLDASM (Assembly) variant. Null if no Assembly file was found.
        /// </summary>
        [DataMember(Name = "assemblyFile")]
        public string AssemblyFile { get; set; }

        /// <summary>
        /// Output path for the .SLDDRW (Drawing) variant. Null if no Drawing file was found.
        /// </summary>
        [DataMember(Name = "drawingFile")]
        public string DrawingFile { get; set; }

        /// <summary>
        /// Output path for the exported .DWG file. Null if none was exported.
        /// </summary>
        [DataMember(Name = "dwgFile")]
        public string DwgFile { get; set; }

        /// <summary>
        /// Output path for the packaged .ZIP file containing all generated models.
        /// </summary>
        [DataMember(Name = "zipFile")]
        public string ZipFile { get; set; }

        /// <summary>
        /// Total number of output files generated (1-3).
        /// </summary>
        [DataMember(Name = "filesGenerated")]
        public int FilesGenerated { get; set; }

        [DataMember(Name = "dimensionsApplied")]
        public int DimensionsApplied { get; set; }

        [DataMember(Name = "dimensionsFailed")]
        public int DimensionsFailed { get; set; }

        /// <summary>
        /// All dimensions extracted from the model.
        /// DealDox can use the "name" field as "swDimension" in future requests
        /// to modify specific dimensions.
        /// </summary>
        [DataMember(Name = "extractedDimensions")]
        public List<ExtractedDimension> ExtractedDimensions { get; set; }

        /// <summary>
        /// Properties that were received but could not be mapped to SW dimensions.
        /// These may be computed properties (like massKg) that are read-only.
        /// </summary>
        [DataMember(Name = "propertiesSkipped")]
        public List<string> PropertiesSkipped { get; set; }

        [DataMember(Name = "uploadedToDealdox")]
        public bool UploadedToDealdox { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }

        public DealdoxApiResponse()
        {
            ExtractedDimensions = new List<ExtractedDimension>();
            PropertiesSkipped = new List<string>();
            Timestamp = DateTime.UtcNow.ToString("o");
        }
    }
}
