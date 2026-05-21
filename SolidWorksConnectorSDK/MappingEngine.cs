using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Serilog;
using SolidWorks.Interop.sldworks;
using SolidWorksConnectorSDK.Models;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Core mapping engine that bridges business parameters to SolidWorks dimensions.
    ///
    /// Architecture:
    ///   Dealdox sends:    { "PLATE_LENGTH": 450, "THICKNESS": 25 }
    ///   Profile defines:  PLATE_LENGTH → D1@Sketch1 (scale 0.001)
    ///   Engine applies:   D1@Sketch1 = 0.45 m
    ///
    /// The profile (labels + mappings) is set ONCE per model.
    /// The values change every time Dealdox sends a new request.
    ///
    /// Usage:
    ///   var profile = MappingEngine.LoadProfile("model.mapping.json");
    ///   var businessParams = new Dictionary&lt;string, object&gt; {
    ///       { "PLATE_LENGTH", 450.0 },
    ///       { "THICKNESS", 25.0 },
    ///       { "HAS_HOLE", true }
    ///   };
    ///   var result = MappingEngine.ApplyParameters(doc, profile, businessParams);
    /// </summary>
    public static class MappingEngine
    {
        private static readonly ILogger _log = Log.ForContext(typeof(MappingEngine));
        // ══════════════════════════════════════════════
        // LOAD / SAVE PROFILE
        // ══════════════════════════════════════════════

        /// <summary>
        /// Loads a ModelProfile from a .mapping.json file.
        /// </summary>
        public static ModelProfile LoadProfile(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                _log.Error("Profile not found: {ProfilePath}", jsonPath);
                return null;
            }

            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var profile = DeserializeJson<ModelProfile>(json);

                _log.Information("Loaded profile: {ProfileName} ({ParameterCount} parameters)",
                    profile.ModelName ?? Path.GetFileName(jsonPath),
                    profile.Parameters != null ? profile.Parameters.Count : 0);

                return profile;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to load profile: {ProfilePath}", jsonPath);
                return null;
            }
        }

        /// <summary>
        /// Saves a ModelProfile to a .mapping.json file.
        /// </summary>
        public static void SaveProfile(ModelProfile profile, string jsonPath)
        {
            try
            {
                string json = SerializeJson(profile);
                File.WriteAllText(jsonPath, json, Encoding.UTF8);

                _log.Information("Profile saved: {ProfilePath}", jsonPath);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to save profile: {ProfilePath}", jsonPath);
            }
        }

        // ══════════════════════════════════════════════
        // APPLY BUSINESS PARAMETERS
        // ══════════════════════════════════════════════

        /// <summary>
        /// Takes business parameters from Dealdox and applies them to the SolidWorks model
        /// using the mapping profile.
        ///
        /// Matching strategy (in order):
        ///   1. EXACT match:  DealDox key matches a mapping key directly
        ///   2. FUZZY match:  "Vertical plate length" → matches "verplatelen@skBrkt"
        ///                    by checking if SW abbreviations are prefixes of DealDox words
        ///   3. If fuzzy match found, auto-updates the mapping file for future fast lookups
        ///
        /// Does NOT rebuild — call ParameterModifier.Rebuild() after this method.
        /// </summary>
        public static MappingResult ApplyParameters(
            ModelDoc2 doc,
            ModelProfile profile,
            Dictionary<string, object> businessParams)
        {
            var result = new MappingResult();

            if (doc == null || profile == null || businessParams == null)
            {
                result.Errors.Add("Document, profile, or parameters is null.");
                return result;
            }

            _log.Information("Applying {ParamCount} business parameter(s) using profile \"{ProfileName}\"...",
                businessParams.Count, profile.ModelName);

            result.TotalParameters = businessParams.Count;

            // Track if we added new fuzzy-matched keys (to auto-save the profile)
            bool profileUpdated = false;

            foreach (var kvp in businessParams)
            {
                string paramName = kvp.Key;
                object paramValue = kvp.Value;

                // ── Strategy 1: EXACT match ──
                ParameterMapping mapping;
                if (profile.Parameters.TryGetValue(paramName, out mapping))
                {
                    // Direct match found
                    string mappingType = (mapping.Type ?? "dimension").ToLowerInvariant();
                    if (mappingType == "dimension")
                        ApplyDimensionMapping(doc, paramName, mapping, paramValue, result);
                    else if (mappingType == "suppression")
                        ApplySuppressionMapping(doc, paramName, mapping, paramValue, result);
                    else
                        result.Errors.Add(string.Format("Unknown mapping type '{0}' for '{1}'.", mappingType, paramName));
                    continue;
                }

                // ── Strategy 2: FUZZY match ──
                // DealDox sends "Vertical plate length", mapping has "VERPLATELEN_SKBRKT"
                // SW dimension is "verplatelen@skBrkt"
                // We match by checking if SW name abbreviations are prefixes of DealDox words
                string fuzzyMatchKey = FindFuzzyMatch(paramName, profile);

                if (fuzzyMatchKey != null)
                {
                    mapping = profile.Parameters[fuzzyMatchKey];

                    _log.Information("FUZZY MATCH: \"{DealdoxParam}\" → \"{MappingKey}\" (dim: {SwDimension})",
                        paramName, fuzzyMatchKey,
                        mapping.Dimensions != null && mapping.Dimensions.Count > 0
                            ? mapping.Dimensions[0].SwDimension : "?");

                    // Auto-learn: add DealDox's name as a new key pointing to the same mapping
                    // So next time it's an exact match (fast)
                    if (!profile.Parameters.ContainsKey(paramName))
                    {
                        profile.Parameters[paramName] = mapping;
                        profileUpdated = true;

                        _log.Debug("Auto-learned: \"{ParamName}\" saved for future fast lookup", paramName);
                    }

                    string mappingType = (mapping.Type ?? "dimension").ToLowerInvariant();
                    if (mappingType == "dimension")
                        ApplyDimensionMapping(doc, paramName, mapping, paramValue, result);
                    else if (mappingType == "suppression")
                        ApplySuppressionMapping(doc, paramName, mapping, paramValue, result);
                    continue;
                }

                // ── Strategy 3: RAW SW DIMENSION match ──
                // DealDox sends the exact SW dimension name (e.g., "D1@Sketch1")
                if (fuzzyMatchKey == null)
                {
                    foreach (var entry in profile.Parameters)
                    {
                        if (entry.Value.Dimensions != null)
                        {
                            foreach (var target in entry.Value.Dimensions)
                            {
                                if (string.Equals(target.SwDimension, paramName, StringComparison.OrdinalIgnoreCase))
                                {
                                    fuzzyMatchKey = entry.Key;
                                    break;
                                }
                            }
                        }
                        if (fuzzyMatchKey != null) break;
                    }

                    if (fuzzyMatchKey != null)
                    {
                        mapping = profile.Parameters[fuzzyMatchKey];
                        
                        _log.Information("RAW DIM MATCH: \"{DealdoxParam}\" matches target in \"{MappingKey}\"", paramName, fuzzyMatchKey);

                        string mappingType = (mapping.Type ?? "dimension").ToLowerInvariant();
                        if (mappingType == "dimension")
                            ApplyDimensionMapping(doc, paramName, mapping, paramValue, result);
                        else if (mappingType == "suppression")
                            ApplySuppressionMapping(doc, paramName, mapping, paramValue, result);
                        continue;
                    }
                }

                // ── No match at all ──
                string err = string.Format("No mapping found for '{0}' - skipped.", paramName);
                result.Errors.Add(err);
                _log.Warning("{ErrorMessage}", err);
            }

            // Auto-save profile if we learned new mappings
            if (profileUpdated && !string.IsNullOrEmpty(profile.MasterFilePath))
            {
                string profilePath = FindProfileForModel(profile.MasterFilePath);
                if (profilePath != null)
                {
                    SaveProfile(profile, profilePath);
                    _log.Information("Auto-saved updated mapping profile with learned DealDox names");
                }
            }

            // Summary
            if (result.OverallSuccess)
                _log.Information("Mapping result: {MappingResult}", result);
            else
                _log.Warning("Mapping result: {MappingResult}", result);

            return result;
        }

        // ══════════════════════════════════════════════
        // FUZZY MATCHING ENGINE
        // ══════════════════════════════════════════════

        /// <summary>
        /// Finds the best fuzzy match for a DealDox parameter name against the mapping profile.
        ///
        /// How it works:
        ///   DealDox sends:   "Vertical plate length"
        ///   Mapping has:     "VERPLATELEN_SKBRKT" → swDimension: "verplatelen@skBrkt"
        ///
        ///   Step 1: Split DealDox name into words: ["vertical", "plate", "length"]
        ///   Step 2: For each mapping entry, extract the SW dimension base name: "verplatelen"
        ///   Step 3: Split SW name by camelCase/known boundaries: ["ver", "plate", "len"]
        ///   Step 4: Check if each SW word is a prefix of a DealDox word:
        ///           "ver" → prefix of "vertical" ✓
        ///           "plate" → matches "plate" ✓
        ///           "len" → prefix of "length" ✓
        ///   Step 5: If all SW words match → it's a hit!
        ///
        /// Returns the mapping key if found, null otherwise.
        /// </summary>
        private static string FindFuzzyMatch(string dealdoxName, ModelProfile profile)
        {
            if (string.IsNullOrEmpty(dealdoxName) || profile.Parameters == null || profile.Parameters.Count == 0)
                return null;

            // Split DealDox name into lowercase words
            string[] dealdoxWords = SplitIntoWords(dealdoxName.ToLowerInvariant());
            if (dealdoxWords.Length == 0) return null;

            string bestMatchKey = null;
            int bestScore = 0;

            foreach (var entry in profile.Parameters)
            {
                if (entry.Value.Dimensions == null || entry.Value.Dimensions.Count == 0)
                    continue;

                string swDimName = entry.Value.Dimensions[0].SwDimension ?? "";

                // Extract base name from SW dimension (before @)
                int atIdx = swDimName.IndexOf('@');
                string swBaseName = atIdx > 0 ? swDimName.Substring(0, atIdx) : swDimName;

                // Split SW base name into words (camelCase + known patterns)
                string[] swWords = SplitSwDimensionName(swBaseName.ToLowerInvariant());
                if (swWords.Length == 0) continue;

                // Calculate match score
                int score = CalculateMatchScore(dealdoxWords, swWords);

                // Require at least 2 word matches, and all SW words must match
                if (score > bestScore && score >= swWords.Length && score >= 2)
                {
                    bestScore = score;
                    bestMatchKey = entry.Key;
                }
            }

            return bestMatchKey;
        }

        /// <summary>
        /// Calculates how well DealDox words match SW abbreviation words.
        /// Each SW word that is a prefix of (or equal to) a DealDox word scores 1 point.
        /// </summary>
        private static int CalculateMatchScore(string[] dealdoxWords, string[] swWords)
        {
            int score = 0;
            var usedDealDoxIndices = new HashSet<int>();

            foreach (string swWord in swWords)
            {
                if (swWord.Length < 2) continue; // Skip tiny fragments

                for (int i = 0; i < dealdoxWords.Length; i++)
                {
                    if (usedDealDoxIndices.Contains(i)) continue;

                    string dWord = dealdoxWords[i];

                    // Check if SW word is a prefix of DealDox word (or exact match)
                    // "ver" is prefix of "vertical", "plate" matches "plate", "len" is prefix of "length"
                    if (dWord.StartsWith(swWord) || swWord.StartsWith(dWord) ||
                        dWord == swWord)
                    {
                        score++;
                        usedDealDoxIndices.Add(i);
                        break;
                    }

                    // Also check common abbreviation patterns
                    // "dia" matches "diameter", "wid" matches "width", "hor" matches "horizontal"
                    if (swWord.Length >= 3 && dWord.Length >= 3 &&
                        dWord.Substring(0, Math.Min(3, dWord.Length)) == swWord.Substring(0, Math.Min(3, swWord.Length)))
                    {
                        score++;
                        usedDealDoxIndices.Add(i);
                        break;
                    }
                }
            }

            return score;
        }

        /// <summary>
        /// Splits a DealDox parameter name into lowercase words.
        /// "Vertical plate length" → ["vertical", "plate", "length"]
        /// "Hole center distance" → ["hole", "center", "distance"]
        /// </summary>
        private static string[] SplitIntoWords(string name)
        {
            // Replace common separators with space, then split
            var cleaned = name.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
            var words = new List<string>();

            foreach (string w in cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = w.Trim();
                if (trimmed.Length > 0)
                    words.Add(trimmed);
            }

            return words.ToArray();
        }

        /// <summary>
        /// Splits a SolidWorks dimension base name into word fragments.
        /// Handles camelCase and common abbreviation patterns.
        ///
        /// "verplatelen"    → ["ver", "plate", "len"]
        /// "horplatelen"    → ["hor", "plate", "len"]
        /// "sprBufferBrktWid" → ["spr", "buffer", "brkt", "wid"]
        /// "holeCD"         → ["hole", "cd"]
        /// "holeDia"        → ["hole", "dia"]
        /// </summary>
        private static string[] SplitSwDimensionName(string swName)
        {
            if (string.IsNullOrEmpty(swName)) return new string[0];

            var words = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < swName.Length; i++)
            {
                char c = swName[i];

                // Split on uppercase (camelCase boundary)
                if (char.IsUpper(c) && current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    current.Append(char.ToLower(c));
                }
                // Split on digits
                else if (char.IsDigit(c) && current.Length > 0 && !char.IsDigit(current[current.Length - 1]))
                {
                    words.Add(current.ToString());
                    current.Clear();
                    current.Append(c);
                }
                else if (c == '_' || c == ' ' || c == '-')
                {
                    if (current.Length > 0)
                    {
                        words.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(char.ToLower(c));
                }
            }

            if (current.Length > 0)
                words.Add(current.ToString());

            // Also try known abbreviation splitting for concatenated names
            // e.g., "verplatelen" → try to split into meaningful chunks
            if (words.Count == 1 && words[0].Length > 6)
            {
                var expanded = TrySplitAbbreviation(words[0]);
                if (expanded.Length > 1)
                    return expanded;
            }

            return words.ToArray();
        }

        /// <summary>
        /// Tries to split a concatenated abbreviation into meaningful word fragments.
        /// Uses a greedy approach matching against common engineering terms.
        ///
        /// "verplatelen" → ["ver", "plate", "len"]
        /// "horplatelen" → ["hor", "plate", "len"]
        /// </summary>
        private static string[] TrySplitAbbreviation(string abbrev)
        {
            // Common engineering word prefixes/abbreviations (3+ chars)
            string[] knownParts = new string[]
            {
                "plate", "hole", "buffer", "bracket", "brkt",
                "spring", "spr", "bolt", "stiff", "gusset",
                "flange", "thick", "width", "wid",
                "height", "length", "len", "depth",
                "vert", "ver", "hor", "horiz",
                "dia", "rad", "dist", "center",
                "inner", "outer", "top", "bot", "bottom",
                "left", "right", "front", "rear", "back",
                "angle", "slot", "cut", "edge", "fillet",
                "chamfer", "offset", "pitch", "gap", "space"
            };

            // Sort by length descending (prefer longer matches first)
            Array.Sort(knownParts, (a, b) => b.Length.CompareTo(a.Length));

            var parts = new List<string>();
            string remaining = abbrev;

            int maxIterations = 20; // Safety
            while (remaining.Length > 0 && maxIterations-- > 0)
            {
                bool found = false;
                foreach (string part in knownParts)
                {
                    if (remaining.StartsWith(part) && remaining.Length >= part.Length)
                    {
                        parts.Add(part);
                        remaining = remaining.Substring(part.Length);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // Take remaining as one chunk
                    if (remaining.Length > 0)
                        parts.Add(remaining);
                    break;
                }
            }

            return parts.ToArray();
        }

        // ══════════════════════════════════════════════
        // DIMENSION MAPPING
        // ══════════════════════════════════════════════

        private static void ApplyDimensionMapping(
            ModelDoc2 doc,
            string paramName,
            ParameterMapping mapping,
            object paramValue,
            MappingResult result)
        {
            // Parse business value as double
            double businessValue;
            if (!TryParseDouble(paramValue, out businessValue))
            {
                string err = string.Format("Cannot parse '{0}' value '{1}' as a number.", paramName, paramValue);
                result.Errors.Add(err);
                result.DimensionsFailed++;
                return;
            }

            // Validate against min/max if defined
            if (mapping.MinValue.HasValue && businessValue < mapping.MinValue.Value)
            {
                _log.Warning("{ParamName}={BusinessValue} is below minimum {MinValue}. Clamping",
                    paramName, businessValue, mapping.MinValue.Value);
                businessValue = mapping.MinValue.Value;
            }
            if (mapping.MaxValue.HasValue && businessValue > mapping.MaxValue.Value)
            {
                _log.Warning("{ParamName}={BusinessValue} exceeds maximum {MaxValue}. Clamping",
                    paramName, businessValue, mapping.MaxValue.Value);
                businessValue = mapping.MaxValue.Value;
            }

            // Apply to each target dimension
            if (mapping.Dimensions == null || mapping.Dimensions.Count == 0)
            {
                result.Errors.Add(string.Format("'{0}' has no dimension targets defined.", paramName));
                result.DimensionsFailed++;
                return;
            }

            foreach (var target in mapping.Dimensions)
            {
                if (string.IsNullOrEmpty(target.SwDimension))
                {
                    result.DimensionsFailed++;
                    continue;
                }

                // Calculate final SI value: (businessValue * scaleFactor) + offset
                double scaleFactor = target.ScaleFactor != 0 ? target.ScaleFactor : 0.001;
                double finalSI = (businessValue * scaleFactor) + target.Offset;

                // Apply using ParameterModifier
                var modResult = ParameterModifier.SetParameter(doc, target.SwDimension, finalSI, false);

                if (modResult.Success)
                {
                    // ── POST-UPDATE VERIFICATION ──
                    // Read back the dimension from SolidWorks to confirm it accepted the value
                    double actualMm = 0;
                    bool verified = false;
                    try
                    {
                        var param = doc.Parameter(target.SwDimension);
                        if (param != null)
                        {
                            double actualSI = param.SystemValue;
                            actualMm = actualSI * 1000.0;
                            double requestedMm = businessValue;
                            // Compare in mm space with 0.01mm tolerance
                            verified = Math.Abs(actualMm - requestedMm) < 0.01;
                        }
                    }
                    catch (Exception ex) { _log.Debug(ex, "Error verifying dimension readback for {SwDimension}", target.SwDimension); }

                    result.DimensionsChanged++;

                    if (verified)
                    {
                        string msg = string.Format("  ✅ {0} → {1}: {2}{3} → {4:F6} m [VERIFIED: {5:F2} mm]",
                            paramName, target.SwDimension,
                            businessValue, mapping.Unit ?? "mm", finalSI, actualMm);
                        result.Successes.Add(msg);
                    }
                    else
                    {
                        string msg = string.Format("  ✅ {0} → {1}: {2}{3} → {4:F6} m [SET OK, readback: {5:F2} mm]",
                            paramName, target.SwDimension,
                            businessValue, mapping.Unit ?? "mm", finalSI, actualMm);
                        result.Successes.Add(msg);
                    }
                }
                else
                {
                    result.DimensionsFailed++;
                    string err = string.Format("  ❌ {0} → {1}: {2}",
                        paramName, target.SwDimension, modResult.Error);
                    result.Errors.Add(err);
                }
            }
        }

        // ══════════════════════════════════════════════
        // SUPPRESSION MAPPING
        // ══════════════════════════════════════════════

        private static void ApplySuppressionMapping(
            ModelDoc2 doc,
            string paramName,
            ParameterMapping mapping,
            object paramValue,
            MappingResult result)
        {
            if (mapping.Suppression == null || mapping.Suppression.Features == null || mapping.Suppression.Features.Count == 0)
            {
                result.Errors.Add(string.Format("'{0}' has no suppression rules defined.", paramName));
                result.FeaturesFailed++;
                return;
            }

            // Parse as boolean
            bool boolValue = TryParseBool(paramValue);
            string action = boolValue
                ? (mapping.Suppression.WhenTrue ?? "unsuppress")
                : (mapping.Suppression.WhenFalse ?? "suppress");
            bool shouldSuppress = action.ToLowerInvariant() == "suppress";

            _log.Information("{ParamName}={BoolValue} → {Action} features: [{FeatureList}]",
                paramName, boolValue, action, string.Join(", ", mapping.Suppression.Features));

            foreach (string featureName in mapping.Suppression.Features)
            {
                try
                {
                    // Find the feature by name
                    Feature targetFeat = null;
                    Feature walkFeat = (Feature)doc.FirstFeature();
                    while (walkFeat != null)
                    {
                        string name = walkFeat.Name ?? "";
                        if (string.Equals(name, featureName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetFeat = walkFeat;
                            break;
                        }
                        walkFeat = (Feature)walkFeat.GetNextFeature();
                    }

                    if (targetFeat == null)
                    {
                        // Try SelectByID2 as fallback
                        doc.ClearSelection2(true);
                        bool selected = doc.Extension.SelectByID2(
                            featureName, "BODYFEATURE", 0, 0, 0, false, 0, null, 0);

                        if (!selected)
                        {
                            // Try as sketch
                            selected = doc.Extension.SelectByID2(
                                featureName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        }

                        if (selected)
                        {
                            if (shouldSuppress)
                                doc.EditSuppress();
                            else
                                doc.EditUnsuppress2();

                            result.FeaturesToggled++;
                            result.Successes.Add(string.Format("  ✅ {0} → {1}", featureName, action));
                        }
                        else
                        {
                            result.FeaturesFailed++;
                            result.Errors.Add(string.Format("  ❌ Feature '{0}' not found.", featureName));
                        }
                    }
                    else
                    {
                        doc.ClearSelection2(true);
                        targetFeat.Select2(false, 0);

                        if (shouldSuppress)
                            doc.EditSuppress();
                        else
                            doc.EditUnsuppress2();

                        result.FeaturesToggled++;
                        result.Successes.Add(string.Format("  ✅ {0} → {1}", featureName, action));
                    }
                }
                catch (Exception ex)
                {
                    result.FeaturesFailed++;
                    result.Errors.Add(string.Format("  ❌ Feature '{0}': {1}", featureName, ex.Message));
                }
            }

            doc.ClearSelection2(true);
        }

        // ══════════════════════════════════════════════
        // GENERATE PROFILE FROM MODEL (Discovery → Profile)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Scans a SolidWorks model and generates a starter ModelProfile
        /// with all discovered dimensions. The user can then edit the JSON
        /// to assign meaningful business names.
        ///
        /// Generated entries use the SW dimension name as the business key
        /// (e.g., "D1_Sketch1") and include the current value as default.
        /// </summary>
        public static ModelProfile GenerateProfileFromModel(ModelDoc2 doc, string modelName, string masterFilePath)
        {
            var profile = new ModelProfile
            {
                ModelName = modelName,
                MasterFilePath = masterFilePath,
                Description = "Auto-generated profile — edit business parameter names as needed",
                CreatedAt = DateTime.UtcNow
            };

            int dimCount = 0;
            var seenSwDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // skip duplicates
            Feature feat = (Feature)doc.FirstFeature();
            while (feat != null)
            {
                try
                {
                    string featName = feat.Name ?? "(unnamed)";
                    string featType = feat.GetTypeName2() ?? "(unknown)";

                    DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                    while (dispDim != null)
                    {
                        try
                        {
                            Dimension dim = (Dimension)dispDim.GetDimension2(0);
                            if (dim != null)
                            {
                                string fullName = dim.FullName ?? "?";
                                double currentSI = dim.SystemValue;

                                // Create a parameter key: strip the @PartName.Part suffix
                                string paramKey = fullName;
                                int lastAt = paramKey.LastIndexOf('@');
                                if (lastAt > 0)
                                    paramKey = paramKey.Substring(0, lastAt);

                                // Skip if we already have a mapping for this SW dimension
                                if (seenSwDimensions.Contains(paramKey))
                                {
                                    dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                                    continue;
                                }
                                seenSwDimensions.Add(paramKey);

                                // Create a clean business name (replace @ with _, remove special chars)
                                string businessName = paramKey.Replace('@', '_').Replace('.', '_').ToUpperInvariant();

                                // Avoid duplicates (same dim name from different features)
                                if (profile.Parameters.ContainsKey(businessName))
                                {
                                    businessName = businessName + "_" + featName.Replace(' ', '_').ToUpperInvariant();
                                }

                                var mapping = new ParameterMapping
                                {
                                    DisplayName = string.Format("{0} ({1})", paramKey, featName),
                                    Unit = "mm",
                                    Type = "dimension",
                                    Editable = true,
                                    DefaultValue = Math.Round(currentSI * 1000.0, 4),  // Convert m → mm for display
                                    Dimensions = new List<DimensionTarget>
                                    {
                                        new DimensionTarget
                                        {
                                            SwDimension = paramKey,
                                            ScaleFactor = 0.001,  // mm → m
                                            Offset = 0.0
                                        }
                                    }
                                };

                                profile.Parameters[businessName] = mapping;
                                dimCount++;
                            }
                        }
                        catch (Exception ex) { _log.Debug(ex, "Error extracting dimension during profile generation"); }
                        dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Error iterating feature during profile generation"); }
                feat = (Feature)feat.GetNextFeature();
            }

            _log.Information("Generated profile with {DimCount} dimension mappings", dimCount);

            return profile;
        }

        // ══════════════════════════════════════════════
        // FIND PROFILE FILE
        // ══════════════════════════════════════════════

        /// <summary>
        /// Searches for a .mapping.json file next to the master model.
        /// Returns the path if found, null otherwise.
        /// 
        /// STRICT matching only: requires exact name match.
        ///   e.g., "CT_STOPPER PLATE.SLDPRT" → "CT_STOPPER PLATE.mapping.json"
        /// 
        /// This prevents accidentally applying one model's mapping to a different model.
        /// </summary>
        public static string FindProfileForModel(string masterFilePath)
        {
            if (string.IsNullOrEmpty(masterFilePath))
                return null;

            string dir = Path.GetDirectoryName(masterFilePath);
            string baseName = Path.GetFileNameWithoutExtension(masterFilePath);

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            // Strict: only exact model name match
            string exactPath = Path.Combine(dir, baseName + ".mapping.json");
            if (File.Exists(exactPath))
                return exactPath;

            return null;
        }

        // ══════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════

        private static bool TryParseDouble(object value, out double result)
        {
            result = 0;
            if (value == null) return false;

            if (value is double) { result = (double)value; return true; }
            if (value is int) { result = (int)value; return true; }
            if (value is long) { result = (long)value; return true; }
            if (value is float) { result = (float)value; return true; }
            if (value is decimal) { result = (double)(decimal)value; return true; }

            return double.TryParse(value.ToString(), out result);
        }

        private static bool TryParseBool(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;

            string str = value.ToString().Trim().ToLowerInvariant();
            if (str == "true" || str == "1" || str == "yes") return true;
            if (str == "false" || str == "0" || str == "no") return false;

            // Numeric: non-zero = true
            double num;
            if (double.TryParse(str, out num)) return num != 0;

            return false;
        }

        // ══════════════════════════════════════════════
        // JSON SERIALIZATION (using DataContractJsonSerializer)
        // ══════════════════════════════════════════════
        // .NET Framework 4.8 doesn't have System.Text.Json,
        // and we avoid adding NuGet packages for simplicity.

        private static string SerializeJson<T>(T obj)
        {
            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                DateTimeFormat = new DateTimeFormat("yyyy-MM-ddTHH:mm:ssZ")
            };

            var serializer = new DataContractJsonSerializer(typeof(T), settings);
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, obj);
                byte[] jsonBytes = ms.ToArray();
                string json = Encoding.UTF8.GetString(jsonBytes);

                // Pretty-print: simple indent for readability
                return PrettyPrintJson(json);
            }
        }

        private static T DeserializeJson<T>(string json)
        {
            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
                DateTimeFormat = new DateTimeFormat("yyyy-MM-ddTHH:mm:ssZ")
            };

            var serializer = new DataContractJsonSerializer(typeof(T), settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(ms);
            }
        }

        /// <summary>
        /// Simple JSON pretty-printer for human-readable output.
        /// Adds indentation to make the mapping file easy to edit manually.
        /// </summary>
        private static string PrettyPrintJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escaped = false;

            foreach (char c in json)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    sb.Append(c);
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        sb.AppendLine();
                        indent--;
                        if (indent < 0) indent = 0;
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(c);
                        sb.Append(' ');
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
