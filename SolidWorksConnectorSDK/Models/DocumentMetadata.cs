using System;
using System.Collections.Generic;

namespace SolidWorksConnectorSDK.Models
{
    public class DimensionMetadata
    {
        public string FullName { get; set; }
        public double Value { get; set; }
        public double ToleranceMin { get; set; }
        public double ToleranceMax { get; set; }
        public int ToleranceType { get; set; }
    }

    /// <summary>
    /// Complete extracted data from a SolidWorks document.
    /// Every accessible parameter is captured here.
    /// </summary>
    public class DocumentMetadata
    {
        // ── 1. FILE INFO ──
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string DirectoryPath { get; set; }
        public string FileType { get; set; }

        // ── 2. DOCUMENT TYPE ──
        public string DocumentTypeDescription { get; set; }
        public int DocumentTypeConstant { get; set; }

        // ── 3. CONFIGURATIONS ──
        public string ActiveConfiguration { get; set; }
        public List<string> ConfigurationNames { get; set; }

        // ── 4. MATERIAL ──
        public string MaterialName { get; set; }

        // ── 5. MASS PROPERTIES ──
        public double? MassKg { get; set; }
        public double? VolumeM3 { get; set; }
        public double? SurfaceAreaM2 { get; set; }
        public double? DensityKgM3 { get; set; }
        public double[] CenterOfGravity { get; set; }

        // ── 6. BOUNDING BOX ──
        public double? BoundingBoxLengthM { get; set; }
        public double? BoundingBoxWidthM { get; set; }
        public double? BoundingBoxHeightM { get; set; }

        // ── 7. FEATURE TREE ──
        public int FeatureCount { get; set; }
        public List<string> FeatureNames { get; set; }

        // ── 8. CUSTOM PROPERTIES (document-level) ──
        public Dictionary<string, string> CustomProperties { get; set; }

        // ── 9. CONFIG-SPECIFIC PROPERTIES ──
        public Dictionary<string, Dictionary<string, string>> ConfigProperties { get; set; }

        // ── 10. BODY COUNT (Parts) ──
        public int SolidBodyCount { get; set; }
        public int SurfaceBodyCount { get; set; }

        // ── 11. COMPONENT INFO (Assemblies) ──
        public int ComponentCount { get; set; }
        public List<string> ComponentPaths { get; set; }

        // ── 12. DIMENSIONS (Named parametric dims) ──
        public List<DimensionMetadata> Dimensions { get; set; }

        // ── 13. EQUATIONS ──
        public List<string> Equations { get; set; }

        // ── 13a. PARAMETRIC: THICKNESS ──
        /// <summary>
        /// Value of the named "Thickness" dimension (in meters), if found.
        /// </summary>
        public double? ThicknessValue { get; set; }

        // ── 13b. PARAMETRIC: HOLE CUT FEATURES ──
        /// <summary>
        /// List of hole-related features (HoleWzd, HoleCut, etc.) with their parameters.
        /// </summary>
        public List<string> HoleCutFeatures { get; set; }

        // ── 14. SUMMARY INFO ──
        public string SummaryTitle { get; set; }
        public string SummarySubject { get; set; }
        public string SummaryAuthor { get; set; }
        public string SummaryKeywords { get; set; }
        public string SummaryComments { get; set; }
        public string SummaryLastSavedBy { get; set; }
        public string SummaryCreateDate { get; set; }
        public string SummaryLastSaveDate { get; set; }

        // ── 15. REFERENCED DOCUMENTS ──
        public List<string> ReferencedDocuments { get; set; }

        // ── 16. DRAWING INFO ──
        public List<string> DrawingSheets { get; set; }
        public List<string> DrawingViews { get; set; }

        // ── TIMESTAMP ──
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            var lines = new List<string>();
            lines.Add("\n╔══════════════════════════════════════════════════════════════╗");
            lines.Add("║              📄 FULL CAD DATA EXTRACTION                    ║");
            lines.Add("╠══════════════════════════════════════════════════════════════╣");
            AddLine(lines, "File Name", FileName);
            AddLine(lines, "Full Path", FullPath);
            AddLine(lines, "File Type", FileType);
            AddLine(lines, "Doc Type", DocumentTypeDescription);
            AddLine(lines, "Config", ActiveConfiguration);

            if (ConfigurationNames != null && ConfigurationNames.Count > 1)
                AddLine(lines, "All Configs", string.Join(", ", ConfigurationNames));

            if (!string.IsNullOrEmpty(MaterialName))
                AddLine(lines, "Material", MaterialName);

            if (MassKg.HasValue) AddLine(lines, "Mass", string.Format("{0:F6} kg", MassKg.Value));
            if (VolumeM3.HasValue) AddLine(lines, "Volume", string.Format("{0:E4} m³", VolumeM3.Value));
            if (SurfaceAreaM2.HasValue) AddLine(lines, "Surface Area", string.Format("{0:E4} m²", SurfaceAreaM2.Value));
            if (DensityKgM3.HasValue) AddLine(lines, "Density", string.Format("{0:F1} kg/m³", DensityKgM3.Value));

            if (BoundingBoxLengthM.HasValue)
                AddLine(lines, "Dimensions", string.Format("{0:F2} × {1:F2} × {2:F2} mm",
                    (BoundingBoxLengthM ?? 0) * 1000, (BoundingBoxWidthM ?? 0) * 1000, (BoundingBoxHeightM ?? 0) * 1000));

            if (SolidBodyCount > 0) AddLine(lines, "Solid Bodies", SolidBodyCount.ToString());
            if (SurfaceBodyCount > 0) AddLine(lines, "Surface Bodies", SurfaceBodyCount.ToString());
            if (ComponentCount > 0) AddLine(lines, "Components", ComponentCount.ToString());
            if (FeatureCount > 0) AddLine(lines, "Features", FeatureCount.ToString());

            if (Dimensions != null && Dimensions.Count > 0)
            {
                AddLine(lines, "Dimensions", string.Format("{0} parametric dims extracted:", Dimensions.Count));
                foreach (var d in Dimensions)
                {
                    string tolStr = "";
                    if (d.ToleranceType != 0) // 0 is usually NONE
                    {
                        if (Math.Abs(d.ToleranceMax - Math.Abs(d.ToleranceMin)) < 1e-6 && d.ToleranceMax != 0)
                            tolStr = string.Format(" ±{0:F2}", d.ToleranceMax);
                        else
                            tolStr = string.Format(" +{0:F2}/-{1:F2}", d.ToleranceMax, Math.Abs(d.ToleranceMin));
                    }
                    AddLine(lines, "  └─", string.Format("{0} = {1:F2}{2}", d.FullName, d.Value, tolStr));
                }
            }

            if (Equations != null && Equations.Count > 0)
                AddLine(lines, "Equations", string.Format("{0} equations", Equations.Count));

            if (ThicknessValue.HasValue)
                AddLine(lines, "Thickness", string.Format("{0:F4} m ({1:F2} mm)", ThicknessValue.Value, ThicknessValue.Value * 1000));

            if (HoleCutFeatures != null && HoleCutFeatures.Count > 0)
            {
                AddLine(lines, "HoleCuts", string.Format("{0} hole feature(s):", HoleCutFeatures.Count));
                foreach (var h in HoleCutFeatures)
                    AddLine(lines, "  hole", h);
            }

            if (CustomProperties != null && CustomProperties.Count > 0)
                AddLine(lines, "Custom Props", string.Format("{0} properties", CustomProperties.Count));

            if (!string.IsNullOrEmpty(SummaryAuthor)) AddLine(lines, "Author", SummaryAuthor);
            if (!string.IsNullOrEmpty(SummaryTitle)) AddLine(lines, "Title", SummaryTitle);

            if (ReferencedDocuments != null && ReferencedDocuments.Count > 0)
                AddLine(lines, "References", string.Format("{0} documents", ReferencedDocuments.Count));

            if (DrawingSheets != null && DrawingSheets.Count > 0)
                AddLine(lines, "Sheets", string.Format("{0} sheets", DrawingSheets.Count));

            if (DrawingViews != null && DrawingViews.Count > 0)
                AddLine(lines, "Views", string.Format("{0} views", DrawingViews.Count));

            lines.Add("╚══════════════════════════════════════════════════════════════╝");
            return string.Join("\n", lines);
        }

        private static void AddLine(List<string> lines, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) value = "N/A";
            if (value.Length > 43) value = value.Substring(0, 40) + "...";
            lines.Add(string.Format("║  {0,-14}: {1,-43}║", label, value));
        }
    }
}
