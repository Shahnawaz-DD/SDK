using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksConnectorSDK.Models;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Custom delegate for save event callbacks.
    /// </summary>
    public delegate void SaveEventHandler(object sender, SaveEventData saveData);

    /// <summary>
    /// Custom delegate for general document event callbacks.
    /// </summary>
    public delegate void DocumentEventHandler(object sender, DocumentMetadata metadata, EventType eventType);

    /// <summary>
    /// Core connector managing SolidWorks document-level event subscriptions.
    /// 
    /// Architecture:
    /// ─────────────────────────────────────────────────────────────
    /// Uses PER-DOCUMENT event sinks (PartDoc / AssemblyDoc / DrawingDoc)
    /// for save events — the correct SolidWorks API pattern.
    /// 
    /// ❌ Does NOT use DSldWorksEvents_SldWorksEvents for save events.
    /// ✅ Uses FileSaveNotify on each document's typed interface.
    /// 
    /// App-level events (cast to SldWorks) are used ONLY for detecting
    /// document open/new to auto-attach per-document trackers.
    ///
    /// Event Flow (Save):
    ///   1. Document opened → DocumentEventTracker created, attached
    ///   2. User saves      → Tracker fires FileSaveNotify (pre-save)
    ///   3. Save completes  → Tracker fires FileSavePostNotify (post-save)
    ///   4. Document closed  → Tracker fires DestroyNotify2, detaches
    ///
    /// Duplicate Suppression:
    ///   Path + time threshold (500ms) prevents duplicate post-save events.
    ///
    /// Thread Safety:
    ///   All state mutations guarded by _lockObject.
    ///   ConcurrentDictionary for pending saves and tracked documents.
    /// ─────────────────────────────────────────────────────────────
    /// </summary>
    public class SolidWorksConnector : IDisposable
    {
        // ── Logging ──
        private static readonly ILogger _log = Log.ForContext<SolidWorksConnector>();

        // ── COM Application Reference ──
        private ISldWorks _swApp;

        // ── State Management ──
        private bool _isEventSubscribed;
        private bool _isDisposed;
        private readonly object _lockObject = new object();
        private int _eventCount;

        // ── Duplicate Event Suppression ──
        private string _lastEventPath = string.Empty;
        private DateTime _lastEventTime = DateTime.MinValue;
        private static readonly TimeSpan DuplicateThreshold = TimeSpan.FromMilliseconds(500);

        // ── API Client ──
        private readonly ApiClient _apiClient = new ApiClient();

        // ── Per-Document Trackers ──
        private readonly ConcurrentDictionary<string, DocumentEventTracker> _trackedDocuments
            = new ConcurrentDictionary<string, DocumentEventTracker>(StringComparer.OrdinalIgnoreCase);

        // ── Pending Save Operations (correlate pre→post) ──
        private readonly ConcurrentDictionary<string, SaveEventData> _pendingSaves
            = new ConcurrentDictionary<string, SaveEventData>(StringComparer.OrdinalIgnoreCase);

        // ── Public Events ──
        public event SaveEventHandler DocumentSaved;
        public event SaveEventHandler DocumentSaving;
        public event DocumentEventHandler DocumentEventOccurred;

        // ══════════════════════════════════════════════
        // CONSTRUCTOR
        // ══════════════════════════════════════════════

        public SolidWorksConnector(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _log.Information("Initialized with SolidWorks Application object");
        }

        // ══════════════════════════════════════════════
        // EVENT SUBSCRIPTION
        // ══════════════════════════════════════════════

        /// <summary>
        /// Subscribes to app-level document lifecycle events and attaches
        /// per-document trackers to all currently open documents.
        /// </summary>
        public void SubscribeToEvents()
        {
            lock (_lockObject)
            {
                if (_isEventSubscribed)
                {
                    _log.Warning("Events already subscribed");
                    return;
                }
                try
                {
                    // App-level events for document lifecycle detection ONLY
                    ((SolidWorks.Interop.sldworks.SldWorks)_swApp).ActiveDocChangeNotify += OnActiveDocChangeNotify;
                    ((SolidWorks.Interop.sldworks.SldWorks)_swApp).FileNewNotify2 += OnFileNewNotify2;
                    ((SolidWorks.Interop.sldworks.SldWorks)_swApp).FileOpenPostNotify += OnFileOpenPostNotify;

                    // Attach trackers to all currently open documents
                    AttachToAllOpenDocuments();

                    _isEventSubscribed = true;

                    _log.Information("Subscribed to events: ActiveDocChangeNotify, FileNewNotify2, FileOpenPostNotify, Per-document save/destroy. Documents tracked: {TrackedCount}", _trackedDocuments.Count);
                }
                catch (COMException comEx)
                {
                    _log.Error(comEx, "COM error subscribing to events (0x{ErrorCode:X8})", comEx.ErrorCode);
                    throw;
                }
                catch (InvalidCastException castEx)
                {
                    _log.Error(castEx, "Cast error subscribing to events");
                    throw;
                }
            }
        }

        /// <summary>
        /// Unsubscribes from all events and detaches all document trackers.
        /// </summary>
        public void UnsubscribeFromEvents()
        {
            lock (_lockObject)
            {
                if (!_isEventSubscribed) return;
                try
                {
                    // Detach all per-document trackers
                    foreach (var kvp in _trackedDocuments)
                    {
                        try { kvp.Value.Detach(); }
                        catch (Exception ex) { _log.Warning(ex, "Error detaching tracker for {DocumentKey}", System.IO.Path.GetFileName(kvp.Key)); }
                    }
                    _trackedDocuments.Clear();

                    // Unhook app-level events
                    try
                    {
                        ((SolidWorks.Interop.sldworks.SldWorks)_swApp).ActiveDocChangeNotify -= OnActiveDocChangeNotify;
                        ((SolidWorks.Interop.sldworks.SldWorks)_swApp).FileNewNotify2 -= OnFileNewNotify2;
                        ((SolidWorks.Interop.sldworks.SldWorks)_swApp).FileOpenPostNotify -= OnFileOpenPostNotify;
                    }
                    catch (COMException comEx) { _log.Warning(comEx, "COM error unhooking app-level events (0x{ErrorCode:X8})", comEx.ErrorCode); }
                    catch (InvalidComObjectException icoEx) { _log.Warning(icoEx, "COM object already released during unsubscribe"); }

                    _pendingSaves.Clear();
                    _isEventSubscribed = false;

                    _log.Information("Unsubscribed from all events. Total events captured: {EventCount}", _eventCount);
                }
                catch (COMException comEx)
                {
                    _log.Warning(comEx, "COM error during unsubscribe (0x{ErrorCode:X8})", comEx.ErrorCode);
                    _isEventSubscribed = false;
                }
            }
        }

        // ══════════════════════════════════════════════
        // PER-DOCUMENT TRACKER MANAGEMENT
        // ══════════════════════════════════════════════

        /// <summary>
        /// Scans all open SolidWorks documents and creates a tracker for each.
        /// Called once at startup to cover documents that were already open.
        /// </summary>
        private void AttachToAllOpenDocuments()
        {
            try
            {
                var enumerator = (EnumDocuments2)_swApp.EnumDocuments2();
                if (enumerator == null) return;

                ModelDoc2 doc;
                int fetched = 0;
                do
                {
                    enumerator.Next(1, out doc, ref fetched);
                    if (fetched > 0 && doc != null)
                    {
                        AttachTrackerToDocument(doc);
                    }
                } while (fetched > 0);
            }
            catch (COMException comEx)
            {
                _log.Warning(comEx, "Could not enumerate open documents (0x{ErrorCode:X8})", comEx.ErrorCode);
            }
        }

        /// <summary>
        /// Creates and registers a DocumentEventTracker for the given document.
        /// Skips if the document is already tracked or is an unsupported type.
        /// </summary>
        private void AttachTrackerToDocument(ModelDoc2 model)
        {
            if (model == null) return;

            string key = GetDocumentKey(model);
            if (string.IsNullOrEmpty(key)) return;

            // Already tracked — skip
            if (_trackedDocuments.ContainsKey(key)) return;

            int docType = model.GetType();
            if (docType < 1 || docType > 3)
            {
                // Only Part(1), Assembly(2), Drawing(3) support document-level events
                return;
            }

            try
            {
                var tracker = new DocumentEventTracker(
                    model,
                    key,
                    onPreSave: OnDocumentPreSave,
                    onPostSave: OnDocumentPostSave,
                    onDestroy: OnDocumentDestroy);

                if (_trackedDocuments.TryAdd(key, tracker))
                {
                    _log.Debug("Tracker attached: {DocumentName}", System.IO.Path.GetFileName(key));
                }
                else
                {
                    // Race condition — another thread added it first
                    tracker.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not attach tracker to {DocumentName}", System.IO.Path.GetFileName(key));
            }
        }

        /// <summary>
        /// Generates a stable key for a document (normalized path, or title for unsaved).
        /// </summary>
        private static string GetDocumentKey(ModelDoc2 model)
        {
            try
            {
                string path = model.GetPathName();
                if (!string.IsNullOrEmpty(path))
                    return path.Trim();

                // Unsaved document — use title as key
                string title = model.GetTitle();
                return !string.IsNullOrEmpty(title) ? "UNSAVED:" + title : null;
            }
            catch (COMException)
            {
                return null;
            }
        }

        // ══════════════════════════════════════════════
        // TRACKER CALLBACKS (Per-Document Save Events)
        // ══════════════════════════════════════════════

        /// <summary>
        /// Called by DocumentEventTracker when a document fires FileSaveNotify or FileSaveAsNotify2.
        /// </summary>
        private void OnDocumentPreSave(string fileName, bool isSaveAs, string documentKey, object document)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            var eventType = isSaveAs ? EventType.FileSaveAsStarting : EventType.FileSaveStarting;
            var saveData = new SaveEventData
            {
                SaveOperationId = Guid.NewGuid(),
                RawFileName = fileName,
                StartTimeUtc = DateTime.UtcNow,
                IsSaveAs = isSaveAs,
                EventType = eventType,
                IsCompleted = false
            };

            // Store for correlation with post-save
            string normalizedPath = fileName.Trim();
            _pendingSaves[normalizedPath] = saveData;

            _log.Information("{SaveType}: {FileName}",
                isSaveAs ? "Saving As" : "Saving",
                System.IO.Path.GetFileName(fileName));

            // 🚀 Offload extraction and sending to a background task to ELIMINATE lag
            Task.Run(async () =>
            {
                try
                {
                    // Let SolidWorks finish writing to disk and unfreeze its main thread
                    // This prevents cross-thread COM deadlocks during the save operation.
                    await Task.Delay(2000).ConfigureAwait(false);

                    // Extract metadata from the CORRECT document directly without searching
                    _log.Debug("Background metadata extraction started...");

                    // Resolve the deadlock: Get a fresh COM proxy strictly for this background MTA thread!
                    var bgSwApp = (ISldWorks)System.Runtime.InteropServices.Marshal.GetActiveObject("SldWorks.Application");
                    var activeDoc = (ModelDoc2)bgSwApp.ActiveDoc;

                    if (activeDoc != null)
                    {
                        saveData.Metadata = DataExtractor.ExtractMetadata(activeDoc);
                    }
                    
                    if (saveData.Metadata != null)
                    {
                        // Save to JSON file
                        string jsonPath = JsonExporter.SaveEventToJson(saveData);
                        if (jsonPath != null)
                        {
                            _log.Information("JSON saved: {JsonPath}", jsonPath);
                        }

                        // [ApiClient] Send to Cloud API
                        await _apiClient.SendMetadataAsync(saveData).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Background extraction failed");
                }
            });

            RaiseSavingEvent(saveData);
        }

        /// <summary>
        /// Called by DocumentEventTracker when a document fires FileSavePostNotify.
        /// </summary>
        private void OnDocumentPostSave(int saveType, string fileName, string documentKey)
        {
            if (string.IsNullOrEmpty(fileName)) return;
            if (IsDuplicateEvent(fileName)) return;

            _eventCount++;
            string normalizedPath = fileName.Trim();

            // Correlate with pending pre-save
            SaveEventData saveData;
            if (!_pendingSaves.TryRemove(normalizedPath, out saveData))
            {
                saveData = new SaveEventData
                {
                    SaveOperationId = Guid.NewGuid(),
                    RawFileName = fileName,
                    StartTimeUtc = DateTime.UtcNow,
                    IsSaveAs = (saveType == 2)
                };
            }

            // Complete the save operation
            saveData.EndTimeUtc = DateTime.UtcNow;
            saveData.IsCompleted = true;
            saveData.EventSequenceNumber = _eventCount;
            saveData.EventType = saveData.IsSaveAs ? EventType.FileSavedAs : EventType.FileSaved;

            // Log completion
            _log.Debug("Event #{EventCount} — {EventType} completed", _eventCount, saveData.EventType);

            RaiseSavedEvent(saveData);

            // If Save-As changed the path, re-key the tracker
            if (saveData.IsSaveAs)
            {
                RekeyTrackerAfterSaveAs(documentKey, normalizedPath);
            }
        }

        /// <summary>
        /// Called by DocumentEventTracker when DestroyNotify2 fires (document closing).
        /// </summary>
        private void OnDocumentDestroy(string documentKey)
        {
            DocumentEventTracker removed;
            if (_trackedDocuments.TryRemove(documentKey, out removed))
            {
                _log.Information("Tracker detached: {DocumentName}", System.IO.Path.GetFileName(documentKey));

                // Raise close event
                var metadata = new DocumentMetadata
                {
                    FileName = System.IO.Path.GetFileName(documentKey),
                    FullPath = documentKey,
                    Timestamp = DateTime.UtcNow
                };
                _eventCount++;
                RaiseDocumentEvent(metadata, EventType.FileClosed);
            }
        }

        /// <summary>
        /// After a Save-As, the file path changes. Re-key the tracker dictionary entry.
        /// </summary>
        private void RekeyTrackerAfterSaveAs(string oldKey, string newPath)
        {
            if (string.Equals(oldKey, newPath, StringComparison.OrdinalIgnoreCase)) return;

            DocumentEventTracker tracker;
            if (_trackedDocuments.TryRemove(oldKey, out tracker))
            {
                _trackedDocuments[newPath] = tracker;
                _log.Information("Tracker re-keyed: {OldName} → {NewName}",
                    System.IO.Path.GetFileName(oldKey), System.IO.Path.GetFileName(newPath));
            }
        }

        // ══════════════════════════════════════════════
        // APP-LEVEL DOCUMENT LIFECYCLE HANDLERS
        // ══════════════════════════════════════════════

        private int OnActiveDocChangeNotify()
        {
            try
            {
                ModelDoc2 activeDoc = (ModelDoc2)_swApp.ActiveDoc;
                if (activeDoc == null) return 0;

                // Auto-attach tracker if not yet tracked
                AttachTrackerToDocument(activeDoc);

                DocumentMetadata metadata = DataExtractor.ExtractMetadata(activeDoc);
                if (metadata == null || IsDuplicateEvent(metadata.FullPath)) return 0;
                _lastEventPath = metadata.FullPath ?? string.Empty;
                _lastEventTime = DateTime.UtcNow;
                _eventCount++;

                _log.Debug("Event #{EventCount} - ActiveDocChange: {FileName}", _eventCount, metadata.FileName);

                RaiseDocumentEvent(metadata, EventType.ActiveDocumentChanged);
            }
            catch (Exception ex) { LogError("ActiveDocChangeNotify", ex); }
            return 0;
        }

        private int OnFileNewNotify2(object newDoc, int docType, string templateName)
        {
            try
            {
                _log.Information("New doc created (Type: {DocType})", GetDocTypeShortName(docType));

                // Auto-attach tracker to the new document
                ModelDoc2 model = newDoc as ModelDoc2;
                if (model == null) model = (ModelDoc2)_swApp.ActiveDoc;
                if (model != null)
                {
                    AttachTrackerToDocument(model);
                    DocumentMetadata metadata = DataExtractor.ExtractMetadata(model);
                    if (metadata != null)
                    {
                        _eventCount++;
                        _log.Debug("New document metadata extracted: {FileName}", metadata.FileName);
                        RaiseDocumentEvent(metadata, EventType.FileNew);
                    }
                }
            }
            catch (Exception ex) { LogError("FileNewNotify2", ex); }
            return 0;
        }

        private int OnFileOpenPostNotify(string fileName)
        {
            try
            {
                _log.Information("File opened: {FileName}", fileName ?? "Unknown");

                // Auto-attach tracker to the opened document
                ModelDoc2 activeDoc = (ModelDoc2)_swApp.ActiveDoc;
                if (activeDoc != null)
                {
                    AttachTrackerToDocument(activeDoc);
                    DocumentMetadata metadata = DataExtractor.ExtractMetadata(activeDoc);
                    if (metadata != null)
                    {
                        _eventCount++;
                        _log.Debug("Opened document metadata extracted: {FileName}", metadata.FileName);
                        RaiseDocumentEvent(metadata, EventType.FileOpened);
                    }
                }
            }
            catch (Exception ex) { LogError("FileOpenPostNotify", ex); }
            return 0;
        }

        // ══════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════

        public DocumentMetadata GetActiveDocumentMetadata()
        {
            try
            {
                ModelDoc2 activeDoc = (ModelDoc2)_swApp.ActiveDoc;
                return activeDoc != null ? DataExtractor.ExtractMetadata(activeDoc) : null;
            }
            catch (COMException) { return null; }
        }

        public int TotalEventCount { get { lock (_lockObject) { return _eventCount; } } }
        public int PendingSaveCount { get { return _pendingSaves.Count; } }
        public int TrackedDocumentCount { get { return _trackedDocuments.Count; } }

        // ══════════════════════════════════════════════
        // INTERNAL HELPERS
        // ══════════════════════════════════════════════

        private DocumentMetadata ExtractCurrentDocumentMetadata()
        {
            try
            {
                ModelDoc2 activeDoc = (ModelDoc2)_swApp.ActiveDoc;
                return activeDoc != null ? DataExtractor.ExtractMetadata(activeDoc) : null;
            }
            catch (COMException comEx)
            {
                _log.Warning(comEx, "Could not extract post-save metadata (0x{ErrorCode:X8})", comEx.ErrorCode);
                return null;
            }
        }

        /// <summary>
        /// Finds the ModelDoc2 matching the documentKey and extracts metadata from it.
        /// Falls back to ActiveDoc if the document can't be found by key.
        /// This ensures we extract from the SAVED document, not just whatever is active.
        /// </summary>
        private DocumentMetadata ExtractMetadataByKey(string documentKey)
        {
            try
            {
                // Try to find the document by iterating all open docs
                ModelDoc2 targetDoc = null;

                try
                {
                    var enumerator = (EnumDocuments2)_swApp.EnumDocuments2();
                    if (enumerator != null)
                    {
                        ModelDoc2 doc;
                        int fetched = 0;
                        do
                        {
                            enumerator.Next(1, out doc, ref fetched);
                            if (fetched > 0 && doc != null)
                            {
                                string path = doc.GetPathName();
                                if (!string.IsNullOrEmpty(path) &&
                                    string.Equals(path.Trim(), documentKey, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetDoc = doc;
                                    break;
                                }
                            }
                        } while (fetched > 0);
                    }
                }
                catch (COMException comEx) { _log.Debug("COM error enumerating docs for key lookup (0x{ErrorCode:X8})", comEx.ErrorCode); }

                // Fallback to active doc if not found by key
                if (targetDoc == null)
                {
                    targetDoc = (ModelDoc2)_swApp.ActiveDoc;
                }

                return targetDoc != null ? DataExtractor.ExtractMetadata(targetDoc) : null;
            }
            catch (COMException comEx)
            {
                _log.Warning(comEx, "Could not extract metadata (0x{ErrorCode:X8})", comEx.ErrorCode);
                return null;
            }
        }

        private bool IsDuplicateEvent(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath) || string.IsNullOrEmpty(_lastEventPath)) return false;
            return string.Equals(currentPath, _lastEventPath, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _lastEventTime) < DuplicateThreshold;
        }

        private string GetDocTypeShortName(int docType)
        {
            switch (docType)
            {
                case 1: return "Part";
                case 2: return "Assembly";
                case 3: return "Drawing";
                default: return string.Format("Unknown({0})", docType);
            }
        }

        // ══════════════════════════════════════════════
        // EVENT RAISING (Safe Invocation)
        // ══════════════════════════════════════════════

        private void RaiseSavingEvent(SaveEventData saveData)
        {
            var handler = DocumentSaving;
            if (handler == null) return;
            foreach (SaveEventHandler subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, saveData); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "DocumentSaving subscriber threw an exception");
                }
            }
        }

        private void RaiseSavedEvent(SaveEventData saveData)
        {
            var handler = DocumentSaved;
            if (handler == null) return;
            foreach (SaveEventHandler subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, saveData); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "DocumentSaved subscriber threw an exception");
                }
            }
        }

        private void RaiseDocumentEvent(DocumentMetadata metadata, EventType eventType)
        {
            var handler = DocumentEventOccurred;
            if (handler == null) return;
            foreach (DocumentEventHandler subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, metadata, eventType); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "DocumentEvent subscriber threw an exception");
                }
            }
        }

        // ══════════════════════════════════════════════
        // LOGGING
        // ══════════════════════════════════════════════

        private void LogError(string eventName, Exception ex)
        {
            if (ex is COMException comEx)
                _log.Error(comEx, "COM error in {EventName} (0x{ErrorCode:X8})", eventName, comEx.ErrorCode);
            else
                _log.Error(ex, "Error in {EventName}", eventName);
        }

        // ══════════════════════════════════════════════
        // DISPOSE PATTERN
        // ══════════════════════════════════════════════

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                UnsubscribeFromEvents();
                _pendingSaves.Clear();
            }
            if (_swApp != null && Marshal.IsComObject(_swApp))
            {
                try { Marshal.ReleaseComObject(_swApp); }
                catch (Exception ex) { _log.Warning(ex, "Error releasing COM object during dispose"); }
            }
            _swApp = null;
            _isDisposed = true;
            _log.Information("Disposed. All COM references released");
        }

        ~SolidWorksConnector() { Dispose(false); }
    }
}
