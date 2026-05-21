using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Serilog;
using SolidWorksConnectorSDK.Models;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Serializes DocumentMetadata to JSON and saves to disk.
    /// 
    /// Uses manual JSON building (no external dependencies) for .NET Framework 4.8.
    /// 
    /// Output Location:
    ///   {EXE_DIR}\ExtractedData\{filename}_{timestamp}.json
    /// 
    /// This is production-ready for the pipeline:
    ///   SolidWorks Save → SDK Extract → JSON File → FastAPI → Cloud
    /// </summary>
    public static class JsonExporter
    {
        private static readonly ILogger _log = Log.ForContext(typeof(JsonExporter));
        private static readonly string OutputFolder;

        static JsonExporter()
        {
            // Create output folder next to the EXE
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            OutputFolder = Path.Combine(exeDir, "ExtractedData");

            try
            {
                if (!Directory.Exists(OutputFolder))
                {
                    Directory.CreateDirectory(OutputFolder);
                }
                
                _log.Information("JSON Output Folder: {OutputFolder}", Path.GetFullPath(OutputFolder));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create output folder: {OutputFolder}", OutputFolder);
            }
        }

        /// <summary>
        /// Serializes the metadata to JSON and saves to a file.
        /// Returns the full path to the saved JSON file, or null on failure.
        /// </summary>
        public static string SaveToJson(DocumentMetadata metadata)
        {
            if (metadata == null) return null;

            try
            {
                string json = SerializeToJson(metadata);

                // Build filename: PartName_2026-04-22_163000.json
                string safeName = GetSafeFileName(metadata.FileName ?? "unknown");
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
                string jsonFileName = string.Format("{0}_{1}.json", safeName, timestamp);
                string fullPath = Path.Combine(OutputFolder, jsonFileName);

                File.WriteAllText(fullPath, json, Encoding.UTF8);

                _log.Information("Saved JSON to: {JsonPath}", Path.GetFullPath(fullPath));

                return fullPath;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to save JSON for {FileName}", metadata.FileName);
                return null;
            }
        }

        /// <summary>
        /// Serializes the SaveEventData (with metadata) to JSON and saves to a file.
        /// </summary>
        public static string SaveEventToJson(SaveEventData saveData)
        {
            if (saveData == null) return null;

            try
            {
                _log.Information("JSON export triggered for {FileName}", saveData.RawFileName);

                string json = SerializeSaveEventToJson(saveData);

                string safeName = GetSafeFileName(saveData.Metadata?.FileName ?? saveData.RawFileName ?? "unknown");
                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
                string jsonFileName = string.Format("SAVE_{0}_{1}.json", safeName, timestamp);
                string fullPath = Path.Combine(OutputFolder, jsonFileName);

                File.WriteAllText(fullPath, json, Encoding.UTF8);

                _log.Information("Saved JSON to: {JsonPath}", Path.GetFullPath(fullPath));

                return fullPath;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to save event JSON for {FileName}", saveData.RawFileName);
                return null;
            }
        }

        // ══════════════════════════════════════════════
        // JSON SERIALIZATION (Manual — no dependencies)
        // ══════════════════════════════════════════════

        private static string SerializeToJson(DocumentMetadata m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");

            // File Info
            AppendJsonString(sb, "fileName", m.FileName, true);
            AppendJsonString(sb, "fullPath", m.FullPath, true);
            AppendJsonString(sb, "directoryPath", m.DirectoryPath, true);
            AppendJsonString(sb, "fileType", m.FileType, true);

            // Document Type
            AppendJsonString(sb, "documentTypeDescription", m.DocumentTypeDescription, true);
            AppendJsonNumber(sb, "documentTypeConstant", m.DocumentTypeConstant, true);

            // Configuration
            AppendJsonString(sb, "activeConfiguration", m.ActiveConfiguration, true);

            // Material
            AppendJsonString(sb, "materialName", m.MaterialName, true);

            // Mass Properties
            AppendJsonNullableDouble(sb, "massKg", m.MassKg, true);
            AppendJsonNullableDouble(sb, "volumeM3", m.VolumeM3, true);
            AppendJsonNullableDouble(sb, "surfaceAreaM2", m.SurfaceAreaM2, true);
            AppendJsonNullableDouble(sb, "densityKgM3", m.DensityKgM3, true);

            // Center of Gravity
            if (m.CenterOfGravity != null && m.CenterOfGravity.Length >= 3)
            {
                sb.AppendFormat("  \"centerOfGravity\": [{0:G}, {1:G}, {2:G}],\n",
                    m.CenterOfGravity[0], m.CenterOfGravity[1], m.CenterOfGravity[2]);
            }
            else
            {
                sb.AppendLine("  \"centerOfGravity\": null,");
            }

            // Bounding Box
            AppendJsonNullableDouble(sb, "boundingBoxLengthM", m.BoundingBoxLengthM, true);
            AppendJsonNullableDouble(sb, "boundingBoxWidthM", m.BoundingBoxWidthM, true);
            AppendJsonNullableDouble(sb, "boundingBoxHeightM", m.BoundingBoxHeightM, true);

            // Feature Tree
            AppendJsonNumber(sb, "featureCount", m.FeatureCount, true);
            if (m.FeatureNames != null && m.FeatureNames.Count > 0)
            {
                sb.Append("  \"featureNames\": [");
                for (int i = 0; i < m.FeatureNames.Count; i++)
                {
                    sb.AppendFormat("\n    {0}", EscapeJsonString(m.FeatureNames[i]));
                    if (i < m.FeatureNames.Count - 1) sb.Append(",");
                }
                sb.AppendLine("\n  ],");
            }
            else
            {
                sb.AppendLine("  \"featureNames\": [],");
            }

            SerializeDimensionsToJson(sb, m.Dimensions, "  ");

            // Thickness
            AppendJsonNullableDouble(sb, "thicknessValue", m.ThicknessValue, true);

            // Hole Cut Features
            SerializeStringListToJson(sb, m.HoleCutFeatures, "holeCutFeatures", "  ");

            // Custom Properties
            if (m.CustomProperties != null && m.CustomProperties.Count > 0)
            {
                sb.AppendLine("  \"customProperties\": {");
                int count = 0;
                foreach (var kvp in m.CustomProperties)
                {
                    count++;
                    sb.AppendFormat("    {0}: {1}",
                        EscapeJsonString(kvp.Key), EscapeJsonString(kvp.Value));
                    if (count < m.CustomProperties.Count) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("  },");
            }
            else
            {
                sb.AppendLine("  \"customProperties\": {},");
            }

            // Timestamp (last field — no trailing comma)
            AppendJsonString(sb, "timestamp", m.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), false);

            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string SerializeSaveEventToJson(SaveEventData s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");

            // Save Event Info
            AppendJsonString(sb, "saveOperationId", s.SaveOperationId.ToString(), true);
            AppendJsonNumber(sb, "eventSequenceNumber", s.EventSequenceNumber, true);
            AppendJsonString(sb, "eventType", s.EventType.ToString(), true);
            AppendJsonString(sb, "rawFileName", s.RawFileName, true);
            AppendJsonBool(sb, "isSaveAs", s.IsSaveAs, true);
            AppendJsonBool(sb, "isCompleted", s.IsCompleted, true);
            AppendJsonString(sb, "startTimeUtc", s.StartTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), true);

            if (s.EndTimeUtc.HasValue)
                AppendJsonString(sb, "endTimeUtc", s.EndTimeUtc.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), true);
            else
                sb.AppendLine("  \"endTimeUtc\": null,");

            if (s.SaveDuration.HasValue)
                AppendJsonNullableDouble(sb, "saveDurationMs", s.SaveDuration.Value.TotalMilliseconds, true);
            else
                sb.AppendLine("  \"saveDurationMs\": null,");

            // Embedded Metadata
            if (s.Metadata != null)
            {
                sb.AppendLine("  \"metadata\": {");

                var m = s.Metadata;
                sb.AppendFormat("    \"fileName\": {0},\n", EscapeJsonString(m.FileName));
                sb.AppendFormat("    \"fullPath\": {0},\n", EscapeJsonString(m.FullPath));
                sb.AppendFormat("    \"directoryPath\": {0},\n", EscapeJsonString(m.DirectoryPath));
                sb.AppendFormat("    \"fileType\": {0},\n", EscapeJsonString(m.FileType));
                sb.AppendFormat("    \"documentTypeDescription\": {0},\n", EscapeJsonString(m.DocumentTypeDescription));
                sb.AppendFormat("    \"documentTypeConstant\": {0},\n", m.DocumentTypeConstant);
                sb.AppendFormat("    \"activeConfiguration\": {0},\n", EscapeJsonString(m.ActiveConfiguration));
                sb.AppendFormat("    \"materialName\": {0},\n", EscapeJsonString(m.MaterialName));

                sb.AppendFormat("    \"massKg\": {0},\n", FormatNullableDouble(m.MassKg));
                sb.AppendFormat("    \"volumeM3\": {0},\n", FormatNullableDouble(m.VolumeM3));
                sb.AppendFormat("    \"surfaceAreaM2\": {0},\n", FormatNullableDouble(m.SurfaceAreaM2));
                sb.AppendFormat("    \"densityKgM3\": {0},\n", FormatNullableDouble(m.DensityKgM3));
                sb.AppendFormat("    \"featureCount\": {0},\n", m.FeatureCount);

                SerializeDimensionsToJson(sb, m.Dimensions, "    ");

                // Thickness
                sb.AppendFormat("    \"thicknessValue\": {0},\n", FormatNullableDouble(m.ThicknessValue));

                // Hole Cut Features
                SerializeStringListToJson(sb, m.HoleCutFeatures, "holeCutFeatures", "    ");

                // Custom properties inline
                if (m.CustomProperties != null && m.CustomProperties.Count > 0)
                {
                    sb.AppendLine("    \"customProperties\": {");
                    int count = 0;
                    foreach (var kvp in m.CustomProperties)
                    {
                        count++;
                        sb.AppendFormat("      {0}: {1}",
                            EscapeJsonString(kvp.Key), EscapeJsonString(kvp.Value));
                        if (count < m.CustomProperties.Count) sb.Append(",");
                        sb.AppendLine();
                    }
                    sb.AppendLine("    },");
                }
                else
                {
                    sb.AppendLine("    \"customProperties\": {},");
                }

                sb.AppendFormat("    \"timestamp\": {0}\n", EscapeJsonString(m.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));
                sb.AppendLine("  }");
            }
            else
            {
                // Last field — no trailing comma
                sb.Append("  \"metadata\": null\n");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════
        // JSON HELPERS
        // ══════════════════════════════════════════════

        private static void SerializeDimensionsToJson(StringBuilder sb, List<DimensionMetadata> dims, string indent)
        {
            if (dims != null && dims.Count > 0)
            {
                sb.AppendLine(indent + "\"dimensions\": [");
                for (int i = 0; i < dims.Count; i++)
                {
                    var d = dims[i];
                    sb.AppendLine(indent + "  {");
                    sb.AppendFormat(indent + "    \"fullName\": {0},\n", EscapeJsonString(d.FullName));
                    sb.AppendFormat(indent + "    \"value\": {0},\n", d.Value.ToString("G"));
                    sb.AppendFormat(indent + "    \"toleranceMin\": {0},\n", d.ToleranceMin.ToString("G"));
                    sb.AppendFormat(indent + "    \"toleranceMax\": {0},\n", d.ToleranceMax.ToString("G"));
                    sb.AppendFormat(indent + "    \"toleranceType\": {0}\n", d.ToleranceType);
                    sb.Append(indent + "  }" + (i < dims.Count - 1 ? "," : "") + "\n");
                }
                sb.AppendLine(indent + "],");
            }
            else
            {
                sb.AppendLine(indent + "\"dimensions\": [],");
            }
        }

        private static void SerializeStringListToJson(StringBuilder sb, List<string> items, string key, string indent)
        {
            if (items != null && items.Count > 0)
            {
                sb.AppendLine(indent + "\"" + key + "\": [");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.AppendFormat("{0}  {1}", indent, EscapeJsonString(items[i]));
                    if (i < items.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine(indent + "],");
            }
            else
            {
                sb.AppendLine(indent + "\"" + key + "\": [],");
            }
        }

        private static string EscapeJsonString(string value)
        {
            if (value == null) return "null";
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t")
                + "\"";
        }

        private static string FormatNullableDouble(double? value)
        {
            return value.HasValue ? value.Value.ToString("G") : "null";
        }

        private static void AppendJsonString(StringBuilder sb, string key, string value, bool hasMore)
        {
            sb.AppendFormat("  \"{0}\": {1}{2}\n", key, EscapeJsonString(value), hasMore ? "," : "");
        }

        private static void AppendJsonNumber(StringBuilder sb, string key, int value, bool hasMore)
        {
            sb.AppendFormat("  \"{0}\": {1}{2}\n", key, value, hasMore ? "," : "");
        }

        private static void AppendJsonNullableDouble(StringBuilder sb, string key, double? value, bool hasMore)
        {
            sb.AppendFormat("  \"{0}\": {1}{2}\n", key, FormatNullableDouble(value), hasMore ? "," : "");
        }

        private static void AppendJsonBool(StringBuilder sb, string key, bool value, bool hasMore)
        {
            sb.AppendFormat("  \"{0}\": {1}{2}\n", key, value ? "true" : "false", hasMore ? "," : "");
        }

        private static string GetSafeFileName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
