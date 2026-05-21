using System;

namespace SolidWorksConnectorSDK.Models
{
    /// <summary>
    /// Categorizes the type of SolidWorks event that was captured.
    /// Used for filtering, routing, and structured logging.
    /// </summary>
    public enum EventType
    {
        /// <summary>Unknown or unclassified event.</summary>
        Unknown = 0,

        /// <summary>The active document changed in SolidWorks.</summary>
        ActiveDocumentChanged = 1,

        /// <summary>A new document was created.</summary>
        FileNew = 2,

        /// <summary>A document was opened.</summary>
        FileOpened = 3,

        /// <summary>A document save operation is starting (pre-save).</summary>
        FileSaveStarting = 10,

        /// <summary>A document was saved successfully (post-save).</summary>
        FileSaved = 11,

        /// <summary>A document Save-As operation is starting.</summary>
        FileSaveAsStarting = 12,

        /// <summary>A document Save-As completed successfully.</summary>
        FileSavedAs = 13,

        /// <summary>A document was closed.</summary>
        FileClosed = 20
    }
}
