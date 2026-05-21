using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SolidWorksConnectorSDK.Models
{
    // ══════════════════════════════════════════════
    // MODEL PROFILE
    // ══════════════════════════════════════════════
    // The complete mapping file for one SolidWorks model.
    // Created ONCE per model, reused with different values every time.
    //
    // Example:
    //   "CC11001_ADJUSTABLE STRICKER.mapping.json"
    //   sits next to "CC11001_ADJUSTABLE STRICKER.SLDPRT"
    //
    // Dealdox sends:  { "PLATE_LENGTH": 450, "THICKNESS": 25 }
    // Profile maps:   PLATE_LENGTH → D1@Sketch1 (scale 0.001)
    // Connector sets:  D1@Sketch1 = 0.45 m
    // ══════════════════════════════════════════════

    /// <summary>
    /// Root object of a .mapping.json file.
    /// Defines the mapping between business parameters and SolidWorks dimensions
    /// for a specific model.
    /// </summary>
    [DataContract]
    public class ModelProfile
    {
        /// <summary>
        /// Human-readable model name (e.g., "CC11001_ADJUSTABLE STRICKER").
        /// </summary>
        [DataMember(Name = "modelName")]
        public string ModelName { get; set; }

        /// <summary>
        /// Relative or absolute path to the master .SLDPRT/.SLDASM file.
        /// </summary>
        [DataMember(Name = "masterFilePath")]
        public string MasterFilePath { get; set; }

        /// <summary>
        /// Optional description of the model/product.
        /// </summary>
        [DataMember(Name = "description")]
        public string Description { get; set; }

        /// <summary>
        /// When this profile was created/last modified.
        /// </summary>
        [DataMember(Name = "createdAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Business parameter name → mapping definition.
        /// Keys are the business parameter names that Dealdox sends (e.g., "DRUM_LENGTH").
        /// Labels are set ONCE, values change every time.
        /// </summary>
        [DataMember(Name = "parameters")]
        public Dictionary<string, ParameterMapping> Parameters { get; set; }

        public ModelProfile()
        {
            Parameters = new Dictionary<string, ParameterMapping>(StringComparer.OrdinalIgnoreCase);
            CreatedAt = DateTime.UtcNow;
        }
    }

    // ══════════════════════════════════════════════
    // PARAMETER MAPPING
    // ══════════════════════════════════════════════

    /// <summary>
    /// Maps ONE business parameter to one or more SolidWorks actions.
    /// 
    /// Two types:
    ///   "dimension"   → sets SW dimension values (most common)
    ///   "suppression" → toggles features on/off (e.g., HasHole)
    /// 
    /// Example (dimension):
    ///   Business: "PLATE_LENGTH" = 450 mm
    ///   Maps to:  D1@Sketch1 → 450 * 0.001 = 0.45 m
    ///   Maps to:  D3@Sketch5 → 450 * 0.001 = 0.45 m (same value, two dims)
    /// 
    /// Example (suppression):
    ///   Business: "HAS_HOLE" = true/false
    ///   Maps to:  Suppress/Unsuppress "Cut-Extrude1", "Sketch2"
    /// </summary>
    [DataContract]
    public class ParameterMapping
    {
        /// <summary>
        /// Human-readable label (e.g., "Plate Length", "Drum Length").
        /// </summary>
        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        /// <summary>
        /// Unit of the business value: "mm", "m", "ton", "kg", "count", "bool".
        /// Used for display and unit conversion.
        /// </summary>
        [DataMember(Name = "unit")]
        public string Unit { get; set; }

        /// <summary>
        /// "dimension" or "suppression".
        /// </summary>
        [DataMember(Name = "type")]
        public string Type { get; set; }

        /// <summary>
        /// Whether this parameter can be changed by the user/API.
        /// </summary>
        [DataMember(Name = "editable")]
        public bool Editable { get; set; }

        /// <summary>
        /// Default value in business units (what Dealdox would normally send).
        /// </summary>
        [DataMember(Name = "defaultValue")]
        public double? DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value (for validation).
        /// </summary>
        [DataMember(Name = "minValue")]
        public double? MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value (for validation).
        /// </summary>
        [DataMember(Name = "maxValue")]
        public double? MaxValue { get; set; }

        /// <summary>
        /// For type="dimension": one business param may drive MULTIPLE SW dimensions.
        /// e.g., DRUM_LENGTH might set D4@Sketch18 AND D2@Sketch22.
        /// </summary>
        [DataMember(Name = "dimensions")]
        public List<DimensionTarget> Dimensions { get; set; }

        /// <summary>
        /// For type="suppression": which features to toggle and how.
        /// </summary>
        [DataMember(Name = "suppression")]
        public SuppressionRule Suppression { get; set; }

        public ParameterMapping()
        {
            Type = "dimension";
            Editable = true;
            Dimensions = new List<DimensionTarget>();
        }
    }

    // ══════════════════════════════════════════════
    // DIMENSION TARGET
    // ══════════════════════════════════════════════

    /// <summary>
    /// Maps a business parameter value to a specific SolidWorks dimension.
    /// 
    /// Final SW value = (BusinessValue * ScaleFactor) + Offset
    /// 
    /// Example: Business sends "PLATE_LENGTH" = 450 (mm)
    ///   ScaleFactor = 0.001 (mm → meters for SolidWorks)
    ///   Offset = 0
    ///   SW value = 450 * 0.001 + 0 = 0.45 m
    /// </summary>
    [DataContract]
    public class DimensionTarget
    {
        /// <summary>
        /// SolidWorks dimension name (e.g., "D1@Sketch1", "D4@Boss-Extrude2").
        /// This is what ModelDoc2.Parameter() accepts.
        /// </summary>
        [DataMember(Name = "swDimension")]
        public string SwDimension { get; set; }

        /// <summary>
        /// Multiply the business value by this to get SI units.
        /// Common values: 0.001 (mm→m), 1.0 (already SI), 9.81 (ton→kN).
        /// Default: 0.001 (assumes mm input, SW needs meters).
        /// </summary>
        [DataMember(Name = "scaleFactor")]
        public double ScaleFactor { get; set; }

        /// <summary>
        /// Added AFTER scaling: finalValue = (businessValue * ScaleFactor) + Offset.
        /// Default: 0.
        /// </summary>
        [DataMember(Name = "offset")]
        public double Offset { get; set; }

        public DimensionTarget()
        {
            ScaleFactor = 0.001;  // mm → meters by default
            Offset = 0.0;
        }
    }

    // ══════════════════════════════════════════════
    // SUPPRESSION RULE
    // ══════════════════════════════════════════════

    /// <summary>
    /// Defines which features to suppress/unsuppress based on a boolean business parameter.
    /// 
    /// Example:
    ///   Business: "HAS_HOLE" = false
    ///   Features: ["Cut-Extrude1", "Sketch2"]
    ///   WhenFalse: "suppress"   → features are suppressed (no hole)
    ///   WhenTrue:  "unsuppress" → features are active (hole exists)
    /// </summary>
    [DataContract]
    public class SuppressionRule
    {
        /// <summary>
        /// List of SolidWorks feature names to toggle (e.g., ["Cut-Extrude1", "Sketch2"]).
        /// </summary>
        [DataMember(Name = "features")]
        public List<string> Features { get; set; }

        /// <summary>
        /// Action when business value is true/non-zero: "suppress" or "unsuppress".
        /// </summary>
        [DataMember(Name = "whenTrue")]
        public string WhenTrue { get; set; }

        /// <summary>
        /// Action when business value is false/zero: "suppress" or "unsuppress".
        /// </summary>
        [DataMember(Name = "whenFalse")]
        public string WhenFalse { get; set; }

        public SuppressionRule()
        {
            Features = new List<string>();
            WhenTrue = "unsuppress";
            WhenFalse = "suppress";
        }
    }

    // ══════════════════════════════════════════════
    // MAPPING RESULT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Result of applying all business parameters to a model via a mapping profile.
    /// </summary>
    public class MappingResult
    {
        public int TotalParameters { get; set; }
        public int DimensionsChanged { get; set; }
        public int DimensionsFailed { get; set; }
        public int FeaturesToggled { get; set; }
        public int FeaturesFailed { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Successes { get; set; }
        public bool OverallSuccess { get { return DimensionsFailed == 0 && FeaturesFailed == 0; } }

        public MappingResult()
        {
            Errors = new List<string>();
            Successes = new List<string>();
        }

        public override string ToString()
        {
            return string.Format(
                "[MappingResult] {0} params → {1} dims OK, {2} dims FAIL, {3} features toggled, {4} features FAIL",
                TotalParameters, DimensionsChanged, DimensionsFailed, FeaturesToggled, FeaturesFailed);
        }
    }
    // ══════════════════════════════════════════════
    // DEALDOX JOB INPUT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Represents a job request from the Dealdox API.
    /// Contains everything needed to generate a variant in headless mode.
    ///
    /// Example JSON from Dealdox:
    /// {
    ///   "masterFile": "C:\\Models\\CONTROL PANEL MOUNTING ANGLE.SLDPRT",
    ///   "outputFolder": "C:\\Output",
    ///   "parameters": {
    ///     "THICKNESS_SKETCH13": 5,
    ///     "V_LEG_SKETCH13": 50,
    ///     "H_LEG_SKETCH13": 50
    ///   }
    /// }
    ///
    /// Usage: SolidWorksConnectorSDK.exe --input=job.json
    /// </summary>
    [DataContract]
    public class DealdoxJob
    {
        /// <summary>
        /// Absolute path to the master .SLDPRT or .SLDASM file.
        /// </summary>
        [DataMember(Name = "masterFile")]
        public string MasterFile { get; set; }

        /// <summary>
        /// Output folder for the generated variant.
        /// </summary>
        [DataMember(Name = "outputFolder")]
        public string OutputFolder { get; set; }

        /// <summary>
        /// Business parameter name → value pairs.
        /// Keys must match the .mapping.json parameter keys.
        /// Values are in business units (typically mm).
        /// </summary>
        [DataMember(Name = "parameters")]
        public Dictionary<string, object> Parameters { get; set; }

        public DealdoxJob()
        {
            Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
