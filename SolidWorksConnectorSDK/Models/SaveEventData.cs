using System;

namespace SolidWorksConnectorSDK.Models
{
    /// <summary>
    /// Rich payload for save-related events, carrying metadata, timing,
    /// and contextual information for downstream consumers (APIs, logging, etc.).
    /// 
    /// Lifecycle:
    ///   1. Pre-save:  Created with EventType = FileSaveStarting, StartTimeUtc set.
    ///   2. Post-save:  Updated with EventType = FileSaved, EndTimeUtc set, metadata populated.
    ///   
    /// This allows consumers to measure save duration and react to both phases.
    /// </summary>
    public class SaveEventData
    {
        // ──────────────────────────────────────────────
        // Identity
        // ──────────────────────────────────────────────

        /// <summary>
        /// Unique identifier for this save operation.
        /// Correlates pre-save and post-save events for the same operation.
        /// </summary>
        public Guid SaveOperationId { get; set; }

        /// <summary>
        /// Sequential event number tracked by the connector.
        /// </summary>
        public int EventSequenceNumber { get; set; }

        /// <summary>
        /// The type of save event (FileSaveStarting, FileSaved, FileSaveAsStarting, FileSavedAs).
        /// </summary>
        public EventType EventType { get; set; }

        // ──────────────────────────────────────────────
        // File Information
        // ──────────────────────────────────────────────

        /// <summary>
        /// The file name reported by the SolidWorks event callback.
        /// This is the raw value from the COM event — may differ from metadata.
        /// </summary>
        public string RawFileName { get; set; }

        /// <summary>
        /// Full document metadata extracted after the save completes.
        /// Only populated on post-save events (FileSaved, FileSavedAs).
        /// Will be null on pre-save events.
        /// </summary>
        public DocumentMetadata Metadata { get; set; }

        // ──────────────────────────────────────────────
        // Timing
        // ──────────────────────────────────────────────

        /// <summary>
        /// UTC timestamp when the save operation started (pre-save event fired).
        /// </summary>
        public DateTime StartTimeUtc { get; set; }

        /// <summary>
        /// UTC timestamp when the save operation completed (post-save event fired).
        /// Only set on post-save events.
        /// </summary>
        public DateTime? EndTimeUtc { get; set; }

        /// <summary>
        /// Calculated save duration. Returns null if the save hasn't completed yet.
        /// </summary>
        public TimeSpan? SaveDuration
        {
            get
            {
                if (EndTimeUtc.HasValue)
                    return EndTimeUtc.Value - StartTimeUtc;
                return null;
            }
        }

        // ──────────────────────────────────────────────
        // Status
        // ──────────────────────────────────────────────

        /// <summary>
        /// Whether the save operation completed successfully.
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Whether this was a Save-As operation (as opposed to a regular Save).
        /// </summary>
        public bool IsSaveAs { get; set; }

        // ──────────────────────────────────────────────
        // Display
        // ──────────────────────────────────────────────

        /// <summary>
        /// Returns a formatted console-friendly representation of the save event.
        /// </summary>
        public override string ToString()
        {
            string durationStr = SaveDuration.HasValue
                ? string.Format("{0:F0}ms", SaveDuration.Value.TotalMilliseconds)
                : "In Progress";

            string saveTypeStr = IsSaveAs ? "SAVE-AS" : "SAVE";
            string statusStr = IsCompleted ? "✅ COMPLETED" : "⏳ PENDING";

            string result = string.Format(
                "\n╔══════════════════════════════════════════════════════════════╗" +
                "\n║              📄 FILE {0,-8} EVENT                        ║" +
                "\n╠══════════════════════════════════════════════════════════════╣" +
                "\n║  Operation ID : {1,-43}║" +
                "\n║  Status       : {2,-43}║" +
                "\n║  File         : {3,-43}║" +
                "\n║  Duration     : {4,-43}║" +
                "\n║  Timestamp    : {5,-43}║",
                saveTypeStr,
                SaveOperationId.ToString("N").Substring(0, 12),
                statusStr,
                TruncateString(RawFileName, 43),
                durationStr,
                StartTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fff UTC"));

            if (Metadata != null)
            {
                result += string.Format(
                    "\n╠══════════════════════════════════════════════════════════════╣" +
                    "\n║  File Name    : {0,-43}║" +
                    "\n║  Full Path    : {1,-43}║" +
                    "\n║  Directory    : {2,-43}║" +
                    "\n║  File Type    : {3,-43}║" +
                    "\n║  Doc Type     : {4,-43}║" +
                    "\n║  Config       : {5,-43}║",
                    Metadata.FileName ?? "N/A",
                    TruncateString(Metadata.FullPath, 43),
                    TruncateString(Metadata.DirectoryPath, 43),
                    Metadata.FileType ?? "N/A",
                    Metadata.DocumentTypeDescription ?? "Unknown",
                    Metadata.ActiveConfiguration ?? "N/A");

                // Show material if available
                if (!string.IsNullOrEmpty(Metadata.MaterialName))
                {
                    result += string.Format(
                        "\n║  Material     : {0,-43}║", Metadata.MaterialName);
                }

                // Show mass properties if available
                if (Metadata.MassKg.HasValue)
                {
                    result += string.Format(
                        "\n║  Mass         : {0,-43}║",
                        string.Format("{0:F6} kg", Metadata.MassKg.Value));
                }
                if (Metadata.VolumeM3.HasValue)
                {
                    result += string.Format(
                        "\n║  Volume       : {0,-43}║",
                        string.Format("{0:E4} m³", Metadata.VolumeM3.Value));
                }
                if (Metadata.SurfaceAreaM2.HasValue)
                {
                    result += string.Format(
                        "\n║  Surface Area : {0,-43}║",
                        string.Format("{0:E4} m²", Metadata.SurfaceAreaM2.Value));
                }

                // Show bounding box as dimensions
                if (Metadata.BoundingBoxLengthM.HasValue)
                {
                    result += string.Format(
                        "\n║  Dimensions   : {0,-43}║",
                        string.Format("{0:F2} × {1:F2} × {2:F2} mm",
                            (Metadata.BoundingBoxLengthM ?? 0) * 1000,
                            (Metadata.BoundingBoxWidthM ?? 0) * 1000,
                            (Metadata.BoundingBoxHeightM ?? 0) * 1000));
                }

                // Show feature count
                if (Metadata.FeatureCount > 0)
                {
                    result += string.Format(
                        "\n║  Features     : {0,-43}║",
                        string.Format("{0} features", Metadata.FeatureCount));
                }

                // Show custom property count
                if (Metadata.CustomProperties != null && Metadata.CustomProperties.Count > 0)
                {
                    result += string.Format(
                        "\n║  Custom Props : {0,-43}║",
                        string.Format("{0} properties", Metadata.CustomProperties.Count));
                }
            }

            result += "\n╚══════════════════════════════════════════════════════════════╝";
            return result;
        }

        /// <summary>
        /// Truncates a string to fit within maxLength, adding "..." if needed.
        /// </summary>
        private static string TruncateString(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return "N/A".PadRight(maxLength);

            if (value.Length <= maxLength)
                return value.PadRight(maxLength);

            int tailLength = maxLength - 18;
            if (tailLength < 5) tailLength = 5;
            int headLength = maxLength - tailLength - 3;
            if (headLength < 3) headLength = 3;

            return (value.Substring(0, headLength) + "..." + value.Substring(value.Length - tailLength))
                   .PadRight(maxLength);
        }
    }
}
