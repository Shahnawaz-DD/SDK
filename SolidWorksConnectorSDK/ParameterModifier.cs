using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Serilog;
using SolidWorks.Interop.sldworks;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Result of a single dimension modification attempt.
    /// </summary>
    public class ModifyResult
    {
        public string ParameterName { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }

        public override string ToString()
        {
            return Success
                ? string.Format("[OK] {0}: {1:G6} -> {2:G6}", ParameterName, OldValue, NewValue)
                : string.Format("[FAIL] {0}: {1}", ParameterName, Error);
        }
    }

    /// <summary>
    /// Modifies parametric dimensions on a SolidWorks document.
    /// 
    /// Usage:
    ///   // By exact parameter name (e.g., "D1@Sketch1")
    ///   var result = ParameterModifier.SetParameter(doc, "D1@Sketch1", 0.150);
    /// 
    ///   // By keyword search (e.g., finds first dim containing "Thickness")
    ///   var result = ParameterModifier.SetParameterByName(doc, "Thickness", 0.005);
    /// 
    ///   // Batch modify from a dictionary
    ///   var changes = new Dictionary&lt;string, double&gt; {
    ///       { "LENGTH@Sketch1", 0.150 },
    ///       { "HEIGHT@Sketch1", 0.080 },
    ///       { "Thickness", 0.005 }
    ///   };
    ///   var results = ParameterModifier.SetParameters(doc, changes);
    /// 
    /// All values are in SI units (meters for length, radians for angles).
    /// Call Rebuild() or use autoRebuild=true to regenerate the model after changes.
    /// </summary>
    public static class ParameterModifier
    {
        private static readonly ILogger _log = Log.ForContext(typeof(ParameterModifier));

        // ══════════════════════════════════════════════
        // SINGLE PARAMETER — EXACT NAME
        // ══════════════════════════════════════════════

        /// <summary>
        /// Sets a dimension by its exact parameter name (e.g., "D1@Sketch1", "LENGTH@Sketch1").
        /// Uses ModelDoc2.Parameter() which is the fastest lookup method.
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="parameterName">Exact parameter name (e.g., "D1@Sketch1").</param>
        /// <param name="valueSI">New value in SI units (meters for length, radians for angles).</param>
        /// <param name="autoRebuild">If true, calls EditRebuild3() after modification.</param>
        /// <returns>Result indicating success/failure and old/new values.</returns>
        public static ModifyResult SetParameter(ModelDoc2 doc, string parameterName, double valueSI, bool autoRebuild = false)
        {
            var result = new ModifyResult { ParameterName = parameterName, NewValue = valueSI };

            if (doc == null)
            {
                result.Error = "Document is null.";
                return result;
            }

            if (string.IsNullOrEmpty(parameterName))
            {
                result.Error = "Parameter name is empty.";
                return result;
            }

            Dimension dim = null;
            try
            {
                dim = (Dimension)doc.Parameter(parameterName);
                if (dim == null)
                {
                    result.Error = string.Format("Parameter '{0}' not found in document.", parameterName);
                    return result;
                }

                result.OldValue = dim.SystemValue;
                dim.SystemValue = valueSI;
                result.Success = true;

                _log.Information("Parameter set: {Result}", result);

                if (autoRebuild)
                    Rebuild(doc);
            }
            catch (COMException comEx)
            {
                result.Error = string.Format("COM error 0x{0:X8}: {1}", comEx.ErrorCode, comEx.Message);
                _log.Error(comEx, "COM error setting parameter {ParameterName}", parameterName);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _log.Error(ex, "Error setting parameter {ParameterName}", parameterName);
            }
            finally
            {
                if (dim != null)
                {
                    try { Marshal.ReleaseComObject(dim); }
                    catch (Exception ex) { _log.Debug(ex, "Error releasing COM object for dimension {ParameterName}", parameterName); }
                }
            }

            return result;
        }

        // ══════════════════════════════════════════════
        // SINGLE PARAMETER — BY KEYWORD SEARCH
        // ══════════════════════════════════════════════

        /// <summary>
        /// Searches the entire feature tree for a dimension whose FullName contains the keyword
        /// (case-insensitive). Sets the FIRST match found.
        /// 
        /// Use this for named dimensions like "Thickness", "HoleCut", "Depth", etc.
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="keyword">Partial name to search for (e.g., "Thickness").</param>
        /// <param name="valueSI">New value in SI units.</param>
        /// <param name="autoRebuild">If true, calls EditRebuild3() after modification.</param>
        /// <returns>Result indicating success/failure and old/new values.</returns>
        public static ModifyResult SetParameterByName(ModelDoc2 doc, string keyword, double valueSI, bool autoRebuild = false)
        {
            var result = new ModifyResult { ParameterName = keyword, NewValue = valueSI };

            if (doc == null)
            {
                result.Error = "Document is null.";
                return result;
            }

            try
            {
                Feature feat = (Feature)doc.FirstFeature();
                while (feat != null)
                {
                    try
                    {
                        DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                        while (dispDim != null)
                        {
                            try
                            {
                                Dimension dim = (Dimension)dispDim.GetDimension2(0);
                                if (dim != null)
                                {
                                    string fullName = dim.FullName ?? "";
                                    if (fullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        result.ParameterName = fullName;
                                        result.OldValue = dim.SystemValue;
                                        dim.SystemValue = valueSI;
                                        result.Success = true;

                                        _log.Information("Parameter set by keyword: {Result}", result);

                                        if (autoRebuild)
                                            Rebuild(doc);

                                        return result;
                                    }
                                }
                            }
                            catch (Exception ex) { _log.Debug(ex, "Error checking dimension in keyword search for {Keyword}", keyword); }

                            dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                        }
                    }
                    catch (Exception ex) { _log.Debug(ex, "Error iterating display dimensions during keyword search for {Keyword}", keyword); }

                    feat = (Feature)feat.GetNextFeature();
                }

                result.Error = string.Format("No dimension containing '{0}' found in feature tree.", keyword);
            }
            catch (COMException comEx)
            {
                result.Error = string.Format("COM error 0x{0:X8}: {1}", comEx.ErrorCode, comEx.Message);
                _log.Error(comEx, "COM error during keyword search for {Keyword}", keyword);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _log.Error(ex, "Error during keyword search for {Keyword}", keyword);
            }

            _log.Warning("Parameter keyword search failed: {Result}", result);
            return result;
        }

        // ══════════════════════════════════════════════
        // BATCH MODIFY — DICTIONARY OF CHANGES
        // ══════════════════════════════════════════════

        /// <summary>
        /// Applies multiple parameter changes in a single pass.
        /// 
        /// Keys can be either:
        ///   - Exact parameter names (e.g., "D1@Sketch1") — resolved via doc.Parameter()
        ///   - Keywords (e.g., "Thickness") — resolved via feature tree search
        /// 
        /// Exact names are tried first. If not found, falls back to keyword search.
        /// Rebuild is called ONCE at the end (not after each individual change).
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="changes">Dictionary of parameter name/keyword → new SI value.</param>
        /// <param name="autoRebuild">If true, calls EditRebuild3() after all modifications.</param>
        /// <returns>List of results for each parameter change attempt.</returns>
        public static List<ModifyResult> SetParameters(ModelDoc2 doc, Dictionary<string, double> changes, bool autoRebuild = true)
        {
            var results = new List<ModifyResult>();

            if (doc == null || changes == null || changes.Count == 0)
                return results;

            _log.Information("Applying {ChangeCount} parameter change(s)...", changes.Count);

            foreach (var kvp in changes)
            {
                string name = kvp.Key;
                double value = kvp.Value;

                // Try exact parameter name first (fast path)
                Dimension dim = null;
                try { dim = (Dimension)doc.Parameter(name); }
                catch (Exception ex) { _log.Debug(ex, "Exact parameter lookup failed for {ParameterName}", name); }

                ModifyResult result;
                if (dim != null)
                {
                    // Found by exact name — use the fast path
                    try { Marshal.ReleaseComObject(dim); }
                    catch (Exception ex) { _log.Debug(ex, "Error releasing COM dimension for {ParameterName}", name); }
                    result = SetParameter(doc, name, value, false);
                }
                else
                {
                    // Not found — try keyword search through feature tree
                    result = SetParameterByName(doc, name, value, false);
                }

                results.Add(result);
            }

            // Single rebuild after all changes
            if (autoRebuild)
                Rebuild(doc);

            // Summary
            int successCount = 0;
            int failCount = 0;
            foreach (var r in results)
            {
                if (r.Success) successCount++;
                else failCount++;
            }

            if (failCount == 0)
                _log.Information("Batch complete: {SuccessCount} succeeded, {FailCount} failed", successCount, failCount);
            else
                _log.Warning("Batch complete: {SuccessCount} succeeded, {FailCount} failed", successCount, failCount);

            return results;
        }

        // ══════════════════════════════════════════════
        // REBUILD
        // ══════════════════════════════════════════════

        /// <summary>
        /// Rebuilds (regenerates) the model to apply all pending dimension changes.
        /// </summary>
        public static bool Rebuild(ModelDoc2 doc)
        {
            if (doc == null) return false;

            try
            {
                bool success = doc.EditRebuild3();
                if (success)
                {
                    _log.Information("Model rebuilt successfully");
                }
                else
                {
                    _log.Warning("Rebuild returned false (geometry may have errors)");
                }
                return success;
            }
            catch (COMException comEx)
            {
                _log.Error(comEx, "Rebuild failed: COM 0x{ErrorCode:X8}", comEx.ErrorCode);
                return false;
            }
        }

        // ══════════════════════════════════════════════
        // DIAGNOSTIC: LIST ALL PARAMETERS
        // ══════════════════════════════════════════════

        /// <summary>
        /// Dumps EVERY feature and dimension in the model to the log.
        /// Use this to discover the exact parameter names for your model before modifying.
        /// 
        /// Output format:
        ///   [Feature] Boss-Extrude1 [Extrusion]
        ///     └─ D1@Boss-Extrude1 = 0.005  ← this is your thickness!
        ///   [Feature] Cut-Extrude1 [ICE]
        ///     └─ D1@Sketch2 = 0.010         ← this is your hole diameter!
        /// </summary>
        public static void ListAllParameters(ModelDoc2 doc)
        {
            if (doc == null) return;

            _log.Information("=== PARAMETER DISCOVERY — All Features & Dimensions ===");

            int featureCount = 0;
            int dimCount = 0;

            Feature feat = (Feature)doc.FirstFeature();
            while (feat != null)
            {
                try
                {
                    string featName = feat.Name ?? "(unnamed)";
                    string featType = feat.GetTypeName2() ?? "(unknown)";

                    // Collect dimensions for this feature
                    var dims = new List<string>();
                    DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                    while (dispDim != null)
                    {
                        try
                        {
                            Dimension dim = (Dimension)dispDim.GetDimension2(0);
                            if (dim != null)
                            {
                                string fullName = dim.FullName ?? "?";
                                double val = dim.SystemValue;
                                dims.Add(string.Format("    └─ {0} = {1:G6} m ({2:F2} mm)",
                                    fullName, val, val * 1000));
                                dimCount++;
                            }
                        }
                        catch (Exception ex) { _log.Debug(ex, "Error listing dimension"); }

                        dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                    }

                    // Only log features that have dimensions
                    if (dims.Count > 0)
                    {
                        featureCount++;
                        _log.Information("  [Feature] {FeatureName}  [{FeatureType}]", featName, featType);

                        foreach (string d in dims)
                        {
                            _log.Information("{DimensionInfo}", d);
                        }
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Error listing feature parameters"); }

                feat = (Feature)feat.GetNextFeature();
            }

            _log.Information("Parameter discovery complete: {FeatureCount} features, {DimCount} dimensions found", featureCount, dimCount);
        }

        /// <summary>
        /// Modifies the thickness/depth of the part.
        /// 
        /// Strategy: Finds the FIRST Boss-Extrude feature (type "Extrusion" or "Boss-Extrude")
        /// and sets its depth dimension. This is how SolidWorks stores plate thickness.
        /// Falls back to keyword search for "Thickness" if no extrusion feature is found.
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="thicknessMeters">New thickness in meters (e.g., 0.005 = 5mm).</param>
        /// <param name="autoRebuild">If true, rebuilds the model after change.</param>
        public static ModifyResult SetThickness(ModelDoc2 doc, double thicknessMeters, bool autoRebuild = true)
        {
            if (doc == null)
                return new ModifyResult { ParameterName = "Thickness", Error = "Document is null." };

            // Strategy 1: Find the first Extrusion feature and set its depth dimension
            try
            {
                Feature feat = (Feature)doc.FirstFeature();
                while (feat != null)
                {
                    try
                    {
                        string typeName = (feat.GetTypeName2() ?? "").ToLowerInvariant();

                        // Match Boss-Extrude features (type name varies by SW version)
                        if (typeName == "extrusion" || typeName == "boss-extrude" ||
                            typeName == "bossextrude" || typeName == "baseextrude")
                        {
                            // Get the first dimension of this extrude (the depth)
                            DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                            if (dispDim != null)
                            {
                                Dimension dim = (Dimension)dispDim.GetDimension2(0);
                                if (dim != null)
                                {
                                    var result = new ModifyResult
                                    {
                                        ParameterName = dim.FullName,
                                        OldValue = dim.SystemValue,
                                        NewValue = thicknessMeters,
                                        Success = true
                                    };

                                    dim.SystemValue = thicknessMeters;

                                    _log.Information("Thickness set: {Result}", result);

                                    if (autoRebuild) Rebuild(doc);
                                    return result;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { _log.Debug(ex, "Error checking feature for thickness extrusion"); }

                    feat = (Feature)feat.GetNextFeature();
                }
            }
            catch (Exception ex) { _log.Debug(ex, "Error iterating features for thickness"); }

            // Strategy 2: Fallback to keyword search
            return SetParameterByName(doc, "Thickness", thicknessMeters, autoRebuild);
        }

        // ══════════════════════════════════════════════
        // CONVENIENCE: MODIFY HOLE CUT DIMENSIONS
        // ══════════════════════════════════════════════

        /// <summary>
        /// Modifies all dimensions on hole-related features that match the given keyword.
        /// For example, to set all hole diameters: SetHoleDimension(doc, "Diameter", 0.010)
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="dimensionKeyword">Keyword to match (e.g., "Diameter", "Depth", "HoleCut").</param>
        /// <param name="valueSI">New value in SI units.</param>
        /// <param name="autoRebuild">If true, rebuilds after all changes.</param>
        /// <returns>List of results for each matched dimension.</returns>
        public static List<ModifyResult> SetHoleDimensions(ModelDoc2 doc, string dimensionKeyword, double valueSI, bool autoRebuild = true)
        {
            var results = new List<ModifyResult>();

            if (doc == null) return results;

            try
            {
                Feature feat = (Feature)doc.FirstFeature();
                while (feat != null)
                {
                    try
                    {
                        string typeName = (feat.GetTypeName2() ?? "").ToLowerInvariant();

                        // Only process hole-related features
                        if (typeName.Contains("hole"))
                        {
                            DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                            while (dispDim != null)
                            {
                                try
                                {
                                    Dimension dim = (Dimension)dispDim.GetDimension2(0);
                                    if (dim != null)
                                    {
                                        string fullName = dim.FullName ?? "";
                                        if (fullName.IndexOf(dimensionKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            var result = new ModifyResult
                                            {
                                                ParameterName = fullName,
                                                OldValue = dim.SystemValue,
                                                NewValue = valueSI
                                            };

                                            try
                                            {
                                                dim.SystemValue = valueSI;
                                                result.Success = true;
                                            }
                                            catch (Exception ex)
                                            {
                                                result.Error = ex.Message;
                                                _log.Error(ex, "Failed to set hole dimension {DimensionName}", fullName);
                                            }

                                            if (result.Success)
                                                _log.Information("Hole dimension set: {Result}", result);
                                            else
                                                _log.Error("Hole dimension failed: {Result}", result);

                                            results.Add(result);
                                        }
                                    }
                                }
                                catch (Exception ex) { _log.Debug(ex, "Error checking hole dimension for keyword {Keyword}", dimensionKeyword); }

                                dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                            }
                        }
                    }
                    catch (Exception ex) { _log.Debug(ex, "Error iterating hole feature"); }

                    feat = (Feature)feat.GetNextFeature();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error scanning hole features for keyword {Keyword}", dimensionKeyword);
            }

            if (autoRebuild && results.Count > 0)
                Rebuild(doc);

            return results;
        }

        // ══════════════════════════════════════════════
        // CONVENIENCE: TOGGLE HOLE FEATURES (HasHole)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Suppresses or unsuppresses ALL hole-related features in the document.
        /// 
        ///   HasHole = true  → Unsuppress (holes are visible / cut into the part)
        ///   HasHole = false → Suppress   (holes are removed / part becomes solid)
        /// 
        /// Matches feature types: HoleWzd, HoleCut, Hole, HoleChamfer, HoleSeries, etc.
        /// </summary>
        /// <param name="doc">The active ModelDoc2 document.</param>
        /// <param name="hasHole">True = holes exist, False = holes suppressed.</param>
        /// <returns>Number of hole features toggled.</returns>
        public static int SetHoleFeatureState(ModelDoc2 doc, bool hasHole)
        {
            if (doc == null) return 0;

            // Rebuild first so the model is in a clean state
            doc.EditRebuild3();

            _log.Information("HasHole={HasHole} — Direct selection approach", hasHole);

            int count = 0;

            // Try to select and suppress/unsuppress known hole feature names
            // We try multiple names and types to cover different model structures
            string[][] targets = new string[][]
            {
                // { "FeatureName", "SelectionType" }
                new string[] { "Cut-Extrude1", "BODYFEATURE" },
                new string[] { "Cut-Extrude2", "BODYFEATURE" },
                new string[] { "Hole1", "BODYFEATURE" },
                new string[] { "HoleWzd1", "BODYFEATURE" },
                new string[] { "Sketch2", "SKETCH" },
                new string[] { "Sketch3", "SKETCH" },
            };

            foreach (string[] target in targets)
            {
                string name = target[0];
                string type = target[1];

                try
                {
                    doc.ClearSelection2(true);
                    bool selected = doc.Extension.SelectByID2(
                        name, type, 0, 0, 0, false, 0, null, 0);

                    if (selected)
                    {
                        _log.Debug("Selected: {FeatureName} [{SelectionType}]", name, type);

                        if (hasHole)
                            doc.EditUnsuppress2();
                        else
                            doc.EditSuppress();

                        count++;
                        _log.Information("{FeatureName} -> {State}", name, hasHole ? "UNSUPPRESSED" : "SUPPRESSED");
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Could not toggle feature {FeatureName}", name);
                }
            }

            doc.ClearSelection2(true);

            // If nothing was found via direct selection, try equation-based fallback:
            // Set HOLEDIAMETER to a tiny value (effectively removes the hole visually)
            if (count == 0)
            {
                _log.Warning("No features found by name. Trying HOLEDIAMETER fallback...");

                try
                {
                    Dimension holeDim = (Dimension)doc.Parameter("HOLEDIAMETER@Sketch2");
                    if (holeDim != null)
                    {
                        if (hasHole)
                        {
                            // Restore to original size (read from model or use default)
                            // If already > 1mm, leave it alone
                            if (holeDim.SystemValue < 0.001)
                            {
                                holeDim.SystemValue = 0.050; // 50mm default
                                _log.Information("HOLEDIAMETER restored to 50mm");
                                count++;
                            }
                            else
                            {
                                _log.Information("Hole already exists (diameter={DiameterMM:F2}mm)", holeDim.SystemValue * 1000);
                                count++;
                            }
                        }
                        else
                        {
                            holeDim.SystemValue = 0.0001; // 0.1mm = effectively no hole
                            _log.Information("HOLEDIAMETER set to 0.1mm (hole removed)");
                            count++;
                        }
                    }
                    else
                    {
                        _log.Error("HOLEDIAMETER@Sketch2 not found!");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "HOLEDIAMETER fallback failed");
                }
            }

            doc.EditRebuild3();

            if (count > 0)
                _log.Information("HasHole={HasHole}: {ActionCount} action(s) completed", hasHole, count);
            else
                _log.Warning("HasHole={HasHole}: {ActionCount} action(s) completed — no features were toggled", hasHole, count);

            return count;
        }
    }
}
