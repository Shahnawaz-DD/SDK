using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SolidWorksConnectorSDK
{
    /// <summary>
    /// Implements IOleMessageFilter to handle COM "Application is Busy" and 
    /// "Call was Rejected By Callee" errors that occur when SolidWorks is processing.
    /// 
    /// This is REQUIRED for stable SolidWorks COM automation. Without it, you will
    /// get RPC_E_CALL_REJECTED (0x80010001) and RPC_E_SERVERCALL_RETRYLATER errors
    /// that crash the connector.
    /// 
    /// IMPORTANT: The calling thread MUST be STA (Single-Threaded Apartment).
    /// Decorate your Main() method with [STAThread].
    /// </summary>
    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(
            int dwCallType,
            IntPtr hTaskCaller,
            int dwTickCount,
            IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(
            IntPtr hTaskCallee,
            int dwTickCount,
            int dwRejectType);

        [PreserveSig]
        int MessagePending(
            IntPtr hTaskCallee,
            int dwTickCount,
            int dwPendingType);
    }

    /// <summary>
    /// COM message filter that automatically retries rejected calls when SolidWorks is busy.
    /// Register this at application startup and revoke it at shutdown.
    /// </summary>
    public class OleMessageFilter : IOleMessageFilter
    {
        // ──────────────────────────────────────────────
        // P/Invoke: OLE32 CoRegisterMessageFilter
        // ──────────────────────────────────────────────

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(
            IOleMessageFilter newFilter,
            out IOleMessageFilter oldFilter);

        // ──────────────────────────────────────────────
        // Constants
        // ──────────────────────────────────────────────

        private const int SERVERCALL_ISHANDLED = 0;
        private const int PENDINGMSG_WAITDEFPROCESS = 2;
        private const int SERVERCALL_RETRYLATER = 2;
        private const int RETRY_DELAY_MS = 99;  // Retry after ~100ms when server is busy

        // ──────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────

        /// <summary>
        /// Registers the OLE message filter on the current thread.
        /// Must be called from an STA thread before any COM calls.
        /// </summary>
        /// <exception cref="COMException">Thrown if the current thread is not STA.</exception>
        public static void Register()
        {
            // Validate STA requirement
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new COMException(
                    "OleMessageFilter requires an STA (Single-Threaded Apartment) thread. " +
                    "Ensure your Main() method is decorated with [STAThread].");
            }

            IOleMessageFilter newFilter = new OleMessageFilter();
            IOleMessageFilter oldFilter;
            int hr = CoRegisterMessageFilter(newFilter, out oldFilter);

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        /// <summary>
        /// Revokes (unregisters) the OLE message filter. 
        /// Always call this in a finally block during shutdown.
        /// </summary>
        public static void Revoke()
        {
            IOleMessageFilter oldFilter;
            CoRegisterMessageFilter(null, out oldFilter);
        }

        // ──────────────────────────────────────────────
        // IOleMessageFilter Implementation
        // ──────────────────────────────────────────────

        /// <summary>
        /// Handles incoming COM calls. We accept all calls.
        /// </summary>
        int IOleMessageFilter.HandleInComingCall(
            int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        {
            return SERVERCALL_ISHANDLED;
        }

        /// <summary>
        /// Called when our outgoing COM call is rejected by the server.
        /// If the server says "retry later", we wait and retry.
        /// </summary>
        int IOleMessageFilter.RetryRejectedCall(
            IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType == SERVERCALL_RETRYLATER)
            {
                // Return a value between 0 and 100 to retry after that many milliseconds
                return RETRY_DELAY_MS;
            }

            // -1 means cancel the call (the server permanently rejected it)
            return -1;
        }

        /// <summary>
        /// Called when there is a message pending while waiting for a COM response.
        /// We use the default processing behavior.
        /// </summary>
        int IOleMessageFilter.MessagePending(
            IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        {
            return PENDINGMSG_WAITDEFPROCESS;
        }
    }
}
