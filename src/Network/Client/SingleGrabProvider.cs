using System;
using System.Configuration;
using WIC_SDK;

namespace WRPServer.Network.Client
{
    public class SingleGrabProvider
    {

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private uint FRAME_TIMEOUT_MS = Convert.ToUInt32(ConfigurationManager.AppSettings["frameTimeoutMs"]);

        private Camera cam;

        public SingleGrabProvider(Camera cam)
        {
            this.cam = cam;
        }

        public bool SingleFrame(out long timestamp, out byte[] frame)
        {
            //// Proměnné.
            //// Buffer pro grab.
            //PylonBuffer<Byte> imgBuf = null;
            //// Snímek z Pylonu.
            //PylonGrabResult_t grabResult;
            //
            //// Dummy hodnota výstupních parametrů, pro všechny případy.
            //timestamp = 0;
            //frame = null;
            //// Provedu grab.
            //try
            //{
            //    if (!PylonC.NET.Pylon.DeviceGrabSingleFrame(device, 0, ref imgBuf, out grabResult, FRAME_TIMEOUT_MS))
            //    {
            //        // Timeout
            //        log.Error("Pri grabovani vyprsel timeout.");
            //        throw new Exception("Single frame timeout exceeded.");
            //    }
            //    if (grabResult.Status == EPylonGrabStatus.Grabbed)
            //    {
            //        log.Debug("Grab byl uspesny.");
            //        frame = imgBuf.Array;
            //        timestamp = grabResult.TimeStamp;
            //        return true;
            //    }
            //    else
            //    {
            //        log.Error("Vysledek grabovani neni uspech: " + grabResult.ErrorCode);
            //        throw new Exception("Frame was not grabbed successfully: " + grabResult.ErrorCode);
            //    }
            //    // Uklizení paměti bufferu - ale potřebuju si zkopírovat data...
            //    // imgBuf.Dispose();
            //}
            //catch (Exception e)
            //{
            //    string msg = GenApi.GetLastErrorMessage() + "\n" + GenApi.GetLastErrorDetail();
            //    log.Error("Pri single frame grab nastala chyba", e);
            //    log.Error("Pylon error: " + msg);
            //    return false;
            //}
            timestamp = 5;
            frame = null;
            return true;
        }
    }
}
