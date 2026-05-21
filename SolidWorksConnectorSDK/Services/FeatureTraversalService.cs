using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;

namespace SolidWorksConnectorSDK.Services
{
    public class FeatureTraversalService
    {
        private readonly ILogger<FeatureTraversalService> _logger;
        private readonly ComLifetimeManager _comManager;

        public FeatureTraversalService(ILogger<FeatureTraversalService> logger, ComLifetimeManager comManager)
        {
            _logger = logger;
            _comManager = comManager;
        }

        public FeatureCache BuildCache(ModelDoc2 doc)
        {
            var cache = new FeatureCache();
            if (doc == null) return cache;

            _logger.LogInformation("Building feature cache for {ModelTitle}", doc.GetTitle());

            Feature feat = null;
            try
            {
                feat = (Feature)doc.FirstFeature();
                while (feat != null)
                {
                    try
                    {
                        string featName = feat.Name ?? "(unnamed)";
                        string typeName = (feat.GetTypeName2() ?? "").ToLowerInvariant();

                        if (!cache.FeatureByName.ContainsKey(featName))
                        {
                            cache.FeatureByName[featName] = feat;
                        }

                        if (typeName.Contains("hole"))
                        {
                            cache.HoleFeatures.Add(featName);
                        }

                        // Extract Dimensions
                        DisplayDimension dispDim = null;
                        try
                        {
                            dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                            while (dispDim != null)
                            {
                                Dimension dim = null;
                                try
                                {
                                    dim = (Dimension)dispDim.GetDimension2(0);
                                    if (dim != null && !string.IsNullOrEmpty(dim.FullName))
                                    {
                                        cache.DimensionByName[dim.FullName] = dim;
                                    }
                                }
                                catch (Exception ex) { _logger.LogDebug(ex, "Error getting dimension from display dimension"); }
                                finally
                                {
                                    if (dim == null) _comManager.ReleaseComObject(dispDim);
                                }
                                
                                DisplayDimension nextDispDim = null;
                                try
                                {
                                    nextDispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                                }
                                catch (Exception ex) { _logger.LogDebug(ex, "Error getting next display dimension"); }
                                finally
                                {
                                    _comManager.ReleaseComObject(dispDim);
                                    dispDim = nextDispDim;
                                }
                            }
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "Error iterating display dimensions for feature"); }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error processing feature during cache build"); }

                    Feature nextFeat = null;
                    try
                    {
                        nextFeat = (Feature)feat.GetNextFeature();
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error getting next feature"); }
                    finally
                    {
                        if (nextFeat != null)
                        {
                            _comManager.ReleaseComObject(feat);
                        }
                        feat = nextFeat;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while building feature cache.");
            }

            _logger.LogInformation("Cached {FeatCount} features and {DimCount} dimensions.", 
                cache.FeatureByName.Count, cache.DimensionByName.Count);
                
            return cache;
        }
    }

    public class FeatureCache : IDisposable
    {
        public Dictionary<string, Feature> FeatureByName { get; } = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dimension> DimensionByName { get; } = new Dictionary<string, Dimension>(StringComparer.OrdinalIgnoreCase);
        public List<string> HoleFeatures { get; } = new List<string>();

        public void Dispose()
        {
            // Do NOT release COM objects here if they are still attached to the live document, 
            // but if we wrapped them, we'd release them. For now, garbage collection of 
            // the dictionary clears the RCW references eventually, but the document close 
            // is the main cleanup. 
            FeatureByName.Clear();
            DimensionByName.Clear();
            HoleFeatures.Clear();
        }
    }
}
