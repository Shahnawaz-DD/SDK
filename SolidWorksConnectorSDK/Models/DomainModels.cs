using System;
using System.Collections.Generic;

namespace SolidWorksConnectorSDK.Models
{
    public class GeometryMetadata
    {
        public double? BoundingBoxLengthM { get; set; }
        public double? BoundingBoxWidthM { get; set; }
        public double? BoundingBoxHeightM { get; set; }
        public double? ThicknessValueM { get; set; }
        public List<string> HoleCutFeatures { get; set; } = new List<string>();
        public List<DimensionMetadata> Dimensions { get; set; } = new List<DimensionMetadata>();
        public List<string> Equations { get; set; } = new List<string>();
    }

    public class MassMetadata
    {
        public string MaterialName { get; set; }
        public double? MassKg { get; set; }
        public double? VolumeM3 { get; set; }
        public double? SurfaceAreaM2 { get; set; }
        public double? DensityKgM3 { get; set; }
        public double[] CenterOfGravity { get; set; }
    }

    public class AssemblyMetadata
    {
        public int ComponentCount { get; set; }
        public List<string> ComponentPaths { get; set; } = new List<string>();
        public int SolidBodyCount { get; set; }
        public int SurfaceBodyCount { get; set; }
    }

    public class DrawingMetadata
    {
        public List<string> DrawingSheets { get; set; } = new List<string>();
        public List<string> DrawingViews { get; set; } = new List<string>();
    }

    public class FileMetadata
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string DirectoryPath { get; set; }
        public string FileType { get; set; }
        public string DocumentTypeDescription { get; set; }
        public int DocumentTypeConstant { get; set; }
        public string ActiveConfiguration { get; set; }
        public List<string> ConfigurationNames { get; set; } = new List<string>();
    }

    public class SummaryMetadata
    {
        public string SummaryTitle { get; set; }
        public string SummarySubject { get; set; }
        public string SummaryAuthor { get; set; }
        public string SummaryKeywords { get; set; }
        public string SummaryComments { get; set; }
        public string SummaryLastSavedBy { get; set; }
        public string SummaryCreateDate { get; set; }
        public string SummaryLastSaveDate { get; set; }
    }

    /// <summary>
    /// The root aggregate model containing all bounded contexts.
    /// </summary>
    public class DocumentExtractionResult
    {
        public FileMetadata File { get; set; } = new FileMetadata();
        public GeometryMetadata Geometry { get; set; } = new GeometryMetadata();
        public MassMetadata Mass { get; set; } = new MassMetadata();
        public AssemblyMetadata Assembly { get; set; } = new AssemblyMetadata();
        public DrawingMetadata Drawing { get; set; } = new DrawingMetadata();
        public SummaryMetadata Summary { get; set; } = new SummaryMetadata();

        public int FeatureCount { get; set; }
        public List<string> FeatureNames { get; set; } = new List<string>();
        public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> ConfigProperties { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public List<string> ReferencedDocuments { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
