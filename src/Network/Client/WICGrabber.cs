using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using WIC_SDK;
using System.Diagnostics;
using WRPServer.Network.Enum;

namespace WRPServer.Network.Client
{
    public class WICGrabber
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        public static int TIMEOUT_FOR_CAMERA_REQUESTS = Convert.ToInt32(ConfigurationManager.AppSettings["timeoutForCameraRequests"]);

        // Počet bufferů, se kterými pracuje StreamGrabber.
        uint NUM_BUFFERS = Convert.ToUInt32(ConfigurationManager.AppSettings["numberOfBuffers"]);
        // Timeout na další snímek v ms.
        uint FRAME_TIMEOUT_MS = Convert.ToUInt32(ConfigurationManager.AppSettings["frameTimeoutMs"]);
        // Maximální počet grab errorů, které mohou nastat po sobě, než je snímání automaticky vypnuto.
        uint SEQUENTIAL_GRAB_ERRORS_LIMIT = Convert.ToUInt32(ConfigurationManager.AppSettings["sequentialGrabErrorsLimit"]);
        // Maximální počet nepotvrzených snímků, po kterých vlákno přestane zasílat nové klientovi.
        // Ale nevypne se, jenom snímky zahazuje
        uint MAX_FRAMES_COUNT_WITHOUT_ACK = Convert.ToUInt32(ConfigurationManager.AppSettings["maxFramesCountWithoutAck"]);

        // Handle na kameru.
        private Camera cam;
        // Velikost obrázku v bytech.
        uint payloadSize;
        // Počet datových kanálů, které kamera má.
        uint nStreams;
        
        // Pořadí snímaného snímku.
        int nGrabs;
        // Pořadí odeslaného snímku.
        int nSends;
        // Počet po sobě jdoucích grab errorů.
        int nGrabErrors;
        
        // Flag, který říká vláknu, zda má pokračovat další iterací.
        // Před spuštěním snímání je nastaveno na true, přepnout na false ho může pouze metoda StopHandler.
        bool continueGrabbing;
        // Interní snímací vlákno.
        Thread grabbingThread;

        // Reference na obsluhu klienta, pro něhož kamera snímá.
        // Slouží k odesílání snímků.
        ClientHandler clientHandler;

        ManualResetEvent onConnectEvent;
        ManualResetEvent onDisconnectEvent;
        ManualResetEvent onNewFrameEvent;
        ManualResetEvent onAcquisitionStartEvent;
        ManualResetEvent onAcquisitionStopEvent;

        public WICGrabber(ClientHandler clientHandler)
        {
            this.clientHandler = clientHandler;
        }
        public void AssignCameraDevice(Camera cam)
        {
            this.cam = cam;
            this.cam.AcquisitionStoped += camera_AcquisitionStopped;
            this.cam.AcquisitionStarted += camera_AcquisitionStarted;
            this.cam.Disconnected += camera_Disconnected;
            this.cam.Connected += camera_Connected;
            this.cam.OnNewFrame += camera_OnNewFrame;

            onConnectEvent = new ManualResetEvent(false);
            onDisconnectEvent = new ManualResetEvent(false);
            onNewFrameEvent = new ManualResetEvent(false);
            onAcquisitionStartEvent = new ManualResetEvent(false);
            onAcquisitionStopEvent = new ManualResetEvent(false);
        }
        public void UnassignCameraDevice()
        {
            this.cam = null;
        }
        public bool HasCameraAssigned()
        {
            return this.cam != null;
        }
        void camera_AcquisitionStarted(object sender, EventArgs e)
        {
            log.Debug("Event camera_AcquisitionStarted byl zachycen");
            onAcquisitionStartEvent.Set();
        }
        void camera_AcquisitionStopped(object sender, EventArgs e)
        {
            log.Debug("Event camera_AcquisitionStoped byl zachycen");
            onAcquisitionStopEvent.Set();
        }
        void camera_Disconnected(object sender, EventArgs e)
        {
            log.Debug("Event camera_Disconnected byl zachycen");
            onDisconnectEvent.Set();
        }
        void camera_Connected(object sender, EventArgs e)
        {
            log.Debug("Event camera_Connected byl zachycen");
            onConnectEvent.Set();
        }
        void camera_OnNewFrame(object sender, EventArgs e)
        {
            //log.Debug("Event camera_OnNewFrame byl zachycen");
            onNewFrameEvent.Set();
        }
        public bool Connect(int timeout)
        {
            if (cam == null)
            {
                log.Warn("Grabber nema prirazenou kameru, kterou by mohl pripojit");
                return false;
            }
            onConnectEvent.Reset();
            log.Debug("Volani cam.Connect()");
            cam.Connect();
            log.Debug("Cekani na event camera_Connected...");
            return onConnectEvent.WaitOne(timeout);
        }
        public bool Disconnect(int timeout)
        {
            if (cam == null)
            {
                log.Warn("Grabber nema prirazenou kameru, kterou by mohl odpojit");
                return false;
            }
            onDisconnectEvent.Reset();
            log.Debug("Volani cam.Disconnect()");
            cam.Disconnect();
            log.Debug("Cekani na event camera_Disconnected...");
            return onDisconnectEvent.WaitOne(timeout);
        }

        public bool StartAcquisition(int timeout)
        {
            if(cam == null)
            {
                log.Warn("Grabber nema prirazenou kameru, které by mohl spustit snimani");
                return false;
            }
            onAcquisitionStartEvent.Reset();
            log.Debug("Volani cam.StartAcquisition()");
            cam.StartAcquisition();
            log.Debug("Cekani na event camera_AcquisitionStarted...");
            return onAcquisitionStartEvent.WaitOne(timeout);
        }
        public bool StopAcquisition(int timeout)
        {
            if (cam == null)
            {
                log.Warn("Grabber nema prirazenou kameru, které by mohl zastavit snimani");
                return false;
            }
            onAcquisitionStopEvent.Reset();
            log.Debug("Volani cam.StopAcquisition()");
            cam.StopAcquisition();
            log.Debug("Cekani na event camera_AcquisitionStopped...");
            return onAcquisitionStopEvent.WaitOne(timeout);
        }
        
        public bool SingleFrame(out UInt64 timestamp, out UInt16 height, out UInt16 width, out float[] temperatures, int timeout)
        {
            log.Warn("Ziskavani noveho snimku...");
            timestamp = 0;
            height = 0;
            width = 0;
            onNewFrameEvent.Reset();
            if(!onNewFrameEvent.WaitOne(timeout))
            {
                log.Warn("Novy snimek neprisel do timeoutu");
                temperatures = null;
                return false;
            }
            timestamp = Convert.ToUInt64(cam.ImageTimestamp);
            height = Convert.ToUInt16(cam.Height);
            width = Convert.ToUInt16(cam.Width);
            temperatures = cam.TemperatureValues;
            log.Debug("Vyska obrazku je " + height);
            log.Debug("Sirka obrazku je " + width);
            log.Debug("Delka pole temperatures je: " + temperatures.Length);
            log.Debug("Pocet dimenzi pole temperatures je: " + temperatures.Rank);
            return true;
        }
        public bool IsGrabbingThreadAlive()
        {
            if (grabbingThread != null)
            {
                return grabbingThread.IsAlive;
            }
            else
            {
                return false;
            }
        }
        public bool StartGrabbing()
        {
            log.Debug("Spoustim grabbovaci vlakno.");
            log.Debug("Nastavuji continueGrabbing=true");
            continueGrabbing = true;
            grabbingThread = new Thread(HandleGrabbing);
            try
            {
                grabbingThread.Start();
                log.Info("Grabbovaci vlakno spusteno.");
                return true;
            }
            catch (ThreadStateException tse)
            {
                log.Error("Vlakno jiz bylo spusteno.", tse);
                return false;
            }
            catch (OutOfMemoryException oome)
            {
                log.Error("Neni dostatek dostupne pameti pro start noveho vlakna.", oome);
                return false;
            }
        }
        
        public void StopGrabbing()
        {
            log.Debug("Nastavuji continueGrabbing=false");
            continueGrabbing = false;
            try
            {
                log.Debug("Cekam na ukonceni grabbovaciho vlakna.");
                grabbingThread.Join();
                log.Info("Grabbovaci vlakno ukonceno.");
            }
            catch (ThreadStateException tse)
            {
                log.Warn("Ukoncovane vlakno je ve ThreadState.Unstarted, nemohu ho tedy ukoncit", tse);
            }
            catch (ThreadInterruptedException tie)
            {
                log.Warn("Vlakno bylo preruseno pri blokujicim Join", tie);
            }
        }

        public void HandleGrabbing()
        {
            log.Debug("Grabbovaci vlakno spusteno.");
            // Proměnné.
            // Flag značící, že další snímek je připraven.
            bool isReady;
            // Index bufferu, do kterého byl nasnímán aktuální snímek.
            int bufferIndex;
            // Výsledek odeslání bufferu.
            bool sendFrameResult;
            // Celkový elapsed time při čekání na jeden grab z kamery v ms.
            long elapsedWaitTime;
            // Flag, který při aktivním čekání na nový snímek značí, že je potřeba opustit vnější grabbovací smyčku.
            bool breakOuterLoop;

            nGrabs = 0;
            nGrabErrors = 0;
            nSends = 0;
            float[] temperatures = null;
            UInt64 timestamp;
            UInt16 height, width;

            int timeoutForFrame = 1000;
            // Hlavní grabbovací smyčka.
            while (continueGrabbing)
            {
                log.Debug("Grabbing thread state: clientId=" + clientHandler.ctx.clientId + ", nGrabs=" + nGrabs + ", nGrabErrors=" + nGrabErrors + ", nSends=" + nSends);
                elapsedWaitTime = 0;
                breakOuterLoop = false;
                


                if (!SingleFrame(out timestamp, out height, out width, out temperatures, timeoutForFrame))
                {
                    log.Error("V rezimu kontinualniho snimanu nebyl ziskan zadny snimek po dobu " + timeoutForFrame);
                    break;
                }
                clientHandler.SendFrame(0, timestamp, height, width, temperatures);
            }
            log.Info("Grabbovaci smycka opustena.");
        }
    }
}
