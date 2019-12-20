using System;
using System.Collections.Generic;
using System.Threading;
using System.Configuration;
using WIC_SDK;

namespace WRPServer.Network.Client
{
    public class ContinuousGrabProvider
    {

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


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
        // Stream grabber kamery
        //PYLON_STREAMGRABBER_HANDLE hGrabber;
        //// Objekt přes který se čeká na naplnění bufferu.
        //PYLON_WAITOBJECT_HANDLE hWait;
        // Buffery asociované se StreamBufferem
        //Dictionary<PYLON_STREAMBUFFER_HANDLE, PylonBuffer<Byte>> buffers;
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

        /// <summary>
        /// Inicializuje grabbovací třídu Handle na otevřené Pylon Device.
        /// </summary>
        /// <param name="device">OTEVŘENÉ PylonDevice, ze kterého má být provedeno snímání.</param>
        public ContinuousGrabProvider(ClientHandler clientHandler, Camera cam)
        {
            this.clientHandler = clientHandler;
            this.cam = cam;
        }

        /// <summary>
        /// Nastaví interní grabbovací struktury. Metoda musí být volána přes StartHandler metodou.
        /// </summary>
        /// <returns>True, pokud proběhne nastavení grabbovacích struktur úspěšně a třída je připravená spustit snímání.</returns>
        public bool SetUpGrabber()
        {
            log.Debug("Nastavuji tridu grabbovaciho vlakna");
            // Řídící proměnná.
            int i;

            try
            {
                //// Kontinuální snímání.
                //PylonC.NET.Pylon.DeviceFeatureFromString(device, "AcquisitionMode", "Continuous");
                //// Velikost obrázku a počet datových kanálů v transportní vrstvě kamery.
                //payloadSize = checked((uint)PylonC.NET.Pylon.DeviceGetIntegerFeature(device, "PayloadSize"));
                //nStreams = PylonC.NET.Pylon.DeviceGetNumStreamGrabberChannels(device);
                //log.Debug("Kamera snima obrazy s velikosti " + payloadSize + " bytes, pocet dostupnych transportnich streamu=" + nStreams);
                //// Transportní vrstva nepodporuje streamování.
                //if (nStreams < 1)
                //{
                //    log.Error("Transportni vrstva nepodporuje streamovani, nStreams=" + nStreams);
                //    return false;
                //}
                //// Nultý StreamGrabber kamer, jeho otevření.
                //log.Debug("Oteviram nulty StreamGrabber kamery.");
                //hGrabber = PylonC.NET.Pylon.DeviceGetStreamGrabber(device, 0);
                //PylonC.NET.Pylon.StreamGrabberOpen(hGrabber);
                //// Inicializace wait objektu, přes který čekáme na načtení snímku
                //hWait = PylonC.NET.Pylon.StreamGrabberGetWaitObject(hGrabber);
                //// Konfigurace Stream bufferu - počet bufferů a velikost bufferů.
                //log.Debug("Snimani bude provedeno pres " + NUM_BUFFERS + " s velikosti " + payloadSize + "B");
                //PylonC.NET.Pylon.StreamGrabberSetMaxNumBuffer(hGrabber, NUM_BUFFERS);
                //PylonC.NET.Pylon.StreamGrabberSetMaxBufferSize(hGrabber, payloadSize);
                //// Příprava Streambufferu - alokace vnitřních struktur Pylonu.
                //PylonC.NET.Pylon.StreamGrabberPrepareGrab(hGrabber);
                //// Alokace paměti pro buffery a registrace bufferů ve StreamGrabberu
                //log.Debug("Registruji snimaci buffery ve StreamGrabber");
                //buffers = new Dictionary<PYLON_STREAMBUFFER_HANDLE, PylonBuffer<Byte>>();
                //for (i = 0; i < NUM_BUFFERS; ++i)
                //{
                //    PylonBuffer<Byte> buffer = new PylonBuffer<byte>(payloadSize, true);
                //    PYLON_STREAMBUFFER_HANDLE handle = PylonC.NET.Pylon.StreamGrabberRegisterBuffer(hGrabber, ref buffer);
                //    buffers.Add(handle, buffer);
                //}
                //// Přidání bufferů do vstupní fronty bufferů Streamgrabberu.
                //log.Debug("Pridavam snimaci buffery do vstupni fronty StreamGrabber");
                //i = 0;
                //foreach (KeyValuePair<PYLON_STREAMBUFFER_HANDLE, PylonBuffer<Byte>> pair in buffers)
                //{
                //    PylonC.NET.Pylon.StreamGrabberQueueBuffer(hGrabber, pair.Key, i++);
                //}
                //// Úspěšně provedené nastavení
                //log.Info("StreamGrabber pro snimaci vlakno uspesne nastaven");
                //return true;
            }
            catch (Exception e)
            {
                // Ziskame chybovou zpravu
                //string msg = GenApi.GetLastErrorMessage() + "\n" + GenApi.GetLastErrorDetail();
                //log.Error("Pri nastavovani StreamGrabber nastala chyba: " + e);
                //log.Error("Posledni chybove hlaseni Pylonu: " + msg);
                //return false;
            }
            return true;
        }

        /// <summary>
        /// Vrátí flag značící, zda je interní vlákno naživu. 
        /// Používá ClientHandler pro ověření, zda se úspěšně spustilo vlákno.
        /// </summary>
        /// <returns>True, pokud vnitřní vlákno běží, jinak false.</returns>
        public bool IsAlive()
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

        /// <summary>
        /// Vytvoří a spustí snímací vlákno. 
        /// Před voláním této metody musí být spuštěna metoda SetUpGrabber. Pouze, pokud tato 
        /// přípravná metoda vrátí true, může vést spuštění StartHandler na úspěšné snímání.
        /// </summary>
        /// <returns>True, pokud je snímací vlákno úspěšně spuštěno.</returns>
        public bool StartHandler()
        {
            log.Debug("Spoustim grabbovaci vlakno.");
            // Neskončí výjimkou, HandlGrabbing není null.
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

        /// <summary>
        /// Pošle grabbovacímu vláknu pokyn k doběhnutí a počká na doběhnutí vlákna.
        /// </summary>
        public void StopHandler()
        {
            continueGrabbing = false;
            // Join vlákna může skončit výjimkou, kromě zalogování je nemám jak dál ošetřit.
            // ThreadStateException může nastat, pokud uživatel třídy volá metody ve špatném pořadí - nicméně to nevede k chybě, vlákno ve výsledku neběží.
            // ThreadInterruptedException nastane např., když při Join někdo zavolá na aplikaci Control+C...
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

        /// <summary>
        /// Řídí grabbovací smyčku. Odesílá data do poskytnutých socketů.
        /// </summary>
        public void HandleGrabbing()
        {
           // log.Debug("Grabbovaci vlakno spusteno.");
           // try
           // {
           //     // Proměnné.
           //     // Flag značící, že další snímek je připraven.
           //     bool isReady;
           //     // Ukládá výsledek grabovací operace.
           //     PylonGrabResult_t grabResult;
           //     // Index bufferu, do kterého byl nasnímán aktuální snímek.
           //     int bufferIndex;
           //     // Výsledek odeslání bufferu.
           //     bool sendFrameResult;
           //     // Celkový elapsed time při čekání na jeden grab z kamery v ms.
           //     long elapsedWaitTime;
           //     // Flag, který při aktivním čekání na nový snímek značí, že je potřeba opustit vnější grabbovací smyčku.
           //     bool breakOuterLoop;
           //
           //     // Zahájení snímání.
           //     log.Info("Zahajuji grabbovani.");
           //     PylonC.NET.Pylon.DeviceExecuteCommandFeature(device, "AcquisitionStart");
           //     nGrabs = 0;
           //     nGrabErrors = 0;
           //     nSends = 0;
           //     // Hlavní grabbovací smyčka.
           //     while (continueGrabbing)
           //     {
           //         log.Debug("Grabbing thread state: clientId=" + clientHandler.ctx.clientId + ", cameraId=" + clientHandler.ctx.continuoslyGrabbingCameraId + ", nGrabs=" + nGrabs + ", nGrabErrors=" + nGrabErrors + ", nSends=" + nSends);
           //         // Čekáme na další snímek aktivním čekáním. Wait v Pylonu nejde přerušit Interruptem (?). 
           //         // V případě, že main vlákno dostane příkaz k ukončení komunikace, je potřeba umět toto čekání přerušit.
           //         elapsedWaitTime = 0;
           //         breakOuterLoop = false;
           //         log.Debug("Entering active wait loop during continous grab.");
           //         while (true)
           //         {
           //             isReady = PylonC.NET.Pylon.WaitObjectWait(hWait, 50);
           //             elapsedWaitTime += 50;
           //             // Pokud jsme překročili maximální akceptovatelnou délku čekání.
           //             // Nebo pokud stihnul přijít nový grab z kamery.
           //             // => Ukončím smyčku aktivního čekání.
           //             if (elapsedWaitTime > FRAME_TIMEOUT_MS || isReady)
           //             {
           //                 log.Info("Leaving active wait loop. ElapsedWaitTime=" + elapsedWaitTime + ", LIMIT=" + FRAME_TIMEOUT_MS + ", isReady=" + isReady);
           //                 break;
           //             }
           //             // Pokud JSME PŘIJALI PŘÍKAZ K UKONČENÍ GRABBINGU, ukončíme vnější smyčku.
           //             if (!continueGrabbing)
           //             {
           //                 log.Info("ContinueGrabbing signal acquired during active wait loop.");
           //                 breakOuterLoop = true;
           //                 break;
           //             }
           //         }
           //         // Bylo přerušeno čekání kvůli STOP GRABBING příkazu
           //         if (breakOuterLoop)
           //         {
           //             continue;
           //         }
           //         // Zpracování výsledku čekání
           //         if (!isReady)
           //         {
           //             log.Error("Vyprsel timeout pri cekani na dalsi snimek.");
           //             throw new Exception("Next frame timeout exceeded.");
           //         }
           //         // Jeden snímek byl nagrabován, převzmeme ho ze StreamGrabberu.
           //         isReady = PylonC.NET.Pylon.StreamGrabberRetrieveResult(hGrabber, out grabResult);
           //         if (!isReady)
           //         {
           //             // Sem bychom se nikdy neměli dostat.
           //             log.Error("Nepodarilo se prevzit grab result ze StreamGrabberu");
           //             throw new Exception("Unable to get GrabResult from StreamGrabber.");
           //         }
           //         // Počet nagrabovaných snímků.
           //         nGrabs++;
           //         // Získáme index bufferu, do kterého byl snímek uložen.
           //         bufferIndex = (int)grabResult.Context;
           //         // Zjistíme, jak dopadla operace grabování.
           //         if (grabResult.Status == EPylonGrabStatus.Grabbed)
           //         {
           //             // Úspěch, zpracujeme snímek. V pozadí jsou zatím získávány další snímky.
           //             PylonBuffer<Byte> buffer;
           //             if (!buffers.TryGetValue(grabResult.hBuffer, out buffer))
           //             {
           //                 // Všechny buffery jsou ve slovníku, neměli bychom se sem dostat.
           //                 log.Error("Nepodarilo se ziskat buffer asociovany s kodem.");
           //                 throw new Exception("Unable to get buffer asociated with the bufferIndex");
           //             }
           //             // Pokud není překročen limit na maximální počet nepotvrzených snímků, odešlu snímek.
           //             if (clientHandler.ctx.maxImageId - clientHandler.ctx.maxAcknowledgedImageId > MAX_FRAMES_COUNT_WITHOUT_ACK)
           //             {
           //                 log.Warn("Byl prekrocen limit na maximalni pocet nepotvrzenych snimku, novy snimek nebude odeslan.");
           //             }
           //             else
           //             {
           //                 log.Debug("Odesilam novy snimek.");
           //                 // Počet odeslaných snímků (nSends se inicializuje na 0, první snímek musí ale mít ID 1).
           //                 nSends++;
           //                 // Odešlu snímek s timestampem.
           //                 sendFrameResult = clientHandler.SendFrame(nSends, grabResult.TimeStamp, buffer.Array);
           //                 if (!sendFrameResult)
           //                 {
           //                     log.Error("Nepodarilo se odeslat snimek.");
           //                     throw new Exception("Could not send a frame to client.");
           //                 }
           //                 // Zvýším v kontextu uživatele nejvyšší imageID.
           //                 clientHandler.ctx.maxImageId = nSends;
           //             }
           //             // Počet po sobě jdoucích GrabErrors
           //             nGrabErrors = 0;
           //         }
           //         else
           //         {
           //             log.Error("Snimek " + nGrabs + " nebyl uspesne ziskan. Kod chyby=" + grabResult.ErrorCode);
           //             // Počet po sobě jdoucích GrabErrors.
           //             nGrabErrors++;
           //             // Byl překročen limit na počet po sobě jdoucích GrabErrors?
           //             if (nGrabErrors >= SEQUENTIAL_GRAB_ERRORS_LIMIT)
           //             {
           //                 log.Error("Byl prekrocen limit na pocet po sobe jdoucich GrabErrors, ukoncuji snimani.");
           //                 throw new Exception("Sequentail grab errors limit exceeded.");
           //             }
           //         }
           //         // Vrátíme buffer do vstupní fronty StreamGrabberu.
           //         PylonC.NET.Pylon.StreamGrabberQueueBuffer(hGrabber, grabResult.hBuffer, bufferIndex);
           //     }
           //     log.Info("Grabbovaci smycka opustena.");
           //
           //     // Ukončíme akvizici.
           //     log.Debug("Ukoncuji akvizici snimku.");
           //     PylonC.NET.Pylon.DeviceExecuteCommandFeature(device, "AcquisitionStop");
           //     // Necháme všechny čekající buffery přemístit do výstupní fronty.
           //     log.Debug("Premistuji cekajici bufferu do vystupni fronty");
           //     PylonC.NET.Pylon.StreamGrabberCancelGrab(hGrabber);
           //     // Vyjmeme všechny buffery ze StreamGrabberu.
           //     log.Debug("Odstranuji buuffery ze stream grabberu.");
           //     do
           //     {
           //         isReady = PylonC.NET.Pylon.StreamGrabberRetrieveResult(hGrabber, out grabResult);
           //
           //     } while (isReady);
           //     // Deregistrujeme buffery (teď už je to bezpečné, když jsou vyjmuté ze StreamGrabberu).
           //     log.Debug("Deregistruji buffery.");
           //     foreach (KeyValuePair<PYLON_STREAMBUFFER_HANDLE, PylonBuffer<Byte>> pair in buffers)
           //     {
           //         PylonC.NET.Pylon.StreamGrabberDeregisterBuffer(hGrabber, pair.Key);
           //         pair.Value.Dispose();
           //     }
           //     buffers = null;
           //     // Necháme uvolnit prostředky StreamGrabberu a uzavřeme ho.
           //     log.Debug("Uvolnuji StreamGrabber prostredky.");
           //     PylonC.NET.Pylon.StreamGrabberFinishGrab(hGrabber);
           //     PylonC.NET.Pylon.StreamGrabberClose(hGrabber);
           //     log.Info("Grabbovaci vlakno uspesne dobehlo.");
           // }
           // catch (Exception e)
           // {
           //     /* Retrieve the error message. */
           //     string msg = GenApi.GetLastErrorMessage() + "\n" + GenApi.GetLastErrorDetail();
           //     log.Error("Pri grabbovani nastala vyjimka", e);
           //     log.Error("Posledni chyba z Pylonu: " + msg);
           //     log.Error("Odesilam chybu klientovi.");
           //     clientHandler.SendGrabbingError("Exception: " + e.Message + ", PylonError: " + msg);
           //     log.Info("Grabbovaci vlakno dobehlo po chybe");
           // }
        }
    }
}
