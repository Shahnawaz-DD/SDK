using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using System;
using System.Runtime.InteropServices;

namespace SolidWorksConnectorSDK.Services
{
    public class ComLifetimeManager : IDisposable
    {
        private readonly ILogger<ComLifetimeManager> _logger;
        private ISldWorks _swApp;

        public ComLifetimeManager(ILogger<ComLifetimeManager> logger)
        {
            _logger = logger;
            OleMessageFilter.Register();
        }

        /// <summary>
        /// Gets the active SolidWorks instance, attempting to reconnect if it crashed.
        /// </summary>
        public ISldWorks GetSolidWorksApp()
        {
            if (_swApp == null || !IsAlive(_swApp))
            {
                Reconnect();
            }
            return _swApp;
        }

        private bool IsAlive(ISldWorks app)
        {
            try
            {
                // Simple fast call to check if RPC channel is alive
                int processId = app.GetProcessID();
                return processId > 0;
            }
            catch (COMException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Reconnect()
        {
            _logger.LogInformation("Attempting to connect to SolidWorks COM server...");
            try
            {
                _swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                
                // Suppress VBA macros and UI prompts during automation
                _swApp.UserControl = false;
                _swApp.CommandInProgress = true;
                
                _logger.LogInformation("✅ Connected to SolidWorks. VBA macros and dialogs suppressed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to SolidWorks. Is it running?");
                throw new InvalidOperationException("SolidWorks must be running before the SDK can process jobs.", ex);
            }
        }

        /// <summary>
        /// Deterministic cleanup of any COM object to avoid GC.Collect().
        /// </summary>
        public void ReleaseComObject(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try
                {
                    Marshal.ReleaseComObject(comObj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release COM object.");
                }
            }
        }

        public void Dispose()
        {
            ReleaseComObject(_swApp);
            _swApp = null;
            OleMessageFilter.Revoke();
        }
    }
}
