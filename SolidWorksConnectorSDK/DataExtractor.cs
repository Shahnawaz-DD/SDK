using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksConnectorSDK.Models;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Extracts EVERY accessible parameter from a SolidWorks document.
    /// Each step is independently try/caught — one failure never blocks others.
    /// </summary>
    public static class DataExtractor
    {
        private static readonly ILogger _log = Log.ForContext(typeof(DataExtractor));

        private const int swDocPART = 1;
        private const int swDocASSEMBLY = 2;
        private const int swDocDRAWING = 3;

        public static DocumentMetadata ExtractMetadata(object documentObject)
        {
            if (documentObject == null) { _log.Warning("Null document passed to ExtractMetadata"); return null; }

            try
            {
                ModelDoc2 doc = documentObject as ModelDoc2;
                if (doc != null) return ExtractAll(doc);

                _log.Warning("ModelDoc2 cast failed. Using reflection fallback");
                return ExtractViaReflection(documentObject);
            }
            catch (COMException comEx)
            {
                _log.Error(comEx, "COM error during metadata extraction (0x{ErrorCode:X8})", comEx.ErrorCode);
                return null;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Unexpected error during metadata extraction");
                return null;
            }
        }

        private static DocumentMetadata ExtractAll(ModelDoc2 doc)
        {
            var m = new DocumentMetadata { Timestamp = DateTime.UtcNow };

            Step("FileInfo", () => ExtractFileInfo(doc, m));
            Step("DocType", () => ExtractDocumentType(doc, m));
            Step("Configs", () => ExtractConfigurations(doc, m));
            Step("Material", () => ExtractMaterial(doc, m));
            Step("MassProps", () => ExtractMassProperties(doc, m));
            Step("Features", () => ExtractFeatureTree(doc, m));
            Step("CustomProps", () => ExtractCustomProperties(doc, m));
            Step("ConfigProps", () => ExtractConfigProperties(doc, m));
            Step("BoundingBox", () => ExtractBoundingBox(doc, m));
            Step("BodyCount", () => ExtractBodyCount(doc, m));
            Step("Components", () => ExtractComponentInfo(doc, m));
            Step("Dimensions", () => ExtractDimensions(doc, m));
            Step("Equations", () => ExtractEquations(doc, m));
            Step("Thickness", () => ExtractThickness(doc, m));
            Step("HoleCuts", () => ExtractHoleCutFeatures(doc, m));
            Step("SummaryInfo", () => ExtractSummaryInfo(doc, m));
            Step("References", () => ExtractReferencedDocuments(doc, m));
            Step("DrawingInfo", () => ExtractDrawingInfo(doc, m));

            return m;
        }

        private static void Step(string name, Action action)
        {
            try { action(); }
            catch (COMException comEx)
            {
                _log.Warning(comEx, "Extraction step {StepName} failed (COM 0x{ErrorCode:X8})", name, comEx.ErrorCode);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Extraction step {StepName} failed", name);
            }
        }

        // ── 1. FILE INFO ──
        private static void ExtractFileInfo(ModelDoc2 doc, DocumentMetadata m)
        {
            try { m.FullPath = doc.GetPathName(); }
            catch (Exception ex) { _log.Debug(ex, "Could not get file path"); }

            if (!string.IsNullOrEmpty(m.FullPath))
            {
                m.FileName = Path.GetFileName(m.FullPath);
                m.DirectoryPath = Path.GetDirectoryName(m.FullPath);
                m.FileType = Path.GetExtension(m.FullPath).TrimStart('.').ToLowerInvariant();
            }
            else
            {
                try { m.FileName = doc.GetTitle(); }
                catch (Exception ex) { _log.Debug(ex, "Could not get document title"); m.FileName = "Unknown"; }
                m.FullPath = "Not Saved";
                m.FileType = "unknown";
            }
        }

        // ── 2. DOCUMENT TYPE ──
        private static void ExtractDocumentType(ModelDoc2 doc, DocumentMetadata m)
        {
            try
            {
                int t = doc.GetType();
                m.DocumentTypeConstant = t;
                m.DocumentTypeDescription = t == 1 ? "Part (.sldprt)" : t == 2 ? "Assembly (.sldasm)" : t == 3 ? "Drawing (.slddrw)" : "Unknown (" + t + ")";
            }
            catch (Exception ex) { _log.Debug(ex, "Could not determine document type"); m.DocumentTypeDescription = InferType(m.FileType); }
        }

        // ── 3. CONFIGURATIONS ──
        private static void ExtractConfigurations(ModelDoc2 doc, DocumentMetadata m)
        {
            ConfigurationManager cfgMgr = doc.ConfigurationManager;
            if (cfgMgr != null)
            {
                Configuration active = cfgMgr.ActiveConfiguration;
                if (active != null) m.ActiveConfiguration = active.Name;
            }

            object namesObj = doc.GetConfigurationNames();
            if (namesObj != null)
            {
                string[] names = (string[])namesObj;
                m.ConfigurationNames = new List<string>(names);
            }
        }

        // ── 4. MATERIAL ──
        private static void ExtractMaterial(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant != swDocPART) return;
            try
            {
                string cfg = m.ActiveConfiguration ?? "";
                string dbName;
                string mat = ((PartDoc)doc).GetMaterialPropertyName2(cfg, out dbName);
                if (!string.IsNullOrEmpty(mat)) m.MaterialName = mat;
            }
            catch (Exception ex) { _log.Debug(ex, "Could not extract material name"); }
        }

        // ── 5. MASS PROPERTIES ──
        private static void ExtractMassProperties(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant == swDocDRAWING) return;
            ModelDocExtension ext = doc.Extension;
            if (ext == null) return;

            int status;
            object result = ext.GetMassProperties2(1, out status, true);
            if (result == null || status != 0) return;

            double[] mp = result as double[];
            if (mp == null || mp.Length < 6) return;

            m.CenterOfGravity = new double[] { mp[0], mp[1], mp[2] };
            m.VolumeM3 = mp[3];
            m.SurfaceAreaM2 = mp[4];
            m.MassKg = mp[5];
            if (mp[3] > 0) m.DensityKgM3 = mp[5] / mp[3];
        }

        // ── 6. FEATURE TREE ──
        private static void ExtractFeatureTree(ModelDoc2 doc, DocumentMetadata m)
        {
            FeatureManager fm = doc.FeatureManager;
            if (fm == null) return;

            m.FeatureCount = fm.GetFeatureCount(true);
            object featObj = fm.GetFeatures(true);
            if (featObj == null) return;

            object[] features = (object[])featObj;
            m.FeatureNames = new List<string>();

            foreach (object f in features)
            {
                try
                {
                    Feature feat = (Feature)f;
                    string typeName = feat.GetTypeName2();
                    if (IsUserFeature(typeName))
                        m.FeatureNames.Add(string.Format("{0} [{1}]", feat.Name, typeName));
                }
                catch (Exception ex) { _log.Debug(ex, "Could not extract feature info"); }
            }
            m.FeatureCount = m.FeatureNames.Count;
        }

        private static bool IsUserFeature(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            string lower = t.ToLowerInvariant();
            return lower != "originfolder" && lower != "refplane" && lower != "refaxis"
                && lower != "originpoint" && lower != "histfolder" && lower != "historyfolderfeature"
                && lower != "detailcabinet" && lower != "materialdiffuse"
                && lower != "commentfolder" && lower != "selectionsetfolder"
                && lower != "sensorsfolder" && lower != "designbinder"
                && lower != "equationfolder" && lower != "docssfolder"
                && lower != "favorites" && lower != "comment";
        }

        // ── 7. CUSTOM PROPERTIES (document-level) ──
        private static void ExtractCustomProperties(ModelDoc2 doc, DocumentMetadata m)
        {
            m.CustomProperties = GetPropertiesForConfig(doc, "");
        }

        // ── 8. CONFIG-SPECIFIC PROPERTIES ──
        private static void ExtractConfigProperties(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.ConfigurationNames == null || m.ConfigurationNames.Count == 0) return;

            m.ConfigProperties = new Dictionary<string, Dictionary<string, string>>();
            foreach (string cfgName in m.ConfigurationNames)
            {
                var props = GetPropertiesForConfig(doc, cfgName);
                if (props != null && props.Count > 0)
                    m.ConfigProperties[cfgName] = props;
            }
        }

        private static Dictionary<string, string> GetPropertiesForConfig(ModelDoc2 doc, string configName)
        {
            ModelDocExtension ext = doc.Extension;
            if (ext == null) return null;

            CustomPropertyManager pm = ext.get_CustomPropertyManager(configName);
            if (pm == null) return null;

            object namesObj = null, typesObj = null, valsObj = null, resolvedObj = null, linkObj = null;
            int count = pm.GetAll3(ref namesObj, ref typesObj, ref valsObj, ref resolvedObj, ref linkObj);

            if (count <= 0 || namesObj == null) return null;

            string[] names = (string[])namesObj;
            string[] resolved = (string[])resolvedObj;
            var dict = new Dictionary<string, string>();

            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    dict[names[i]] = (resolved != null && i < resolved.Length) ? (resolved[i] ?? "") : "";
            }
            return dict;
        }

        // ── 9. BOUNDING BOX (reflection-safe) ──
        private static void ExtractBoundingBox(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant == swDocDRAWING) return;

            double[] box = null;
            Type comType = ((object)doc).GetType();

            try
            {
                object r = comType.InvokeMember("VisibleBox",
                    System.Reflection.BindingFlags.GetProperty, null, doc, null);
                box = r as double[];
            }
            catch (Exception ex) { _log.Debug(ex, "VisibleBox property not available, trying GetBox"); }

            if (box == null)
            {
                try
                {
                    object r = comType.InvokeMember("GetBox",
                        System.Reflection.BindingFlags.InvokeMethod, null, doc, null);
                    box = r as double[];
                }
                catch (Exception ex) { _log.Debug(ex, "GetBox method not available"); }
            }

            if (box != null && box.Length >= 6)
            {
                m.BoundingBoxLengthM = Math.Abs(box[3] - box[0]);
                m.BoundingBoxWidthM = Math.Abs(box[4] - box[1]);
                m.BoundingBoxHeightM = Math.Abs(box[5] - box[2]);
            }
        }

        // ── 10. BODY COUNT ──
        private static void ExtractBodyCount(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant != swDocPART) return;

            PartDoc part = (PartDoc)doc;

            // Solid bodies (swBodyType = 0 = swSolidBody)
            try
            {
                object solidBodies = part.GetBodies2(0, true);
                if (solidBodies != null) m.SolidBodyCount = ((object[])solidBodies).Length;
            }
            catch (Exception ex) { _log.Debug(ex, "Could not get solid body count"); }

            // Surface/sheet bodies (swBodyType = 1 = swSheetBody)
            try
            {
                object surfBodies = part.GetBodies2(1, true);
                if (surfBodies != null) m.SurfaceBodyCount = ((object[])surfBodies).Length;
            }
            catch (Exception ex) { _log.Debug(ex, "Could not get surface body count"); }
        }

        // ── 11. COMPONENT INFO (Assemblies) ──
        private static void ExtractComponentInfo(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant != swDocASSEMBLY) return;

            AssemblyDoc asm = (AssemblyDoc)doc;
            object comps = asm.GetComponents(true); // true = top-level only
            if (comps == null) return;

            object[] components = (object[])comps;
            m.ComponentCount = components.Length;
            m.ComponentPaths = new List<string>();

            foreach (object c in components)
            {
                try
                {
                    Component2 comp = (Component2)c;
                    string path = comp.GetPathName();
                    string name = comp.Name2;
                    m.ComponentPaths.Add(string.Format("{0} → {1}", name, path));
                }
                catch (Exception ex) { _log.Debug(ex, "Could not extract component info"); }
            }
        }

        // ── 12. NAMED DIMENSIONS ──
        private static void ExtractDimensions(ModelDoc2 doc, DocumentMetadata m)
        {
            m.Dimensions = new List<DimensionMetadata>();
            var seen = new HashSet<string>();

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
                                string fullName = dim.FullName;
                                if (!string.IsNullOrEmpty(fullName) && seen.Add(fullName))
                                {
                                    var meta = new DimensionMetadata
                                    {
                                        FullName = fullName,
                                        Value = dim.Value
                                    };

                                    try
                                    {
                                        DimensionTolerance tol = (DimensionTolerance)dim.Tolerance;
                                        if (tol != null)
                                        {
                                            meta.ToleranceType = tol.Type;
                                            double min = 0, max = 0;
                                            tol.GetMinValue2(out min);
                                            tol.GetMaxValue2(out max);
                                            meta.ToleranceMin = min;
                                            meta.ToleranceMax = max;
                                        }
                                    }
                                    catch (Exception ex) { _log.Debug(ex, "Could not extract tolerance for {DimensionName}", fullName); }

                                    m.Dimensions.Add(meta);
                                }
                            }
                        }
                        catch (Exception ex) { _log.Debug(ex, "Could not extract dimension from display dimension"); }

                        dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Could not iterate display dimensions for feature"); }

                feat = (Feature)feat.GetNextFeature();
            }
        }

        // ── 13. EQUATIONS ──
        private static void ExtractEquations(ModelDoc2 doc, DocumentMetadata m)
        {
            EquationMgr eqMgr = doc.GetEquationMgr();
            if (eqMgr == null) return;

            int count = eqMgr.GetCount();
            if (count <= 0) return;

            m.Equations = new List<string>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    string eq = eqMgr.get_Equation(i);
                    if (!string.IsNullOrEmpty(eq)) m.Equations.Add(eq);
                }
                catch (Exception ex) { _log.Debug(ex, "Could not extract equation at index {EquationIndex}", i); }
            }
        }

        // ── 13a. THICKNESS (Named dimension lookup) ──
        /// <summary>
        /// Walks the feature tree looking for a dimension whose name contains "Thickness".
        /// Captures the first match and stores its value (in model units, typically meters).
        /// </summary>
        private static void ExtractThickness(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant == swDocDRAWING) return;

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
                                string name = dim.FullName ?? "";
                                if (name.IndexOf("Thickness", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    m.ThicknessValue = dim.Value;
                                    return; // Found it — stop searching
                                }
                            }
                        }
                        catch (Exception ex) { _log.Debug(ex, "Error checking dimension for thickness"); }

                        dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Error iterating feature for thickness"); }

                feat = (Feature)feat.GetNextFeature();
            }
        }

        // ── 13b. HOLE CUT FEATURES ──
        /// <summary>
        /// Scans the feature tree for hole-related features (HoleWzd, HoleCut, Hole, etc.)
        /// and captures their name, type, and any associated dimension values.
        /// </summary>
        private static void ExtractHoleCutFeatures(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant == swDocDRAWING) return;

            m.HoleCutFeatures = new List<string>();

            Feature feat = (Feature)doc.FirstFeature();
            while (feat != null)
            {
                try
                {
                    string typeName = feat.GetTypeName2() ?? "";
                    string lower = typeName.ToLowerInvariant();

                    // Match hole-related feature types
                    if (lower == "holewzd" || lower == "holecut" || lower == "hole" ||
                        lower == "holechamfer" || lower == "holeseries" ||
                        lower.Contains("hole"))
                    {
                        // Collect dimensions attached to this hole feature
                        var dims = new List<string>();
                        DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                        while (dispDim != null)
                        {
                            try
                            {
                                Dimension dim = (Dimension)dispDim.GetDimension2(0);
                                if (dim != null)
                                {
                                    dims.Add(string.Format("{0}={1:F4}", dim.FullName, dim.Value));
                                }
                            }
                            catch (Exception ex) { _log.Debug(ex, "Could not extract hole dimension"); }

                            dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                        }

                        string entry = string.Format("{0} [{1}]", feat.Name, typeName);
                        if (dims.Count > 0)
                            entry += " | " + string.Join(", ", dims);

                        m.HoleCutFeatures.Add(entry);
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Could not process feature for hole cuts"); }

                feat = (Feature)feat.GetNextFeature();
            }

            // Clean up if nothing found
            if (m.HoleCutFeatures.Count == 0)
                m.HoleCutFeatures = null;
        }

        // ── 14. SUMMARY INFO ──
        private static void ExtractSummaryInfo(ModelDoc2 doc, DocumentMetadata m)
        {
            // SummaryInfo indices: 0=Title, 1=Subject, 2=Author, 3=Keywords,
            // 4=Comments, 5=LastSavedBy, 6=CreateDate, 7=LastSaveDate
            try { m.SummaryTitle = doc.get_SummaryInfo(0); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryTitle"); }
            try { m.SummarySubject = doc.get_SummaryInfo(1); } catch (Exception ex) { _log.Debug(ex, "Could not get SummarySubject"); }
            try { m.SummaryAuthor = doc.get_SummaryInfo(2); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryAuthor"); }
            try { m.SummaryKeywords = doc.get_SummaryInfo(3); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryKeywords"); }
            try { m.SummaryComments = doc.get_SummaryInfo(4); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryComments"); }
            try { m.SummaryLastSavedBy = doc.get_SummaryInfo(5); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryLastSavedBy"); }
            try { m.SummaryCreateDate = doc.get_SummaryInfo(6); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryCreateDate"); }
            try { m.SummaryLastSaveDate = doc.get_SummaryInfo(7); } catch (Exception ex) { _log.Debug(ex, "Could not get SummaryLastSaveDate"); }
        }

        // ── 15. REFERENCED DOCUMENTS ──
        private static void ExtractReferencedDocuments(ModelDoc2 doc, DocumentMetadata m)
        {
            object depsObj = doc.GetDependencies2(true, true, false);
            if (depsObj == null) return;

            string[] deps = (string[])depsObj;
            m.ReferencedDocuments = new List<string>();

            // GetDependencies2 returns pairs: [path, isVirtual, path, isVirtual, ...]
            // But in some versions it just returns paths
            for (int i = 0; i < deps.Length; i++)
            {
                string dep = deps[i];
                if (!string.IsNullOrEmpty(dep) && (dep.Contains("\\") || dep.Contains("/")))
                {
                    if (!m.ReferencedDocuments.Contains(dep))
                        m.ReferencedDocuments.Add(dep);
                }
            }
        }

        // ── 16. DRAWING INFO ──
        private static void ExtractDrawingInfo(ModelDoc2 doc, DocumentMetadata m)
        {
            if (m.DocumentTypeConstant != swDocDRAWING) return;

            DrawingDoc drw = (DrawingDoc)doc;

            // Sheet names
            try
            {
                object sheetsObj = drw.GetSheetNames();
                if (sheetsObj != null)
                {
                    string[] sheets = (string[])sheetsObj;
                    m.DrawingSheets = new List<string>(sheets);
                }
            }
            catch (Exception ex) { _log.Debug(ex, "Could not extract drawing sheet names"); }

            // Views
            try
            {
                m.DrawingViews = new List<string>();
                View view = (View)drw.GetFirstView();
                while (view != null)
                {
                    try
                    {
                        string vName = view.GetName2();
                        string refModel = "";
                        try { refModel = view.GetReferencedModelName(); }
                        catch (Exception ex) { _log.Debug(ex, "Could not get referenced model name for view"); }

                        string info = string.IsNullOrEmpty(refModel)
                            ? vName
                            : string.Format("{0} → {1}", vName, Path.GetFileName(refModel));
                        m.DrawingViews.Add(info);
                    }
                    catch (Exception ex) { _log.Debug(ex, "Could not extract view info"); }

                    view = (View)view.GetNextView();
                }
            }
            catch (Exception ex) { _log.Debug(ex, "Could not iterate drawing views"); }
        }

        // ── REFLECTION FALLBACK ──
        private static DocumentMetadata ExtractViaReflection(object obj)
        {
            var m = new DocumentMetadata { Timestamp = DateTime.UtcNow };
            Type t = obj.GetType();

            try { m.FullPath = t.InvokeMember("GetPathName", System.Reflection.BindingFlags.InvokeMethod, null, obj, null)?.ToString(); }
            catch (Exception ex) { _log.Debug(ex, "Reflection fallback: could not get path name"); }

            if (!string.IsNullOrEmpty(m.FullPath))
            {
                m.FileName = Path.GetFileName(m.FullPath);
                m.DirectoryPath = Path.GetDirectoryName(m.FullPath);
                m.FileType = Path.GetExtension(m.FullPath).TrimStart('.').ToLowerInvariant();
            }
            else
            {
                try { m.FileName = t.InvokeMember("GetTitle", System.Reflection.BindingFlags.InvokeMethod, null, obj, null)?.ToString() ?? "Unknown"; }
                catch (Exception ex) { _log.Debug(ex, "Reflection fallback: could not get title"); m.FileName = "Unknown"; }
            }

            try
            {
                int docType = Convert.ToInt32(t.InvokeMember("Ge" + "tType", System.Reflection.BindingFlags.InvokeMethod, null, obj, null));
                m.DocumentTypeConstant = docType;
                m.DocumentTypeDescription = docType == 1 ? "Part" : docType == 2 ? "Assembly" : docType == 3 ? "Drawing" : "Unknown";
            }
            catch (Exception ex) { _log.Debug(ex, "Reflection fallback: could not determine document type"); }

            return m;
        }

        // ── HELPERS ──
        private static string InferType(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return "Unknown";
            switch (ext.ToLowerInvariant())
            {
                case "sldprt": return "Part (.sldprt)";
                case "sldasm": return "Assembly (.sldasm)";
                case "slddrw": return "Drawing (.slddrw)";
                default: return "Unknown (" + ext + ")";
            }
        }
    }
}
