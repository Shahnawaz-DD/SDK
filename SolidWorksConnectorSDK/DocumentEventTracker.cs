using System;
using System.Runtime.InteropServices;
using Serilog;
using SolidWorks.Interop.sldworks;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Wraps per-document COM event subscriptions for a single SolidWorks document.
    /// 
    /// Architecture:
    /// ─────────────────────────────────────────────────────────────────────
    /// SolidWorks exposes document-level events through type-specific interfaces:
    ///   - PartDoc     → DPartDocEvents
    ///   - AssemblyDoc → DAssemblyDocEvents
    ///   - DrawingDoc  → DDrawingDocEvents
    ///
    /// Each interface exposes:
    ///   - FileSaveNotify       (pre-save for regular Save)
    ///   - FileSaveAsNotify2    (pre-save for Save-As)
    ///   - FileSavePostNotify   (post-save for both Save and Save-As)
    ///   - DestroyNotify2       (document closing — cleanup hook)
    ///
    /// This tracker:
    ///   1. Holds a strong reference to the typed COM object (PartDoc, etc.)
    ///      to prevent GC from releasing the event sink prematurely.
    ///   2. Subscribes to the four events above on construction.
    ///   3. Forwards events to the parent SolidWorksConnector via callbacks.
    ///   4. Cleanly unsubscribes on Detach() or when the document is destroyed.
    ///
    /// Thread Safety:
    ///   All state transitions are guarded by _lock. Double-detach is safe.
    ///
    /// COM Safety:
    ///   - We hold only the typed doc reference (PartDoc/AssemblyDoc/DrawingDoc).
    ///   - We do NOT release the COM object here — that is the caller's
    ///     responsibility, since the ModelDoc2 may still be in use by SW.
    ///   - DestroyNotify2 fires BEFORE the COM reference is invalidated,
    ///     so we can safely unsubscribe in that handler.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class DocumentEventTracker : IDisposable
    {
        // ──────────────────────────────────────────────
        // Logging
        // ──────────────────────────────────────────────
        private static readonly ILogger _log = Log.ForContext<DocumentEventTracker>();

        // ──────────────────────────────────────────────
        // Callbacks to the parent connector
        // ──────────────────────────────────────────────

        /// <summary>Signature for pre-save callbacks (Save or Save-As).</summary>
        internal delegate void PreSaveCallback(string fileName, bool isSaveAs, string documentKey, object document);

        /// <summary>Signature for post-save callbacks.</summary>
        internal delegate void PostSaveCallback(int saveType, string fileName, string documentKey);

        /// <summary>Signature for document-closing callbacks.</summary>
        internal delegate void DestroyCallback(string documentKey);

        // ──────────────────────────────────────────────
        // Instance State
        // ──────────────────────────────────────────────

        private readonly string _documentKey;
        private readonly int _documentType;
        private readonly object _lock = new object();
        private bool _isAttached;
        private bool _isDisposed;

        // Strong references to the typed COM doc — prevents GC from
        // releasing the event sink (the #1 cause of silent event loss).
        private PartDoc _partDoc;
        private AssemblyDoc _assemblyDoc;
        private DrawingDoc _drawingDoc;

        // Parent callbacks
        private readonly PreSaveCallback _onPreSave;
        private readonly PostSaveCallback _onPostSave;
        private readonly DestroyCallback _onDestroy;

        // ──────────────────────────────────────────────
        // Constants (swDocumentTypes_e)
        // ──────────────────────────────────────────────
        private const int swDocPART = 1;
        private const int swDocASSEMBLY = 2;
        private const int swDocDRAWING = 3;

        // ──────────────────────────────────────────────
        // Constructor
        // ──────────────────────────────────────────────

        /// <summary>
        /// Creates a tracker and immediately subscribes to document-level events.
        /// </summary>
        /// <param name="model">The ModelDoc2 COM object to track. Must not be null.</param>
        /// <param name="documentKey">
        ///   Unique key for this document (normalized file path or title for unsaved docs).
        ///   Used for dictionary lookup and event correlation.
        /// </param>
        /// <param name="onPreSave">Callback for FileSaveNotify / FileSaveAsNotify2.</param>
        /// <param name="onPostSave">Callback for FileSavePostNotify.</param>
        /// <param name="onDestroy">Callback for DestroyNotify2 (document closing).</param>
        /// <exception cref="ArgumentNullException">If model or callbacks are null.</exception>
        /// <exception cref="InvalidOperationException">If the document type is unsupported.</exception>
        internal DocumentEventTracker(
            ModelDoc2 model,
            string documentKey,
            PreSaveCallback onPreSave,
            PostSaveCallback onPostSave,
            DestroyCallback onDestroy)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            _documentKey = documentKey ?? throw new ArgumentNullException(nameof(documentKey));
            _onPreSave = onPreSave ?? throw new ArgumentNullException(nameof(onPreSave));
            _onPostSave = onPostSave ?? throw new ArgumentNullException(nameof(onPostSave));
            _onDestroy = onDestroy ?? throw new ArgumentNullException(nameof(onDestroy));

            _documentType = model.GetType();
            Attach(model);
        }

        /// <summary>
        /// Gets the document key (normalized path or title) for this tracker.
        /// </summary>
        internal string DocumentKey { get { return _documentKey; } }

        /// <summary>
        /// Gets whether this tracker is currently attached to document events.
        /// </summary>
        internal bool IsAttached
        {
            get { lock (_lock) { return _isAttached; } }
        }

        // ══════════════════════════════════════════════
        // ATTACH / DETACH
        // ══════════════════════════════════════════════

        /// <summary>
        /// Subscribes to the document-level COM events based on document type.
        /// </summary>
        private void Attach(ModelDoc2 model)
        {
            lock (_lock)
            {
                if (_isAttached) return;

                switch (_documentType)
                {
                    case swDocPART:
                        _partDoc = (PartDoc)model;
                        _partDoc.FileSaveNotify += OnFileSaveNotify;
                        _partDoc.FileSaveAsNotify2 += OnFileSaveAsNotify2;
                        _partDoc.FileSavePostNotify += OnFileSavePostNotify;
                        _partDoc.DestroyNotify2 += OnDestroyNotify2;
                        break;

                    case swDocASSEMBLY:
                        _assemblyDoc = (AssemblyDoc)model;
                        _assemblyDoc.FileSaveNotify += OnFileSaveNotify;
                        _assemblyDoc.FileSaveAsNotify2 += OnFileSaveAsNotify2;
                        _assemblyDoc.FileSavePostNotify += OnFileSavePostNotify;
                        _assemblyDoc.DestroyNotify2 += OnDestroyNotify2;
                        break;

                    case swDocDRAWING:
                        _drawingDoc = (DrawingDoc)model;
                        _drawingDoc.FileSaveNotify += OnFileSaveNotify;
                        _drawingDoc.FileSaveAsNotify2 += OnFileSaveAsNotify2;
                        _drawingDoc.FileSavePostNotify += OnFileSavePostNotify;
                        _drawingDoc.DestroyNotify2 += OnDestroyNotify2;
                        break;

                    default:
                        throw new InvalidOperationException(
                            string.Format("Unsupported document type: {0}. Only Part (1), Assembly (2), and Drawing (3) are supported.", _documentType));
                }

                _isAttached = true;
            }
        }

        /// <summary>
        /// Unsubscribes from all document-level COM events.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        internal void Detach()
        {
            lock (_lock)
            {
                if (!_isAttached) return;

                try
                {
                    switch (_documentType)
                    {
                        case swDocPART:
                            if (_partDoc != null)
                            {
                                _partDoc.FileSaveNotify -= OnFileSaveNotify;
                                _partDoc.FileSaveAsNotify2 -= OnFileSaveAsNotify2;
                                _partDoc.FileSavePostNotify -= OnFileSavePostNotify;
                                _partDoc.DestroyNotify2 -= OnDestroyNotify2;
                            }
                            break;

                        case swDocASSEMBLY:
                            if (_assemblyDoc != null)
                            {
                                _assemblyDoc.FileSaveNotify -= OnFileSaveNotify;
                                _assemblyDoc.FileSaveAsNotify2 -= OnFileSaveAsNotify2;
                                _assemblyDoc.FileSavePostNotify -= OnFileSavePostNotify;
                                _assemblyDoc.DestroyNotify2 -= OnDestroyNotify2;
                            }
                            break;

                        case swDocDRAWING:
                            if (_drawingDoc != null)
                            {
                                _drawingDoc.FileSaveNotify -= OnFileSaveNotify;
                                _drawingDoc.FileSaveAsNotify2 -= OnFileSaveAsNotify2;
                                _drawingDoc.FileSavePostNotify -= OnFileSavePostNotify;
                                _drawingDoc.DestroyNotify2 -= OnDestroyNotify2;
                            }
                            break;
                    }
                }
                catch (COMException comEx)
                {
                    // The COM object may already be disconnected if SW is shutting down.
                    // This is expected and safe to swallow.
                    _log.Warning(comEx, "COM error during detach for {DocumentName} (0x{ErrorCode:X8})",
                        System.IO.Path.GetFileName(_documentKey), comEx.ErrorCode);
                }
                catch (InvalidComObjectException icoEx)
                {
                    // COM object was already released — nothing to unsubscribe from.
                    _log.Debug(icoEx, "COM object already released during detach for {DocumentName}", System.IO.Path.GetFileName(_documentKey));
                }
                finally
                {
                    _partDoc = null;
                    _assemblyDoc = null;
                    _drawingDoc = null;
                    _isAttached = false;
                }
            }
        }

        // ══════════════════════════════════════════════
        // COM EVENT HANDLERS
        // ══════════════════════════════════════════════

        /// <summary>
        /// Pre-save handler for regular Save operations.
        /// COM Signature: int FileSaveNotify(string fileName)
        /// </summary>
        private int OnFileSaveNotify(string fileName)
        {
            try
            {
                object activeDoc = (object)_partDoc ?? (object)_assemblyDoc ?? (object)_drawingDoc;
                _onPreSave(fileName, false, _documentKey, activeDoc);
            }
            catch (Exception ex)
            {
                LogHandlerError("FileSaveNotify", ex);
            }
            return 0;
        }

        /// <summary>
        /// Pre-save handler for Save-As operations.
        /// COM Signature: int FileSaveAsNotify2(string fileName)
        /// </summary>
        private int OnFileSaveAsNotify2(string fileName)
        {
            try
            {
                object activeDoc = (object)_partDoc ?? (object)_assemblyDoc ?? (object)_drawingDoc;
                _onPreSave(fileName, true, _documentKey, activeDoc);
            }
            catch (Exception ex)
            {
                LogHandlerError("FileSaveAsNotify2", ex);
            }
            return 0;
        }

        /// <summary>
        /// Post-save handler — fires AFTER the file has been written to disk.
        /// COM Signature: int FileSavePostNotify(int saveType, string fileName)
        /// </summary>
        private int OnFileSavePostNotify(int saveType, string fileName)
        {
            try
            {
                _onPostSave(saveType, fileName, _documentKey);
            }
            catch (Exception ex)
            {
                LogHandlerError("FileSavePostNotify", ex);
            }
            return 0;
        }

        /// <summary>
        /// Document closing handler.
        /// COM Signature: int DestroyNotify2(int destroyType)
        /// destroyType: cycled through swDestroyNotifyType_e
        ///   0 = swDestroyNotifyType_FullyDestroyed (all windows closed)
        ///   1 = swDestroyNotifyType_Hidden (view closed, doc still in memory)
        /// 
        /// We detach on BOTH types — once the document is hidden or destroyed,
        /// our event subscriptions are no longer meaningful.
        /// </summary>
        private int OnDestroyNotify2(int destroyType)
        {
            try
            {
                // Notify parent first (so it can clean up its dictionary)
                _onDestroy(_documentKey);

                // Then detach our events
                Detach();
            }
            catch (Exception ex)
            {
                LogHandlerError("DestroyNotify2", ex);
            }
            return 0;
        }

        // ══════════════════════════════════════════════
        // LOGGING
        // ══════════════════════════════════════════════

        private void LogHandlerError(string eventName, Exception ex)
        {
            if (ex is COMException comEx)
                _log.Error(comEx, "COM error in {EventName} for {DocumentName} (0x{ErrorCode:X8})",
                    eventName, System.IO.Path.GetFileName(_documentKey), comEx.ErrorCode);
            else
                _log.Error(ex, "Error in {EventName} for {DocumentName}",
                    eventName, System.IO.Path.GetFileName(_documentKey));
        }

        // ══════════════════════════════════════════════
        // DISPOSE
        // ══════════════════════════════════════════════

        public void Dispose()
        {
            if (_isDisposed) return;
            Detach();
            _isDisposed = true;
        }
    }
}
