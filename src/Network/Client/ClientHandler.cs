using System;
using System.IO;
using System.Threading;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Xml;
using WRPServer.Network.Enum;
using WRPServer.Network.Server;
using WIC_SDK;

namespace WRPServer.Network.Client
{
    /// <summary>
    /// Centrální třída řídící komunikaci s jedním klientem. 
    /// Instance třídy je zpravidla vytvořena ze třídy ServerSocket. Obsluha klienta probíhá asynchronně. Metoda HandleClient obsahuje hlavní smyčku,
    /// ve které jsou parsovány zprávy přijaté od klienta a sestavované odpovědi.
    /// </summary>
    public class ClientHandler
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private ServerContext serverCtx;
        public ClientContext ctx;

        /// <summary>
        /// Inicializuje třídu globálním kontextem serveru i lokálním kontextem klienta.
        /// </summary>
        /// <param name="serverCtx">Kontext aplikace sloužící pro přístup ke kamerám a dalším sdíleným prostředkům. Celá aplikace používá jedinou instanci.</param>
        /// <param name="state">Lokální kontext komunikace s aktuálním klientem. Každý klient má vlastní kontext. V kontextu je od ServerSocket zadáno ID klienta a otevřený Socket pro komunikaci.</param>
        public ClientHandler(ServerContext serverCtx, ClientContext state)
        {
            this.serverCtx = serverCtx;
            this.ctx = state;
        }

        /// <summary>
        /// Založí a spustí vlákno pro obsluhu klienta. Obsluha klienta je řešena v metodě HandleClient.
        /// </summary>
        public void StartHandler()
        {
            Thread clientThread = new Thread(HandleClient);
            clientThread.Start();
        }

        /// <summary>
        /// Definuje centrální smyčku, přes kterou probíhá obsluha klienta.
        /// </summary>
        public void HandleClient()
        {
            // Inicializace.
            // Výsledek nastavení proxy objektů.
            bool setUpResult;

            // Získáme do klientského kontextu proxy objekty pro čtení ze Socketu.
            setUpResult = SetUpSocketStreams();
            if (!setUpResult)
            {
                log.Error("Nepodarilo se nastavit proxy objekty pro cteni ze Socketu, ukoncuji obsluhu klienta.");
                DisposeAllResources();
                return;
            }
            // Konec inicializace.


            // Obsluha klienta.
            // Definice proměnných.
            // Výsledek čtení akce od klienta.
            bool actionReadResult;
            // Přijatá akce od klienta.
            byte action;
            // Výsledek zpracování akce.
            bool actionSolvingResult;
            // Výsledek odeslání bufferu klientovi.
            bool sendingBufferResult;

            // Centrální smyčka obsluhy.
            // Nastavím pokračování komunikace - flag je přepnut při dobrovolném vypnutí kanálu klientem.
            ctx.continueCommunication = true;
            // Nastavím v kontextu prvotní stav = START.
            ctx.clientState = ClientState.START;
            while (true)
            {
                // Řešení extrémních situací.
                // Pokud je klient v režimu kontinuálního snímání, ověříme, že vlákno běží,
                // jinak může dojít k potenciálnímu deadlocku, kdy klient čeká na první snímek
                // a server čeká na první ACK.
                // Jde o ošklivý hotfix, ale je potřeba zprávu odeslat jakkoliv dřív, než začnu s blokujícím čtením ze socketu...
                if (ctx.clientState == ClientState.CONTINUOUS_GRABBING
                    && ctx.maxAcknowledgedImageId == 0
                    && !ctx.continousGrabProvider.IsAlive())
                {
                    log.Error("Snimaci vlakno selhalo a neodeslalo zadny snimek.");
                    SendGrabbingErrorMessage("Grabbing failed.");
                }

                // Začátek běžné smyčky.
                // Přečtení požadované akce od klienta.
                actionReadResult = ReadSafelyByteFromSocket(out action);
                if (!actionReadResult)
                {
                    log.Error("Nepodarilo se precist pozadovanou akci od klienta, ukoncuji obsluhu klienta.");
                    DisposeAllResources();
                    return;
                }

                // Na základě aktuálního stavu rozhodnu a zpracování akce.
                log.Debug("Aktualni stav ClientHandler je " + ctx.clientState.ToString());
                switch (ctx.clientState)
                {
                    case ClientState.START:
                        actionSolvingResult = InitializeCommunication(action);
                        break;
                    case ClientState.INITIALIZED:
                        actionSolvingResult = HandleActionRequest(action);
                        break;
                    case ClientState.CONTINUOUS_GRABBING:
                        actionSolvingResult = HandleGrabbingRequest(action);
                        break;
                    default:
                        // Sem bych se nikdy neměl dostat.
                        log.Error("ClientState je v neznamem stavu " + ctx.clientState);
                        actionSolvingResult = false;
                        break;
                }
                log.Debug("Vysledek zpracovani akce: " + actionSolvingResult);

                // Pokud byla v rámci obsluhy přichystána nová zpráva, odešlu ji.
                if (ctx.bufferReadyToSend)
                {
                    log.Debug("Ve vystupnim bufferu jsou pripravena data, ktera budou nyni odeslana.");
                    // Odešlu data.
                    sendingBufferResult = SendOutputBuffer();
                    if (!sendingBufferResult)
                    {
                        log.Error("Nepodarilo se klientovi odeslat odpoved, ukoncuji obsluhu klienta");
                        DisposeAllResources();
                        return;
                    }
                    // Přepnu flag zpátky.
                    ctx.bufferReadyToSend = false;
                }

                // Pokud selhalo zpracování zprávy od klienta (IO chyba, nesmyslný požadavek, ...)
                // Nebo klient požádal o ukončení komunikace
                // Vrátíme prostředky a ukončíme komunikaci.
                log.Debug("Vysledek zpracovani akce od klienta: " + actionSolvingResult);
                log.Debug("Stav continueCommunication: " + ctx.continueCommunication);
                if (!actionSolvingResult ||
                    !ctx.continueCommunication)
                {
                    log.Info("Komunikace s klientem bude ukoncena.");
                    DisposeAllResources();
                    return;
                }
            }
        }

        /// <summary>
        /// Odešle nouzovou zprávu v případě, že se nepodaří spustit grabbování v separátním vlákně a hrozí deadlock.
        /// </summary>
        private void SendGrabbingErrorMessage(string message)
        {
            // Proměnné.
            // Výsledek zapsání nouzové zprávy.
            bool messageWriteResult;
            // Výsledek odeslání nouzov zprávy.
            bool sendResult;

            // Odešlu zprávu.
            messageWriteResult = WriteErrorMessageToOutputBuffer(message);
            if (!messageWriteResult)
            {
                log.Error("Nepodarilo se do vystupniho bufferu zapsat nouzovou zpravu.");
                return;
            }
            sendResult = SendOutputBuffer();
            if (!sendResult)
            {
                log.Error("Buffer s nouzovou zpravou se nepodarilo odeslat.");
            }
        }

        /// <summary>
        /// Zpracuje novou akci o klienta, když je ClientHandler ve stavu ContinuousGrabbing.
        /// </summary>
        /// <param name="action">Akce, o kterou klient žádá.</param>
        /// <returns>True, pokud byla akce validní a byla serverem úspěšně vyřešena, jinak false.</returns>
        private bool HandleGrabbingRequest(byte action)
        {
            // V tomto režimu klient posílá buď ACK_CONTINUOUS_GRAB nebo STOP_CONTINUOUS_GRAB.
            // Všechny ostatní požadavky jsou nevalidní a vedou k ukončení komunikace.
            if (action != (byte)ActionCode.ACK_CONTINUOUS_GRAB
                && action != (byte)ActionCode.STOP_CONTINUOS_GRAB)
            {
                log.Error("Klient poslal akci " + action + ", ktera je nevalidni v ramci stavu " + ctx.clientState);
                return false;
            }

            // Naopak hlavní vlákno zde nic neposílá - snímky a případné selhání snímání posílá grabbovací vlákno.
            // Hlavní vlákno pouze do kontextu zapisuje aktuální ACK, při close operaci grabbovací vlákno explicitně vypne a teprve po odeslání
            // posledního snímku odesílá potvrzení o ukončení grabbovacího stavu a přepíná se zpět na INITIALIZED kontext.
            log.Debug("Zpracovavam akci s kodem=" + action + ", pri stavu=" + ctx.clientState);
            // Proměnné.
            // Výsledek ověření uživatelova ID.
            bool clientIdValidationResult;
            // Výsledek čtení ID kamery.
            bool cameraIdReadResult;
            // ID kamery, kterou uživatel snímá.
            int cameraId;
            // Popis případné chyby pro klienta.
            string errorDescription;

            // Zpracování požadavku.
            // Klient se vždy nejdřív musí ohlásit svým ID (bytes na pozici 2-5).
            clientIdValidationResult = ValidateClientId();
            if (!clientIdValidationResult)
            {
                log.Error("Nepodarilo se overit ID klienta.");
                return false;
            }
            // Klient dále posílá ID kamery.
            cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
            if (!cameraIdReadResult)
            {
                log.Error("Nepodarilo se precist ID kamery ze socketu.");
                return false;
            }
            // Ověříme, že klient vlastní kameru.
            // Vlastní klient kameru?
            if (!ctx.cameras.ContainsKey(cameraId))
            {
                log.Warn("Klient se pokousi manipulovat s kamerou, ke ktere nevlastni zamek.");
                errorDescription = "Client does not hold lock over " + cameraId + " camera.";
                return WriteErrorMessageToOutputBuffer(errorDescription);
            }
            // Ověříme, že kamera byla zahrnuta do režimu kontinuálního snímání.
            if (cameraId != ctx.continuoslyGrabbingCameraId)
            {
                log.Warn("Klient posila ACK na kameru, kterou vlastni, ale nema jit v CONT GRAB rezimu, cameraId=" + cameraId);
                errorDescription = "Client does not have camera " + cameraId + " in continuos grabbing state.";
                return WriteErrorMessageToOutputBuffer(errorDescription);
            }
            // Samotný dispatch akcí.
            // Na začátku metody je ověřeno, že jde o jednu ze dvou povolených akcí.
            // Větev default tedy nemůže nastat (ale bez ní řve Visual Studio).
            switch (action)
            {
                case (byte)ActionCode.ACK_CONTINUOUS_GRAB:
                    {
                        // Potvrzení ID snímku.
                        return ResolveAckContinuousGrabAction();
                    }
                case (byte)ActionCode.STOP_CONTINUOS_GRAB:
                    {
                        // Příkaz k zastavení kontinuálního snímání.
                        return ResolveStopContinuousGrabAction();
                    }
                default:
                    return false;
            }

        }

        /// <summary>
        /// Zpracuje požadavek na potvrzení zaslaných snímků při kontinuálním grabbování.
        /// Klient zasílá nové nejvyšší ID snímku, které dostal.
        /// </summary>
        /// <returns>True, pokud je požadavek úspěšně zpracován a do bufferu připravena odpověď k odeslání, jinak false.</returns>
        private bool ResolveAckContinuousGrabAction()
        {
            log.Debug("Zpracovavam potvrzeni snimku od klienta.");
            // Proměnné.
            // Výsledek čtení ID potvrzovaného snímku.
            bool imageIdReadResult;
            // ID potvrzeného snímku.
            int imageId;

            // Přečtu ID snímku, který se klient snaží potvrdit.
            imageIdReadResult = ReadSafelyIntFromSocket(out imageId);
            if (!imageIdReadResult)
            {
                log.Error("Nepodarilo se nacist ID obrazku z klientskeho socketu.");
                return false;
            }
            log.Debug("Aktualne maxAcknowledgedImageId=" + ctx.maxAcknowledgedImageId + ", maxImageId=" + ctx.maxImageId);
            log.Debug("Klient potvrzuje snimek s ID=" + imageId);
            // Ověřím, že ID potvrzeného snímku je vyšší než doposud nejvýšše potvrzené snímku.
            if (imageId <= ctx.maxAcknowledgedImageId)
            {
                log.Warn("Klient potvrzuje snimek s nizsim ID nez potvrdil uz drive, ignoruji.");
            }
            // Ověřím, že ID potvrzovaného snímku je validní, tedy je nižší nebo rovno maximálnímu známému imageId.
            if (imageId > ctx.maxImageId)
            {
                log.Warn("Klient potvrzuje snimek s vyssim ID nez je maximalni zname ID v kontextu, ignoruji.");
            }
            // ID je validní, inkrementuji tedy max potvrzené ID.
            ctx.maxAcknowledgedImageId = imageId;
            return true;
        }

        /// <summary>
        /// Zpracuje požadavek na ukončení kontinuálního snímání.
        /// Metoda vždy končí úspěchem, protože je již dříve ověřeno, že klient vlastní a kamera je ve stavu snímání.
        /// V případě, že se nepovede ukončit grabbovací vlákno sice již nemusí jít znovu cont grabbing spustit, ale s tím nic neuděláme (viz systém výjimek v Pylon API).
        /// </summary>
        /// <returns>Vždy true.</returns>
        private bool ResolveStopContinuousGrabAction()
        {
            log.Debug("Zpracovavam pozadavek na ukonceni kontinualniho snimani.");
            // Vypnu grabbovací vlákno a počkám na jeho doběhnutí.
            log.Info("Vypinam grabbovaci vlakno a mazu grabbovaci kontext.");
            ctx.continousGrabProvider.StopHandler();
            // Zruším grabbovací kontext.
            ctx.continuoslyGrabbingCameraId = -1;
            ctx.maxImageId = 0;
            ctx.maxAcknowledgedImageId = 0;
            ctx.continousGrabProvider = null;
            // Přepnu stav klienta.
            log.Info("Prepinam stav klienta na " + ClientState.INITIALIZED);
            ctx.clientState = ClientState.INITIALIZED;
            // Sestavím klientovi odpověď.
            ctx.oPos = 0;
            AddByteToOutputBuffer((byte)ResponseCode.OK);
            ctx.bufferReadyToSend = true;
            return true;
        }

        /// <summary>
        /// Zpracuje od klienta akci, když je ClientHandler ve stavu INITIALIZED.
        /// </summary>
        /// <param name="action">Kód akce, kterou klient požaduje.</param>
        /// <returns>True, pokud je akce úspěšně vyřešena a do výstupního bufferu zapsána odpověď pro klienta, jinak false.</returns>
        private bool HandleActionRequest(byte action)
        {
            log.Debug("Zpracovavam akci s kodem=" + action + ", pri stavu=" + ctx.clientState);
            // Proměnné.
            // Výsledek ověření uživatelova ID.
            bool clientIdValidationResult;

            // Zpracování požadavku.
            // Klient se vždy nejdřív musí ohlásit svým ID (bytes na pozici 2-5).
            clientIdValidationResult = ValidateClientId();
            if (!clientIdValidationResult)
            {
                log.Warn("Nepodarilo se overit ID klienta.");
                return false;
            }

            // Podle akce zavolám příslušný resolver.
            switch (action)
            {
                case (byte)ActionCode.ACK_CONTINUOUS_GRAB:
                    {
                        // Tato akce je validní pouze když je klient v stavu kontinuálního snímání.
                        return ResolveInvalidAction(action);
                    }
                case (byte)ActionCode.CLOSE:
                    {
                        // Validní ukončení komunikace, nastavím pokyn k ukončení komunikace vracím úspěch
                        return ResolveCloseAction();
                    }
                case (byte)ActionCode.CONTINOUS_GRAB:
                    {
                        // Přepne aplikace do grabovacího módu.
                        // TODO
                        return ResolveContinuousGrabAction();
                    }
                case (byte)ActionCode.GET_SETTINGS:
                    {
                        // Přečte ID kamery, pro kterou se uživatel snaží získat nastavení, zjistí zda ji vlastní. 
                        // Pokud ano, načte nastavení přes GenICam API do XML a ten uloží do výstupního bufferu přes ASCII string.
                        throw new NotImplementedException();
                        //return ResolveGetSettingsAction();
                    }
                case (byte)ActionCode.HELLO:
                    {
                        // HELLO příkaz je validní pouze při zahájení komunikace, přeruším tedy komunikaci.
                        return ResolveInvalidAction(action);
                    }
                case (byte)ActionCode.LIST:
                    {
                        // LIST příkaz nechá vylistovat připojené kamery, transformuje jejich seznam do XML, XML převede na ASCII string a ten pošle uživateli.
                        return ResolveListCameraAction();
                    }
                case (byte)ActionCode.LOCK_CAMERA:
                    {
                        // Zjistí, zda kamera existuje a je volná. Pokud ano, je zamknuta pro klienta.
                        throw new NotImplementedException();
                        //return ResolveLockCameraAction();
                    }
                case (byte)ActionCode.SET_SETTINGS:
                    {
                        // Přečte ID kamery, pro kterou se uživatel snaží zapsat nastavení a zjistí, zda ji vlastní.
                        // Přečte požadavky na nastavení a pokusí se je prosadit do kamery.
                        // Pošle zpátky report s výsledkem nastavení.
                        throw new NotImplementedException();
                        // return ResolveSetSettingsAction();
                    }
                case (byte)ActionCode.SINGLE_FRAME:
                    {
                        // Odešle jeden snímek ze zadané kamery.
                        return ResolveSingleFrameAction();
                    }
                case (byte)ActionCode.STOP_CONTINUOS_GRAB:
                    {
                        // Tato akce je validní pouze když je klient v stavu kontinuálního snímání.
                        return ResolveInvalidAction(action);
                    }
                case (byte)ActionCode.UNLOCK_CAMERA:
                    {
                        // Zjistí, zda klient vlastní kameru, pokud ano, tak jí vrátí ServerContextu. Jinak sestaví chybovou zprávu.
                        throw new NotImplementedException();
                        //return ResolveUnlockCameraAction();
                    }
                default:
                    {
                        // Neznamá akce, vrátím false, což vede k ukončení komunikace.
                        return ResolveUnknownAction(action);
                    }
            }
        }

        /// <summary>
        /// Zpracuje požadavek klienta na nasnímání jediného snímku.
        /// Ověří zda klient vlastní kameru. Pokud ano, spustí grabbování jediného snímku. 
        /// Tento snímek zapíše do výstupního bufferu společně s timestampem, ID snímku=0 a daty snímku.
        /// </summary>
        /// <returns>True, pokud je požadavek klienta úspěšně vyřešen (ať už kladně nebo záporně pro klienta - např. nezamčená kamera), jinak false (při zpracování nastala chyba).</returns>
        private bool ResolveSingleFrameAction()
        {
            log.Debug("Zpracovavam pozadavek na jeden snimek.");
            // Proměnné.
            // ID kamery.
            int cameraId;
            // Výsledek čtení ID kamery.
            bool cameraIdReadResult;
            // Popis případné chyby.
            string errorDescription;
            // Výsledek čtení snímku z kamery.
            bool frameReadResult;
            // Pole bajtů se snímkem.
            byte[] frame;
            // Timestamp snimku.
            long timestamp;
            // Kamera, ze které chceme snímat.
            Camera imageProvider;
            // Provider SingleFrame
            SingleGrabProvider singleGrabProvider;
            // Výsledek zápisu snímku do výstupního bufferu.
            bool bufferWriteResult;

            // Přečtu ID kamery.
            cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
            if (!cameraIdReadResult)
            {
                log.Error("Nepodarilo se nacist ID kamery z klientskeho socketu.");
                return false;
            }
            // Ověřím, že klient vlastní kameru s daným ID.
            if (!ctx.cameras.ContainsKey(cameraId))
            {
                log.Warn("Klient se pokousi ziskat snimek z kamery, kterou nema zamcenou.");
                errorDescription = "Client does not hold lock over " + cameraId + " camera.";
                return WriteErrorMessageToOutputBuffer(errorDescription);
            }
            imageProvider = ctx.cameras[cameraId];
            // Pokusím se získat snímek z kamery.
            singleGrabProvider = new SingleGrabProvider(imageProvider);
            frameReadResult = singleGrabProvider.SingleFrame(out timestamp, out frame);
            if (!frameReadResult)
            {
                log.Error("Nepodarilo se nacist single frame z kamery.");
                return WriteErrorMessageToOutputBuffer("Server unable to fetch single frame.");
            }
            // Máme snímek, zapíšu ho do výstupního bufferu.
            ctx.oPos = 0;
            bufferWriteResult = WriteFrameToOutputBuffer(0, timestamp, frame);
            if (!bufferWriteResult)
            {
                log.Error("Nepodarilo se zapsat snimek do vystupniho bufferu.");
                return false;
            }
            // Buffer připraven na odeslání úspěch.
            ctx.bufferReadyToSend = true;
            return true;
        }

        /// <summary>
        /// Zpracuje klientův požadavek na zapnutí kontinuálního snímání z kamery.
        /// Ověří, že klient vlastní kameru a případně spustí speciální vlákno, ve kterém probíhá grabbing.
        /// Pokud je kontinuální snímání spuštěno, ClientHandler se přepíná do nového stavu kontinuálního snímání
        /// a klientovi jsou odesílány nové snímmky.
        /// </summary>
        /// <returns>True, pokud je požadavek úspěšně vyřešen (ať už kladně nebo záporně pro klienta), jinak false.</returns>
        private bool ResolveContinuousGrabAction()
        {
            // Proměnné.
            // Výsledek čtení ID kamery klientského socketu.
            bool cameraIdReadResult;
            // ID kamery, ze které chceme kontinuálně snímat.
            int cameraId;
            // Případná chybová zpráva.
            string errorDescription;
            // Proxy objekt pro kameru
            Camera imageProvider;
            // Výsledek nastavení snímacího vlákna.
            bool grabberSetupResult;
            // Výsledek spuštění grabbovacího vlákna.
            bool grabberRunResult;

            // Přečtu ID kamery ze socketu.
            cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
            if (!cameraIdReadResult)
            {
                log.Error("Nepodarilo se precist ID kamery pro kontinualni snimani.");
                return false;
            }
            // Vlastní klient kameru?
            if (!ctx.cameras.ContainsKey(cameraId))
            {
                log.Warn("Klient se pokousi spustit kontinualni snimani na kamere, kterou nema zamcenou.");
                errorDescription = "Client does not hold lock over " + cameraId + " camera.";
                return WriteErrorMessageToOutputBuffer(errorDescription);
            }
            // Klient vlastní kameru - zkusím spustit kontinuální snímání ve zvláštním vláknu.
            imageProvider = ctx.cameras[cameraId];
            // Inicializuji vlákno, připravím pro snímání, spustím snímání.
            ContinuousGrabProvider contGrabProvider = new ContinuousGrabProvider(this, imageProvider);
            grabberSetupResult = contGrabProvider.SetUpGrabber();
            if (!grabberSetupResult)
            {
                log.Error("Nepodarilo se nastavit StreamGrabber");
                errorDescription = "Unable to setup the camera's StreamGrabber";
                return WriteErrorMessageToOutputBuffer(errorDescription);
            }
            // Potřebuji poslat klientovi potvrzení ještě před tím, než vlákno odešle první snímek.
            // Proto ještě před spuštěním snímání odešlu potvrzení.
            // Pokud by selhalo spuštění vlákna, bude se to řešit jako selhání v průběhu snímání v dalších iteracích komunikace.
            ctx.oPos = 0;
            AddByteToOutputBuffer((byte)ResponseCode.OK);
            ctx.bufferReadyToSend = true;
            // Nastavuji GrabbingContext - toto taky musí proběhnout dříve, než do hodnot začne hrabat druhé vlákno.
            ctx.continuoslyGrabbingCameraId = cameraId;
            ctx.continousGrabProvider = contGrabProvider;
            ctx.maxImageId = 0;
            ctx.maxAcknowledgedImageId = 0;
            // Spustím vlákno.
            grabberRunResult = contGrabProvider.StartHandler();
            if (!grabberRunResult)
            {
                log.Error("Nepodarilo se spustit StreamGrabber!!!");
            }
            // Nastavuji stav na ContinousGrabbing.
            log.Info("Prepinam stav klientskeho vlakna na continuos grabbing.");
            ctx.clientState = ClientState.CONTINUOUS_GRABBING;
            // Vracím úspěch.
            return true;
        }

        ///// <summary>
        ///// Zpracuje požadavek na změnu nastavení kamery.
        ///// Přečte ze socketu ID kamery, kterou chce uživatel změnit, délku XML konfigurace a text konfigurace.
        ///// Zpracuje vstupní XML a pokusí se podle něj kameru nastavit.
        ///// Vyprodukuje výstupní XML reportem, v případě chyby vyprodukuje chybové hlášení
        ///// </summary>
        ///// <returns>True, pokud je požadavek zpracován bez interní chyby aplikace, tzn. aplikace dokáže uživateli poskytnou výstup, který je zapsán do výstupního buffer. Jinak v případě selhání false.</returns>
        //private bool ResolveSetSettingsAction()
        //{
        //    log.Debug("Zpracovavam pozadavek na modifikace nastaveni kamery.");
        //    // Proměnné.
        //    // Výsledek načtení ID kamery ze socketu.
        //    bool cameraIdReadResult;
        //    // ID kamery.
        //    int cameraId;
        //    // Výsledek čtení délku konfiguračního XML od klienta.
        //    bool xmlLengthReadResult;
        //    // Délka konfiguračního XML.
        //    int xmlLength;
        //    // Výsledek čtení konfiguračního XML.
        //    bool xmlReadResult;
        //    // Konfigurační XML od klienta jako XML ve stringu.
        //    string xml;
        //    // Výsledek zpracování akce.
        //    ResponseCode responseCode;
        //    // Popis případného slehání pro klienta.
        //    string errorDescription;
        //    // Proxy objekt pro kameru, na které mají být provedeny změny.
        //    Camera imageProvider;
        //    // Výsledek převodu XML na list změn.
        //    bool xmlToListResult;
        //    // List změn (key, value), které uživatel chce provést v nastavení.
        //    List<KeyValuePair<string, string>> changes;
        //    // Výstupní XML dokument.
        //    XmlDocument outputXmlDocument;
        //    // Výstupní XML dokument jako string.
        //    string outputXml;
        //    // Výsledek zápisu výstupního XML do bufferu.
        //    bool outputXmlWriteResult;
        //
        //    // Přečtu od klienta ID kamery | Délku konfiguračního XML | Konfigurační XML (ASCII)
        //    cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
        //    if (!cameraIdReadResult)
        //    {
        //        log.Error("Nepodarilo nacist ID kamery ze socketu.");
        //        return false;
        //    }
        //    xmlLengthReadResult = ReadSafelyIntFromSocket(out xmlLength);
        //    if (!xmlLengthReadResult)
        //    {
        //        log.Error("Nepodarilo se nacist delku konfiguracniho XML ze socketu.");
        //        return false;
        //    }
        //    xmlReadResult = ReadSafelyAsciiStringFromSocket(out xml, xmlLength);
        //    if (!xmlReadResult)
        //    {
        //        log.Error("Nepodarilo se nacist konfiguracni XML ze socketu.");
        //        return false;
        //    }
        //    // Ověřím, zda má uživatel zámek na kameru.
        //    if (!ctx.cameras.ContainsKey(cameraId))
        //    {
        //        log.Warn("Klient se pokousi nastavit kameru, kterou nevlastni");
        //        // Odešlu chybovou zprávu.
        //        errorDescription = "Client is not an owner of the specified camera, cannot change its settings.";
        //        return WriteErrorMessageToOutputBuffer(errorDescription);
        //    }
        //    // Uživatel vlastní kameru.
        //    // Přeložím XML na list požadovaných změn.
        //    imageProvider = ctx.cameras[cameraId];
        //    changes = new List<KeyValuePair<string, string>>();
        //    xmlToListResult = XmlUtils.translateXmlToChangesList(changes, out outputXmlDocument, xml);
        //    if (!xmlToListResult)
        //    {
        //        log.Warn("Konfiguracni XML ma nevalidni strukturu, neni mozne jej pouzit pro nastaveni.");
        //        // Odešlu chybovou zprávu.
        //        errorDescription = "Could not parse the configuration XML.";
        //        return WriteErrorMessageToOutputBuffer(errorDescription);
        //    }
        //    // Pokusím se zapsat nastavení
        //    try
        //    {
        //        XmlNodeList rootList = outputXmlDocument.GetElementsByTagName("Root");
        //        // Metoda modifikuje outputXmlDocument
        //        imageProvider.SetSettingsTreeFromXml(changes, outputXmlDocument, (XmlElement)rootList.Item(0));
        //    }
        //    catch (Exception e)
        //    {
        //        log.Error("Nepodarilo se zmenit nastaveni kamery", e);
        //        return WriteErrorMessageToOutputBuffer("Application was unable to change the camera settings: " + e.Message);
        //    }
        //    // Převedeme výstupní XML na string
        //    try
        //    {
        //        StringWriter stringWriter = new StringWriter();
        //        XmlWriter xmlTextWriter = XmlWriter.Create(stringWriter);
        //        outputXmlDocument.WriteTo(xmlTextWriter);
        //        xmlTextWriter.Flush();
        //        outputXml = stringWriter.GetStringBuilder().ToString();
        //    }
        //    catch (Exception e)
        //    {
        //        log.Error("Nepodarilo se prepsat vystupni XML na string.", e);
        //        return WriteErrorMessageToOutputBuffer("Internal application error" + e);
        //    }
        //    // Odešleme zprávu
        //    ctx.oPos = 0;
        //    responseCode = ResponseCode.OK;
        //    AddByteToOutputBuffer((byte)responseCode);
        //    outputXmlWriteResult = AddPrefixedMessageToOutputBuffer(outputXml);
        //    if (!outputXmlWriteResult)
        //    {
        //        log.Error("Nepodarilo se zapsat XML do vystupniho bufferu");
        //        return false;
        //    }
        //    ctx.bufferReadyToSend = true;
        //    return true;
        //}

        /// <summary>
        /// Zapíše do výstupního bufferu zadané chybové hlášení.
        /// Metoda si vynuluje offset ve výstupním bufferu a zapíše ERROR_CODE (1B),
        /// délku chybové zprávy (4B) a samotnou chybovou zprávu jako pole bajtů v ASCII.
        /// </summary>
        /// <param name="errorDescription">Chybová hláška k odeslání.</param>
        /// <returns>True, pokud byl výstupní buffer úspěšně zapsán a připraven k odesláníl jinak false.</returns>
        private bool WriteErrorMessageToOutputBuffer(string errorDescription)
        {
            // Výsledek zapsání chybové zprávy.
            bool errorDescriptionWriteResult;

            // Odešlu chybovou zprávu.
            ctx.oPos = 0;
            AddByteToOutputBuffer((byte)ResponseCode.ERROR);
            errorDescriptionWriteResult = AddPrefixedMessageToOutputBuffer(errorDescription);
            if (!errorDescriptionWriteResult)
            {
                log.Error("Nepodarilo se zapsat chybovou hlasku do vystupniho bufferu.");
                return false;
            }
            // Akce úspěšně zpracována, socket připraven k odeslání.
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool WriteFrameToOutputBuffer(int frameId, long timestamp, byte[] array)
        {
            // Proměnné.
            // Výsledek zápisu frameID do bufferu.
            bool frameIdWriteResult;
            // Výsledek zapsání timestampu snímku do bufferu.
            bool frameTimestampWriteResult;
            // Výsledek zápisu snímku do bufferu.
            bool frameWriteResult;

            // Zápis do bufferu.
            AddByteToOutputBuffer((byte)ResponseCode.FRAME);
            frameIdWriteResult = AddIntToOutputBuffer(frameId);
            if (!frameIdWriteResult)
            {
                log.Error("Nepodarilo se zapsat frameId do bufferu.");
                return false;
            }
            frameTimestampWriteResult = AddLongToOutputBuffer(timestamp);
            if (!frameTimestampWriteResult)
            {
                log.Error("Nepodarilo se zapsat timestamp snimku do bufferu.");
                return false;
            }
            frameWriteResult = AddByteArrayToOutputBuffer(array);
            if (!frameWriteResult)
            {
                log.Error("Nepodarilo se zapsat frame do bufferu.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Zpracuje akci na přečtení nastavení kamery. 
        /// Nejprve přečte ze socketu ID kamery, pro kterou chce klient číst nastavení. 
        /// Zjistí, zda klient tuto kameru vlastní. Pokud ano, vytáhne její proxy objekt z klientova kontextu.
        /// Pokusí se získat XML s aktuálním nastavením z kamery a odeslat ji klientovi.
        /// </summary>
        /// <returns>True, pokud je akce úspěšně zpracována (ať už pro klienta úspěšně nebo neúspěšně), jank false.</returns>
        //private bool ResolveGetSettingsAction()
        //{
        //    log.Debug("Zpracovavam pozadavek na ziskani nastaveni z kamery.");
        //    // Proměnné.
        //    // Výsledek čtení ID kamery.
        //    bool cameraIdReadResult;
        //    // ID kamery, pro kterou chce uživatel znát nastavení.
        //    int cameraId;
        //    // Výsledek zpracování požadavku.
        //    ResponseCode responseCode;
        //    // Popis chyby pro klienta pro případ, že požadavek nebyl vyřízen kladně.
        //    string errorDescription;
        //    // Výsledek zápisu délky chybové zprávy a samotné chybové zprávy do výstupního socketu.
        //    bool errorDescriptionWriteResult;
        //    // Proxy třída pro kameru.
        //    ImageProvider imageProvider;
        //    // Nastavení kamery jako string s XML.
        //    string xmlSettings;
        //    // Výsledek zápisu XML nastavení do výstupního bufferu.
        //    bool xmlSettingsWriteResult;
        //
        //    // Načtu ID kamery z uživatelského socketu.
        //    cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
        //    if (!cameraIdReadResult)
        //    {
        //        log.Error("Nepodarilo se nacist ID kamery ze socketu");
        //        return false;
        //    }
        //    log.Debug("Uzivatel zada o nastaveni kamery " + cameraId);
        //    // Ověřím, že uživatel vlastní kameru.
        //    if (!ctx.cameras.ContainsKey(cameraId))
        //    {
        //        log.Warn("Klient se pokousi ziskat nastaveni kamery, pro kterou nevlastni zamek.");
        //        // Zapíšu do výstupního bufferu byte:ERROR_CODE (1B) | int:ERROR_MESSAGE_LENGTH (4B) | string:ERROR_MESSAGE
        //        responseCode = ResponseCode.ERROR;
        //        AddByteToOutputBuffer((byte)responseCode);
        //        errorDescription = "Client is not an owner of the specified camera, cannot provide settings.";
        //        errorDescriptionWriteResult = AddPrefixedMessageToOutputBuffer(errorDescription);
        //        if (!errorDescriptionWriteResult)
        //        {
        //            log.Error("Nepodarilo se do vystupniho bufferu zapsat chybovou zpravu.");
        //            return false;
        //        }
        //        // Ve výstupním socketu je zapsána chybová zpráva, zpracování požadavku bylo úspěšné.
        //        ctx.bufferReadyToSend = true;
        //        return true;
        //    }
        //    // Uživatel vlastní kamery, získám její nastavení.
        //    log.Info("Uzivatel vlastni kameru " + cameraId + ", bude mu predano nastaveni");
        //    imageProvider = ctx.cameras[cameraId];
        //    try
        //    {
        //        // Pokusím se získat strom s XML nastavením ve stringu.
        //        xmlSettings = imageProvider.GetSettingsTreeAsXml();
        //        // Úspěch.
        //        // Posílám OK_CODE (1B) | XML_LEN (4B) | XML
        //        ctx.oPos = 0;
        //        responseCode = ResponseCode.OK;
        //        AddByteToOutputBuffer((byte)responseCode);
        //        xmlSettingsWriteResult = AddPrefixedMessageToOutputBuffer(xmlSettings);
        //        if (!xmlSettingsWriteResult)
        //        {
        //            log.Error("Nepodarilo se zapsat XML nastaveni kamery do vystupniho bufferu");
        //            return false;
        //        }
        //        // Požadavek úspěšně zpracován, buffer připraven na odeslání.
        //        ctx.bufferReadyToSend = true;
        //        return true;
        //    }
        //    catch (Exception e)
        //    {
        //        log.Warn("Nepodarilo se z kamery ziskat nastaveni.", e);
        //        // Zapíšu do výstupního bufferu byte:ERROR_CODE (1B) | int:ERROR_MSG_LENGTH (4B) | string:ERROR_MSG
        //        ctx.oPos = 0;
        //        responseCode = ResponseCode.ERROR;
        //        AddByteToOutputBuffer((byte)responseCode);
        //        errorDescription = "Application was unable to transform camera settings into XML representation: " + e.Message;
        //        errorDescriptionWriteResult = AddPrefixedMessageToOutputBuffer(errorDescription);
        //        if (!errorDescriptionWriteResult)
        //        {
        //            log.Error("Nepodarilo se do vystupniho bufferu zapsat chybovou zpravu");
        //            return false;
        //        }
        //        // Požadavek úspěšně zpracován, buffer připraven na odeslání.
        //        ctx.bufferReadyToSend = true;
        //        return true;
        //    }
        //}

        /// <summary>
        /// Zpracuje požadavek klienta na odemčení kamery.
        /// Metoda přečte ID kamery, kterou chce klient odemknout. Ověří, zda ji klient skutečně vlastní. Pokud ano, odebere ji z Dictionary
        /// v uživatelově kontextu a pokusí se jí vrátit ServerContextu. Do výstupního bufferu je zapsán výsledek operace. Buď jenom ResponseCode OK
        /// v případě úspěšného vrácení, jinak ERROR a prefixed error description.
        /// </summary>
        /// <remarks>
        /// Může se stát, že selže odemření kamery v rámci ServerContextu (při volání metod Pylon API). Tím může nastat situace,
        /// kdy klient odemkne kamera, je mu odmazána z jeho kontextu, nicméně kamera zůstává nepoužitelná pro jakékoliv další žadatele.
        /// Tento problém nejde příliš rozumně vyřešit, metoda tak vrací úspěch i když vracení kamery technicky selže.
        /// </remarks>
        /// <returns>True, pokud je akce úspěšně zpracována, tzn. je do výstupního bufferu zapsána celá výstupní zpráva (ať už OK nebo ERROR). Jinak false.</returns>
        //private bool ResolveUnlockCameraAction()
        //{
        //    log.Debug("Zpracovavam pozadavek na odemceni kamery.");
        //    // Proměnné.
        //    // Výsledek čtení ID kamery ze socketu.
        //    bool cameraIdReadResult;
        //    // ID kamery, kterou chce klient vrátit.
        //    int cameraId;
        //    // Výsledek odemykání kamery.
        //    ResponseCode responseCode;
        //    // V případě chyby její popis.
        //    string errorDescription;
        //    // Proxy objekt mazané kamery.
        //    ImageProvider imageProvider;
        //    // Výsledek uvolnění kamery ze ServerContext.
        //    bool serverContextFreeResult;
        //    // Výsledek případného zápisu chyby do výstupního bufferu.
        //    bool errorDescriptionWriteResult;
        //
        //    // Přečtu ze socketu ID kamery, co klient vrací.
        //    cameraIdReadResult = ReadSafelyIntFromSocket(out cameraId);
        //    if (!cameraIdReadResult)
        //    {
        //        log.Error("Nepodarilo se nacist ID vracene kamery ze socketu");
        //        return false;
        //    }
        //    // Vrátím kameru ServerContextu
        //    log.Info("Klient se snazi vratit kameru " + cameraId);
        //    if (ctx.cameras.ContainsKey(cameraId))
        //    {
        //        log.Debug("Overeno, ze klient vlastni kameru " + cameraId);
        //        // Uchopím objekt s kamerou
        //        imageProvider = ctx.cameras[cameraId];
        //        // Smažu klíč s kamerou z lokálního dictionary
        //        ctx.cameras.Remove(cameraId);
        //        // Pokusím se uvolnit kameru globálně.
        //        // Pokud toto neskončí úspěchem, je to závažná chyba, se kterou nic neudělám.
        //        // Z profilu klienta bude kamera vymazána a je dost možné, že už nebude dále dostupná pro nikoho dalšího (je obecně v nedef stavu,
        //        // ale pokud kamera zůstane připojená a nebude zaseknutá v Open stavu, měla by být čitelná i pro další klienty...).
        //        serverContextFreeResult = serverCtx.UnlockCamera(imageProvider, cameraId);
        //        if (!serverContextFreeResult)
        //        {
        //            log.Error("Nepodarilo se korektne vratit kameru na ServerContext!!!");
        //            // Nekončím - nejde o problém klienta...
        //        }
        //        // Vrátím zprávu s úspěchem.
        //        ctx.oPos = 0;
        //        responseCode = ResponseCode.OK;
        //        AddByteToOutputBuffer((byte)responseCode);
        //    }
        //    else
        //    {
        //        log.Info("Klient se pokousi uvolnit kameru " + cameraId + ", kterou nevlastni.");
        //        // Vrátím zprávu s neúspěchem.
        //        ctx.oPos = 0;
        //        responseCode = ResponseCode.ERROR;
        //        AddByteToOutputBuffer((byte)responseCode);
        //        // Popis chyby.
        //        errorDescription = "Cannot unlock camera that is not owned by client.";
        //        errorDescriptionWriteResult = AddPrefixedMessageToOutputBuffer(errorDescription);
        //        if (!errorDescriptionWriteResult)
        //        {
        //            log.Error("Nepodarilo se zapsat chybovou zpravu do vystupniho bufferu");
        //            return false;
        //        }
        //    }
        //    // Akce úspěšně zpracována, výsledek zapsán dovýstupního bufferu, který je nyní připraven k odeslání.
        //    ctx.bufferReadyToSend = true;
        //    return true;
        //}

        /// <summary>
        /// Zpracuje poždavek klienta na zamčení kamery.
        /// Metoda přečte ID požadované kamery ze socketu. Následně požádá ServerContext o zamčení kamery.
        /// Pokud kamera existuje, je volná a zamčení proběhne úspěšně, je kamera přiřazena klientovi a uložena do jeho kontextu.
        /// V případě úspěchu je odeslána zpráva s 1B označující úspěch. Jinak je poslán neúspěch (1B), délka chybové zprávy (4B) a chybová zpráva.
        /// Metoda tuto odpověď zapíše do socketu.
        /// </summary>
        /// <returns>True, pokud je požadavek úspěšně zpracován a do socketu je zapsána výstupní zpráva (ať už o úspěchu zamčení, nebo neúspěchu). Jinak false.</returns>
        //private bool ResolveLockCameraAction()
        //{
        //    log.Debug("Zpracovavam pozadavek na zamceni kamery");
        //    // Proměnné.
        //    // Výsledek čtení ID kamery ze socketu.
        //    bool requestedCameraIdReadResult;
        //    // ID požadované kamery.
        //    int requestedCameraId;
        //    // Výsledek získání kamery.
        //    ResponseCode response;
        //    // Popis případné chyby při získávání kamery.
        //    string responseErrorDescription;
        //    // Handle na zamčenou kameru.
        //    ImageProvider imageProvider;
        //    // Výsledek v případě vypisování chybové zprávy do výstupního bufferu.
        //    bool errorMessageWriteResult;
        //
        //    // Ze socketu přečtu ID požadované kamery.
        //    requestedCameraIdReadResult = ReadSafelyIntFromSocket(out requestedCameraId);
        //    if (!requestedCameraIdReadResult)
        //    {
        //        log.Error("Nepodarilo se nacist ID kamery z klientskeho socketu.");
        //        return false;
        //    }
        //    // Od ServerContext se pokusím získat zámek.
        //    // ServerContext ošetří při zamykání chyby, vrátí jednak parametry výsledek procesu a případný popis chyby.
        //    // Samotný návratový objekt není null pouze, když je výsledek procesu ResponseCode OK.
        //    log.Info("Klient se snazi zamknou kameru " + requestedCameraId);
        //    imageProvider = this.serverCtx.LockCamera(requestedCameraId, out response, out responseErrorDescription);
        //    if (imageProvider != null)
        //    {
        //        log.Info("Klient " + ctx.clientId + " uspesne zamknul kameru " + requestedCameraId);
        //        // Přidám kameru do dictionary zamčených devices.
        //        ctx.cameras.Add(requestedCameraId, imageProvider);
        //    }
        //    // Odpovím klientovi s výsledkem
        //    ctx.oPos = 0;
        //    AddByteToOutputBuffer((byte)response);
        //    // Pokud jsme zámek získali, je nyní zpráva kompletní.
        //    // Pokud ne, musíme ještě přidat délku chybové zprávy a text chybové zprávy.
        //    if (response != ResponseCode.OK)
        //    {
        //        log.Info("Klient " + ctx.clientId + " nedostal zamek na kameru " + requestedCameraId);
        //        errorMessageWriteResult = AddPrefixedMessageToOutputBuffer(responseErrorDescription);
        //        if (!errorMessageWriteResult)
        //        {
        //            log.Error("Nepodarilo se po neuspesnem zamceni kamery zapsat chybove hlaseni do vystupniho bufferu");
        //            return false;
        //        }
        //    }
        //    // Akce zpracována bez chyby, zpráva ve výstupním bufferu připravena k odeslání.
        //    log.Debug("Uspesne zpracovan pozadavek na zamceni kamery.");
        //    ctx.bufferReadyToSend = true;
        //    return true;
        //}

        /// <summary>
        /// Zpracuje žádost na vylistování připojených kamer. 
        /// Vylistované kamery třídou ServerContext zapsány do XML. XML je převedeno do stringu a následně do pole bajtů v ASCII kódování.
        /// Do bufferu je zapsána zpráva, ve které první bajt obsahuje úspěšnost vylistování kamer (1 bajt). Následuje délka výpisu kamer (4 bajty)
        /// a samotný výpis.
        /// </summary>
        /// <returns>True, pokud je požadavek zpracován úspěšně a do výstupního bufferzu je připravena zpráva, jinak false.</returns>
        private bool ResolveListCameraAction()
        {
            log.Debug("Zpracovavam akci na vylistovani pripojenych kamer.");
            // Proměnné
            // Délka zasílaného payloadu - XML zprávy.
            int xmlMessageLength;
            // Výsledek zápisu délky XML do bufferu.
            bool xmlMessageLengthResult;
            // Výsledek zápisu XML zprávy do výstupního bufferu.
            bool xmlMessageResult;

            // Příprava ASCII stringu, který obsahuje výpis XML
            // V nejhorším případě nejsou připojené žádné kamery nebo se je nepovede vypsat, ale string je vždy platný a neprázdný.
            string cameraListXml = this.serverCtx.ListDevicesAsXml();
            // Sestavím odpověď
            // byte:OK | int:XML_LEN | byte[]:XML
            // 1 B     | 4 B         | XML_LEN B
            // Vynuluji pozici ve výstupním bufferu
            ctx.oPos = 0;
            // Výsledek - vždy OK
            AddByteToOutputBuffer((byte)ResponseCode.OK);
            // Délka payload
            xmlMessageLength = cameraListXml.Length;
            xmlMessageLengthResult = AddIntToOutputBuffer(xmlMessageLength);
            // Samotný payload
            xmlMessageResult = AddStringToOutputBuffer(cameraListXml);
            // Bylo vše převedeno a zapsáno úspěšně?
            if (xmlMessageLengthResult
                && xmlMessageResult)
            {
                // Požadavek úspěšně zpracován, výstupní buffer připraven k odeslání.
                log.Debug("Pozadavek na vylistovani kamer uspesne zpracovan.");
                ctx.bufferReadyToSend = true;
                return true;
            }
            else
            {
                log.Error("Nepovedlo se splnit pozadavek na vylistovani kamer.");
                return false;
            }
        }

        /// <summary>
        /// Zpracuje požadavek na akci, která není v aktuálním stavu klienta povolená.
        /// </summary>
        /// <param name="action">Kód akce, kterou klient požadoval.</param>
        /// <returns>Vždy false.</returns>
        private bool ResolveInvalidAction(byte action)
        {
            log.Error("Klient pozadoval akci " + action + ", ktera neni pri stavu " + ctx.clientState + " validni.");
            return false;
        }

        /// <summary>
        /// Zpracuje požadavek klienta k ukončení komunikace. Nastaví flag continueCommunication v lokálním kontextu na false, čímž ukončí komunikaci s klientem.
        /// </summary>
        /// <returns>Vždy true.</returns>
        private bool ResolveCloseAction()
        {
            log.Info("Rozpoznan pokyn k ukonceni komunikace.");
            ctx.continueCommunication = false;
            // Už nic dalšího neposílám.
            return true;
        }

        /// <summary>
        /// Zpracuje nevalidní akci od klienta. Zaloguje neznámý kód a vrátí false.
        /// </summary>
        /// <param name="action">Kód akce, kterou uživatel definoval.</param>
        /// <returns>Vždy false.</returns>
        private bool ResolveUnknownAction(byte action)
        {
            log.Error("Klient se pokousi o neznamou akci (action code: " + action + "), nebo akci neplatnou v kontextu aktualniho stavu (stav=" + ctx.clientState + ").");
            return false;
        }

        /// <summary>
        /// Z klientského socketu přečte 4 bajty, sestaví z nich integer, reprezentující ID, kterým se uživatel prokazuje a ověří toto ID vůči ID uiloženém v kontextu.
        /// </summary>
        /// 
        /// <remarks>
        /// Volající je zodpovědný za volání této metody ve vhodné chvíli, tzn. v sitauci, kdy se očekává, že následující 4 bajty v socketu budou reprezentovat klientovo ID.
        /// Metoda je však odolná vůči chybám, tudíž při selhání síťové komunikace nevyhodí výjimku, ale jenom vrátí false.
        /// </remarks>
        /// 
        /// <returns>True, pokud bylo ID klienta úspěšně načteno ze socketu a ověřeno vůči ID v kontextu.</returns>
        private bool ValidateClientId()
        {
            log.Debug("Overuji ID klienta.");
            // Výsledek čtení deklarované uživatelského ID z profilu.
            bool readIntFromSocketResult;
            // ID, kterým se uživatel ve zprávě představuje.
            int declaredClientId;

            // Pokusím se přečíst int ze socketu
            readIntFromSocketResult = ReadSafelyIntFromSocket(out declaredClientId);
            if (!readIntFromSocketResult)
            {
                log.Error("Nepodarilo se nacist klientske cislo ze socketu.");
                return false;
            }
            // Zvaliduji deklarované číslo
            if (declaredClientId == ctx.clientId)
            {
                log.Debug("Klientske ID uspesne overeno.");
                return true;
            }
            else
            {
                log.Warn("Klient se deklaruje spatnym ID=" + declaredClientId);
                return false;
            }
        }

        /// <summary>
        /// Přečte ze socketu 32 bit integer. 
        /// </summary>
        /// <param name="number">Výstupní parametr, který reprezentuje načtené číslo. Pikud načtení čísla selže a metoda vrátí false, parametr bude mít vždy hodnotu 0.</param>
        /// <returns>True, pokud bylo ze socketu úspěšně načteno číslo, jinak false.</returns>
        private bool ReadSafelyIntFromSocket(out int number)
        {
            // Výsledek čtení bytů ze socketu.
            bool readBytesFromSocketResult;
            // Načtené bajty ze socketu.
            byte[] intBytes;
            // Výsledek převedení big endian bytes na int.
            bool transformBytesToIntResult;

            // Dummy hodnota 0 výstupního parametru, pro případ, že něco selže.
            number = 0;
            // Chci 32 bit int, čtu 4 bajty ze socketu.
            readBytesFromSocketResult = ReadSafelyBytesFromSocket(out intBytes, 4);
            if (!readBytesFromSocketResult)
            {
                log.Error("Nepodarilo se nacist potrebne 4 bajty ze socketu.");
                return false;
            }
            // Načtené bajty ze sítě jsou v big endian.
            // Pokud je to potřeba, převedu je na little endian a následně na 32 bit int.
            transformBytesToIntResult = BigEndianBytesToInt(out number, intBytes);
            if (!transformBytesToIntResult)
            {
                log.Error("Nepodarilo se prevest 4 bajty nactene ze site na int.");
                return false;
            }
            else
            {
                // Nyní je výstupní parametr úspěšně nastaven, můžu vrátit úspěch.
                return true;
            }
        }


        /// <summary>
        /// Provede úvodní HandShake v protokolu s klientem.
        /// Nejprve ověří, zda první zaslaná akce je HELLO. 
        /// 
        /// Pokud ano, zapíše do výstupního bufferu zprávu, která obsahuje HELLO a ID,
        /// jež bylo přiděleno klientovi, a kterým se bude identifikovat v následné komunikaci. Volající se stará o samotné odeslání zprávy.
        /// V případě úspěšné inicializace rovněž změní stav klienta na INITIALIZED.
        /// </summary>
        /// <param name="action">Kód zprávy, kterou klient poslal.</param>
        /// <returns>True, pokud klient poslal kód HELLO, jinak false.</returns>
        private bool InitializeCommunication(byte action)
        {
            log.Debug("Provadim uvodni HandShake s klientem.");
            // Výsledek převedení int na pole bajtů
            bool intTransformResult;

            // Zkontroluji kód, který klient poslal
            if (action != (byte)ActionCode.HELLO)
            {
                log.Error("Klient zaslal neplatnou prvni akci: " + action + ". Prvni akce musi byt HELO. Ukoncuji komunikaci");
                return false;
            }
            else
            {
                log.Debug("Overen format inicializacni zpravy, klient se ohlasuje HELLO.");
                // Založím odpověď - vynuluji pozici ve výstupním bufferu.
                ctx.oPos = 0;
                // Odpovím HELO a pošlu klientovi jeho ID.
                AddByteToOutputBuffer((byte)ResponseCode.HELLO);
                intTransformResult = AddIntToOutputBuffer(ctx.clientId);
                if (!intTransformResult)
                {
                    log.Error("Nepodarilo se zapsat klientovo cislo do vysupniho bufferu.");
                    return false;
                }
                // Přehodím flag indikující, že výstupní buffer je připraven k odeslání.
                // Celkově 5 bajtů.
                ctx.bufferReadyToSend = true;
                log.Debug("Pripraven vystupni buffer s uvitaci zpravou a ID k odeslani.");
                // Změním stav klienta.
                ctx.clientState = ClientState.INITIALIZED;
                log.Info("Zmenen stav klienta na " + ctx.clientState);
                // Vrátím úspěšné vyřízení inicializace.
                return true;
            }
        }

        /// <summary>
        /// Zapíše do výstupního bufferu v lokálním kontextu zadaný byte a inkrementuje pozici v bufferu o 1.
        /// </summary>
        /// 
        /// <remarks>
        /// Metoda nekontroluje přetečení výstupního bufferu. Je na volajícím, aby ověřil, že ve výstupním bufferu zbývá dostatek místa.
        /// </remarks>
        /// 
        /// <param name="b">Hodnota byte, která má být zapsána do výstupního bufferu.</param>
        private void AddByteToOutputBuffer(byte b)
        {
            // Zapíšu byte a inkrementuji pozici
            ctx.outputBuffer[ctx.oPos] = b;
            ctx.oPos += 1;
        }

        /// <summary>
        /// Zapíše do výstupního bufferu v lokálním kontextu 32bitový integer.
        /// Metoda převede integer do big endian bytové reprezentace (přes bytové pole) a v této reprezentaci integer uloží do výstupního bufferu.
        /// Pozice ve výstupním bufferu je inkrementována o 4 pozice.
        /// </summary>
        /// 
        /// <remarks>
        /// Metoda nekontroluje přetečení výstupního bufferu. Je na volajícím, aby ověřil, že ve výstupním bufferu zbývá dostatek místa.
        /// Pokud není metoda úspěšná a vrací tedy false, buffer zůstane nezměněný.
        /// </remarks>
        /// 
        /// <param name="number">Číslo, které má být zapsáno do výstupního bufferu.</param>
        private bool AddIntToOutputBuffer(int number)
        {
            // Reference pro výstupní parametr - pole bajtů, do kterého bude vypsán int
            byte[] bytes;
            // Výsledek převedení integeru na pole bajtů
            bool intToBytesTransformResult;

            // Převedu na big endian, zapíšu, inkrementuji pozici
            intToBytesTransformResult = IntToBigEndianBytes(out bytes, number);
            if (!intToBytesTransformResult)
            {
                log.Debug("Nepodarilo se prevest predane cislo na big endian pole bajtu");
                return false;
            }
            // Zkopíruji pole bajtů do výstupního bufferu
            try
            {
                Array.Copy(bytes, 0, ctx.outputBuffer, ctx.oPos, 4);
                ctx.oPos += 4;
                return true;
            }
            catch (ArgumentException ae)
            {
                // ArgumentException, ArgumentOutOfRangeException i ArgumentNullException najednou
                log.Error("Nemohu nakopirovat cislo do vystupniho bufferu, kopirovaci rutina volana s nevalidnimi parametry", ae);
                return false;
            }
            catch (RankException re)
            {
                // Sem bychom se nemeli nikdy dostat
                log.Error("Nemohu nakopirovat cislo do vystupniho bufferu, pole maji ruzne dimensionality", re);
                return false;
            }
            catch (InvalidCastException ice)
            {
                // Sem bychom se taky nemeli dostat - pracujeme s bajty, ne s vyssimi typy
                log.Error("Hodnoty v polich maji rozdilne typy, nemohu provest kopirovani", ice);
                return false;
            }
        }

        /// <summary>
        /// Zapíše do výstupního bufferu v lokálním kontextu 64bitový integer.
        /// Metoda převede long do big endian bytové reprezentace (přes bytové pole) a v této reprezentaci long uloží do výstupního bufferu.
        /// Pozice ve výstupním bufferu je inkrementována o 8 pozic.
        /// </summary>
        /// 
        /// <remarks>
        /// Metoda nekontroluje přetečení výstupního bufferu. Je na volajícím, aby ověřil, že ve výstupním bufferu zbývá dostatek místa.
        /// Pokud není metoda úspěšná a vrací tedy false, buffer zůstane nezměněný.
        /// </remarks>
        /// 
        /// <param name="number">Číslo, které má být zapsáno do výstupního bufferu.</param>
        private bool AddLongToOutputBuffer(long number)
        {
            // Reference pro výstupní parametr - pole bajtů, do kterého bude vypsán long
            byte[] bytes;
            // Výsledek převedení longu na pole bajtů
            bool longToBytesTransformResult;

            // Převedu na big endian, zapíšu, inkrementuji pozici
            longToBytesTransformResult = LongToBigEndianBytes(out bytes, number);
            if (!longToBytesTransformResult)
            {
                log.Debug("Nepodarilo se prevest predane cislo na big endian pole bajtu");
                return false;
            }
            // Zkopíruji pole bajtů do výstupního bufferu
            try
            {
                Array.Copy(bytes, 0, ctx.outputBuffer, ctx.oPos, 8);
                ctx.oPos += 8;
                return true;
            }
            catch (ArgumentException ae)
            {
                // ArgumentException, ArgumentOutOfRangeException i ArgumentNullException najednou
                log.Error("Nemohu nakopirovat cislo do vystupniho bufferu, kopirovaci rutina volana s nevalidnimi parametry", ae);
                return false;
            }
            catch (RankException re)
            {
                // Sem bychom se nemeli nikdy dostat
                log.Error("Nemohu nakopirovat cislo do vystupniho bufferu, pole maji ruzne dimensionality", re);
                return false;
            }
            catch (InvalidCastException ice)
            {
                // Sem bychom se taky nemeli dostat - pracujeme s bajty, ne s vyssimi typy
                log.Error("Hodnoty v polich maji rozdilne typy, nemohu provest kopirovani", ice);
                return false;
            }
        }

        /// <summary>
        /// Zapíše zadanou zprávu do výstupního bufferu společně s prefixem, který představuje délku zprávy. Prefix je 32bit int, následující zpráva je kódována v ASCII.
        /// Pokud není zápis některé složky do bufferu neúspěšný, metoda vrátí false a výstupní buffer zůstává v nezměněném stavu.
        /// </summary>
        /// <remarks>Pokud není metoda úspěšná a vrací false, buffer zůstane nezměněný.</remarks>
        /// <param name="message">Zpráva, která má být společně s bufferem zapsána do výstupního bufferu.</param>
        /// <returns>True, pokud proběhne zápis prefixu i zprávy do výstupního bufferu úspěšně.</returns>
        private bool AddPrefixedMessageToOutputBuffer(string message)
        {
            // Proměnné.
            // Výsledek zápisu délku zprávy (prefixu) do výstupního bufferu.
            bool messageLengthWriteResult;
            // Délka zprávy.
            int messageLength;
            // Výsledek zápisu textu zprávy do výstupního bufferu.
            bool messageWriteResult;

            // Ověření, že předávaná zpráva není null a nemá nulovou délku
            if (message == null)
            {
                log.Error("Predana zprava je null, neni mozne ji pridat do vystupniho bufferu.");
                return false;
            }
            if (message.Length == 0)
            {
                log.Error("Predana zprava ma nulovou delku, neni mozne ji pridat do vystupniho bufferu.");
                return false;
            }
            // Přidám do výstupního bufferu délku zprávu.
            messageLength = message.Length;
            messageLengthWriteResult = AddIntToOutputBuffer(messageLength);
            if (!messageLengthWriteResult)
            {
                log.Error("Nepodarilo se zapsat delku zpravy (prefix) do vystupniho bufferu");
                return false;
            }
            // Přidám do výstupního bufferu text zprávy.
            messageWriteResult = AddStringToOutputBuffer(message);
            if (!messageWriteResult)
            {
                log.Error("Nepodarilo se zapsat text zpravy do vystupniho bufferu");
                return false;
            }
            // Zapsány obě složky, úspěch.
            return true;
        }

        /// <summary>
        /// Zapíše obsah stringu do výstupního bufferu v ASCII kódování.
        /// </summary>
        /// <remarks>Pokud není metoda úspěšná a vrací false, buffer zůstane nezměněný.</remarks>
        /// <param name="sourceString">String, který má být zapsán do výstupního bufferu. Nesmí být null, nebo obsahovat znaky mimo sadu standardního ASCII.</param>
        /// <returns>True, pokud se povedlo string do výstupního bufferu úspěšně zapsat, jinak false.</returns>
        private bool AddStringToOutputBuffer(string sourceString)
        {
            // Proměnné
            // Výsledek převádění stringu na pole bajtů.
            bool stringToBytesResult;
            // Výsledné pole bajtů do kterého je v ASCII přepsán string.
            byte[] stringBytes;
            // Výsledek kopírování bajtového pole do výstupního bufferu.
            bool arrayCopyResult;

            // Převedu string pole bajtů
            stringToBytesResult = StringToAsciiBytesArray(out stringBytes, sourceString);
            if (!stringToBytesResult)
            {
                log.Error("Nepodarilo se prevest string na pole bajtu, string nemuze byt pridan do vystupniho bufferu.");
                return false;
            }

            // Bezpečně nakopíruji pole bajtů do výstupního bufferu.
            arrayCopyResult = AddByteArrayToOutputBuffer(stringBytes);
            if (!arrayCopyResult)
            {
                log.Error("Nepodarilo se nakopirovat pole bajtu s ASCII znaky do vystupniho buffery.");
                return false;
            }
            // Pole je nakopírované, AddByteArrayToOutputBuffer zvýšila pozici v bufferu = hotovo, vracím úspěch.
            return true;
        }

        /// <summary>
        /// Zapíše do výstupního bufferu v uživatelském kontextu pole bajtů a posune aktuální index první volné pozice v bufferu. Metoda je ošetřena tak, aby nevyhazovala výjimky.
        /// </summary>
        /// <remarks>Pokud není metoda úspěšná a vrací false, buffer zůstane nezměněný.</remarks>
        /// <param name="stringBytes">Pole bajtů, které má být zapsáno do výstupního bufferu. Nesmí být null.</param>
        /// <returns>True, pokud bylo zadané pole úspěšně zapsáno do výstupního bufferu.</returns>
        private bool AddByteArrayToOutputBuffer(byte[] stringBytes)
        {
            // Proměnné
            // Počet bajtů k nakopírování
            int bytesCount;

            // Nakopírování pole bajtů do výstupního bufferu.
            bytesCount = stringBytes.Length;
            // Pridani XML do bufferu
            try
            {
                Array.Copy(stringBytes, 0, ctx.outputBuffer, ctx.oPos, bytesCount);
                this.ctx.oPos += bytesCount;
                return true;
            }
            catch (ArgumentNullException ane)
            {
                log.Error("Vstupni nebo vystupni pole je null", ane);
                return false;
            }
            catch (RankException re)
            {
                log.Error("Dimensionality vstupniho a vystupniho pole nesouhlasi", re);
                return false;
            }
            catch (ArrayTypeMismatchException atme)
            {
                // Toto by nemělo nikdy nastat.
                log.Error("Vstupni a vystupni pole maji rozdilny typ", atme);
                return false;
            }
            catch (InvalidCastException ice)
            {
                // Toto by nemělo nikdy nastat.
                log.Error("Nektere prvky vstupniho a vystupniho pole maji rozdilny typ", ice);
                return false;
            }
            catch (ArgumentOutOfRangeException aoore)
            {
                log.Error("Operace presahuje rozmery poli", aoore);
                return false;
            }
            catch (ArgumentException ae)
            {
                // Toto by nemělo nikdy nastat.
                log.Error("Copy metoda volana s neplatnym length parametrem", ae);
                return false;
            }
        }

        /// <summary>
        /// Převede string na pole bajtů v kódování ASCII. Odchytává potenciální výjimky.
        /// </summary>
        /// <param name="stringBytes">Výstujpní parametr, přes který je předáno výstupní pole bajtů. Pokud se metodě nepovede převést string na pole bajtů, je parametrem vždy vráceno null.</param>
        /// <param name="sourceString">String, který má být převeden na pole bajtů.</param>
        /// <returns>True, pokud se úspěšně povedlo převést string na pole bajtův ASCII, jinak false.</returns>
        private bool StringToAsciiBytesArray(out byte[] stringBytes, string sourceString)
        {
            // Dummy výstupní parametr pro případ neúspěchu
            stringBytes = null;
            // Pokusím se string převést
            try
            {
                stringBytes = Encoding.ASCII.GetBytes(sourceString);
                return true;
            }
            catch (ArgumentNullException ane)
            {
                log.Error("Nemohu prevest string na byte array, zadany string je null", ane);
                return false;
            }
            catch (EncoderFallbackException efe)
            {
                log.Error("Nepodarilo se prevest string na pole bajtu s ASCII kodovanim", efe);
                return false;
            }
        }


        /// <summary>
        /// Zapíše výstupní buffer z klientského kontextu do NetworkStreamu a okamžitě ho odešle.
        /// </summary>
        /// <returns>Pokud dojde k úspěšnému odeslání true, jinak false.</returns>
        private bool SendOutputBuffer()
        {
            log.Debug("Bude odeslat vystupni buffer.");
            log.Debug("Pozice v bufferu pred odeslanim: " + ctx.oPos);
            // Odešlu zprávu
            try
            {
                // Zapíšu do streamu bufferu.
                ctx.socketStream.Write(ctx.outputBuffer, 0, ctx.oPos);
                // NetworkStream není bufferovaný, aktuálně tedy Flush nemá efekt.
                // Pro případ nahrazení bufferovanou verzí zajistí, že je zpráva odeslána okamžitě.
                ctx.socketStream.Flush();
                // Nedostal jsem výjimku, všechno dobré.
                return true;
            }
            catch (ArgumentNullException ane)
            {
                log.Error("Pri odeslani buffferu byl pouzit null argument", ane);
                return false;
            }
            catch (ArgumentOutOfRangeException aoore)
            {
                log.Error("Pri odeslani bufferu byl pouzit neplatny argument", aoore);
                return false;
            }
            catch (IOException ioe)
            {
                log.Info("Pri odeslani bufferu nastala IO chyba", ioe);
                return false;
            }
            catch (ObjectDisposedException ode)
            {
                log.Error("NetworkStream jiz byl odstranen.");
                return false;
            }
        }

        /// <summary>
        /// Vytvoří proxy objekty pro čtení dat z klientského Socketu a uloží je do klientského kontextu.
        /// </summary>
        /// <returns>True pokud byly proxy objekty úspěšně vytvořeny, jinak false.</returns>
        private bool SetUpSocketStreams()
        {
            log.Debug("Nastavuji objekty pro cteni klientskeho socketu.");
            // Streamy pro přijímaní dat.
            // NetworkStream je nastaven, aby vlastnil socket. Samotné čtení socketu probíhá výhradně přes
            // BinaryReader, veškerá textová část komunikaci probíhá v ASCII kódování (ale protokol je v základě binární).
            try
            {
                NetworkStream socketStream = new NetworkStream(this.ctx.socket, true);
                BinaryReader binReader = new BinaryReader(socketStream, Encoding.ASCII);
                ctx.socketStream = socketStream;
                ctx.binReader = binReader;
                log.Debug("Proxy objekty NetworkStream a BinaryReader pro cteni klientskeho Socketu uspesne vytvoreny.");
                return true;
            }
            catch (ArgumentException ane)
            {
                log.Error("Pokus o vytvoreni NetworkStream nebo BinaryStream s neplatnymi argumenty.", ane);
                return false;
            }
            catch (IOException ioe)
            {
                log.Error("Chyba pri napojovani Socketu na objekty pro cteni dat.", ioe);
                return false;
            }
        }

        /// <summary>
        /// Přečte jeden byte z klientského socketu. 
        /// Jsou odchyceny všechny potenciální výjimky, tudíž z metody při selhání nevypadne výjimka, ale je vracena hodnota false.
        /// </summary>
        /// <param name="b">Výstupní parametr obsahující hodnotu přijatého byte. Pokud metoda selže a vrací false, je vrácena fixně hodnota 0.</param>
        /// <returns>True, pokud byl úspěšně přijat byte zprávy od klienta, jinak false.</returns>
        private bool ReadSafelyByteFromSocket(out byte b)
        {
            // Nastavím načtený bajt na dummy hodnotu, abych vrátil hodnotu i při selhání.
            b = 0;
            // Pokusím se přečíst byte z klientského socketu
            try
            {
                log.Debug("Cekam na nacteni bajtu ze socketu.");
                b = ctx.binReader.ReadByte();
                log.Debug("Prijata novy bajt od klienta: " + b);
                return true;
            }
            catch (EndOfStreamException eose)
            {
                log.Warn("Bylo dosazeno konce streamu z klientskeho socketu", eose);
                return false;
            }
            catch (ObjectDisposedException ode)
            {
                log.Warn("Klientsky socket byl uzavren", ode);
                return false;
            }
            catch (IOException ioe)
            {
                log.Error("Pri cteni z klientskeho socketu doslo k IO chybe", ioe);
                return false;
            }
        }

        /// <summary>
        /// Přečte ze socketu string zadané délky, jehož znaky jsou kódované v ASCII.
        /// </summary>
        /// <param name="s">Výstupní parametr, do kterého je načten string ze socketu. Pokud je čtení stringu neúspěšné (metoda vrací false), parametr je nastaven vždy na null.</param>
        /// <param name="sLength">Délka načítaného stringu. Musí jít o kladné číslo.</param>
        /// <returns>True, pokud byl řetězec úspěšně načten ze socketu a uložen do výstupního parametru, jinak false.</returns>
        private bool ReadSafelyAsciiStringFromSocket(out string s, int sLength)
        {
            // Proměnné.
            // Pole načtených bajtů ze socketu.
            byte[] byteArray;
            // Výsledek čtení pole bajtů ze socketu.
            bool byteArrayReadResult;

            // Dummy hodnota výstupního parametru pro případ, že metoda selže a vrací false.
            s = null;
            // Načtu sLength bajtů ze socketu.
            byteArrayReadResult = ReadSafelyBytesFromSocket(out byteArray, sLength);
            if (!byteArrayReadResult)
            {
                log.Error("Nepodarilo se nacist bajty stringu ze socketu.");
                return false;
            }
            // Převedu pole bajtů na ASCII string.
            try
            {
                s = Encoding.ASCII.GetString(byteArray);
                return true;
            }
            catch (ArgumentNullException ane)
            {
                // To by nemělo nikdy nastat.
                log.Error("Nepodaril se prevod pole bajtu na string, pole bajtu je null", ane);
                return false;
            }
            catch (ArgumentException ae)
            {
                // Tato vetev odchyti i dalsi moznou DecoderFallbackException.
                log.Error("Nepodarilo se prevest pole bajtu na string, pole bajtu obsahuje neplatne Unicode znaky", ae);
                return false;
            }
        }

        /// <summary>
        /// Přečte zadaný počet bajtů z klientského socketu.
        /// Metoda odchytává všechny potenciální výjimky plynoucí ze síťové komunikace.
        /// V případě neúspěchu proto vrátí false, ale nevyhodí výjimku.
        /// </summary>
        /// <param name="bytes">Výstupní parametr, přes který je předáno pole s bajty načtenými ze socketu. Metoda si alokuje vlastní pole, stačí tedy předat referenci.</param>
        /// <param name="numberOfBytes">Počet bajtů, které mají být načteny ze socketu. Musí jít o kladné číslo.</param>
        /// <returns>True, pokud byl zadaný počet bajtů úspěšně načten ze socketu. Samotná data jsou předána výstupním parametrem.</returns>
        private bool ReadSafelyBytesFromSocket(out byte[] bytes, int numberOfBytes)
        {
            // Načtu výstupní parametr na dummy hodnotu, aby byl splněn kontrakt i při selhání čtení ze socketu.
            bytes = null;
            // Pokusím se načíst bajty z klientského socketu.
            try
            {
                log.Debug("Cekam na nacteni " + numberOfBytes + " z klientskeho socketu.");
                bytes = ctx.binReader.ReadBytes(numberOfBytes);
                log.Debug("Ze socketu uspesne nacteno " + numberOfBytes + " bajtu.");
                return true;
            }
            catch (ArgumentOutOfRangeException aoore)
            {
                log.Error("Volajici se pokousi nacist negativni pocet bajtu ze socketu", aoore);
                return false;
            }
            catch (ArgumentException ae)
            {
                // Toto by nemelo nikdy nestat, pracujeme s daty na bazi bajtu, nepouzivame abstrakci kodovani
                log.Error("Pocet bajtu k nacteni nesedi s potrebnym poctem bajtu k nacteni vzhledem k unicode kodovani", ae);
                return false;
            }
            catch (IOException ioe)
            {
                log.Error("Nastal IO problem pri cteni dat ze socketu", ioe);
                return false;
            }
            catch (ObjectDisposedException ode)
            {
                log.Warn("Klientsky socket byl uzavren", ode);
                return false;
            }
        }

        /// <summary>
        /// Zavře všechny prostředky, kterými ClientHandler disponuje.
        /// Jde o otevřené komunikační prostředky (Sockety, Readery) a vypůjčené kamery.
        /// </summary>
        /// <returns>True, pokud byly všechny prostředky korektně vrácené a uzavřené, jinak false.</returns>
        private bool DisposeAllResources()
        {
            // Výsledky uzavírání prostředků.
            bool socketResourcesClosingResult;
            bool cameraResourcesClosingResult;

            // Zavřu prostředky Socketu.
            socketResourcesClosingResult = DisposeSocketResources();
            if (!socketResourcesClosingResult)
            {
                log.Error("Nepodarilo se korektne uzavrit prostredky vyuzivane pri komunikaci s klientem.");
            }

            // Zavřu kamerové prostředky.
            cameraResourcesClosingResult = DisposeCameraResources();
            if (!cameraResourcesClosingResult)
            {
                log.Error("Nepodarilo se korektne uzavrit prostredky vyuzivane pri cteni dat z kamer!");
            }

            // Předám výsledek uzavírání.
            if (socketResourcesClosingResult && cameraResourcesClosingResult)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Uzavře komunikační prostředky související s klientským Socketem a vrátí používané prostředky.
        /// Po volání této metody již není možné znovuotevřít komunikační kanál (přes stejné objekty).
        /// </summary>
        /// <returns>Vždy true. C Sharp neindikuje, zda uzavření a vrácení prostředků bylo úspěšné.</returns>
        private bool DisposeSocketResources()
        {
            log.Debug("Vracim prostredky klienta pouzivane pro sitovou komunikaci.");
            // Uzavřu BinaryReader, ten uzvaře vnořený StreamReader, ten uzavře vnořený Socket.
            // C Sharp API při uzavírání Readerů nevyhazuje výjimky, návratová hodnota je zde tedy pouze pro konzistenci s dalšími Dispose metodami.
            if (ctx.binReader != null)
            {
                ctx.binReader.Dispose();
            }
            // Jakákoliv další práce skončí NullPointerException
            ctx.socket = null;
            ctx.socketStream = null;
            ctx.binReader = null;
            ctx.outputBuffer = null;
            ctx.oPos = 0;
            // "Bylo uděláno maximum"
            return true;
        }

        /// <summary>
        /// Vrátí všechny kamery zamčené klienty. 
        /// Tyto kamery jsou vráceny do ServerContext a jsou následovně použitelné dalšími klienty.
        /// </summary>
        /// <returns>True, pokud navrácení všech zamčených kamer proběhlo úspěšně, jinak false.</returns>
        private bool DisposeCameraResources()
        {
            log.Debug("Vracim kamery zamcene klientem.");
            // Výsledek uzavírání kamer
            bool singleCameraClosingResult;
            bool allCamerasClosingResult;

            // Odemknu každou kameru. 
            // Všechny výjimky padající z Pylonu jsou odchyceny metodou UnlockCameras.
            allCamerasClosingResult = true;
            //foreach (KeyValuePair<int, Camera> entry in ctx.cameras)
            //{
            //    log.Debug("Vracim kameru s globalnim ID " + entry.Value);
            //    singleCameraClosingResult = serverCtx.UnlockCamera(entry.Value, entry.Key);
            //    // Pokud jde o první kameru, kde uzavírání selhalo, přehodím flag, který reprezentuje celkový výsledek.
            //    if (allCamerasClosingResult && !singleCameraClosingResult)
            //    {
            //        log.Warn("Vraceni kamery s globalnim ID " + entry.Value + " se nezdarilo, celkovy vysledek operace vraceni kamer klienta bude neuspesny.");
            //        allCamerasClosingResult = false;
            //    }
            //}
            // Smažu všechny klíče a znemožním další práci.
            ctx.cameras.Clear();
            ctx.cameras = null;
            // Smažu kontext pro continous grabbing
            ctx.continousGrabProvider = null;
            ctx.continuoslyGrabbingCameraId = -1;
            ctx.maxImageId = 0;
            ctx.maxAcknowledgedImageId = 0;
            // Výsledek
            return allCamerasClosingResult;
        }

        /// <summary>
        /// Převede 32bit signed integer na pole 4 bajtů v pořadí big endian.
        /// </summary>
        /// <param name="bytes">Výstupní parametr, přes které je předáno pole 4 bajtů v uspořádání big endian.</param>
        /// <param name="number">Číslo, které má být převedeno na pole bajtů.</param>
        /// <returns>Vždy vrátí true. Metoda vrací bool kvůli konsistenci s dalšími metodami (a pro případné budoucí změny).</returns>
        private bool IntToBigEndianBytes(out byte[] bytes, int number)
        {
            // Získáme pole 4 bajtů reprezentující číslo.
            bytes = BitConverter.GetBytes(number);
            // Pokud pracujeme v little endian, otočíme pořadí bajtů.
            if (BitConverter.IsLittleEndian)
            {
                // Zde nemůže otočení selhat, protože předchozí metoda GetBytes garantuje navrat 4 prvkového pole.
                Array.Reverse(bytes);
            }
            // Máme převedený int na pole 4 bajtů ve správné endianitě.
            return true;
        }

        /// <summary>
        /// Převede big endian pole 4 bajtů na 32bit signed integer.
        /// Metoda je odolná vůči chybám, v případě selhání vrátí false, ale nevyhodí výjimku.
        /// </summary>
        /// <param name="number">Výstupní parametr s převedeným číslem. Pokud metoda selže a vrátí false, tento argument bude vždy nastaven na 0.</param>
        /// <param name="bytes">Pole 4 bajtů, které mají být převedeny na integer. Metoda počítá s tím, že uspořádání bajtů je big endian.</param>
        /// <returns>True, pokuid převod pole bajtů na integer proběhnul úspěšně, jinak false. Převedené číslo je předávané výstupním parametrem number.</returns>
        private bool BigEndianBytesToInt(out int number, byte[] bytes)
        {
            // Dummy výstupní hodnota pro případ, že převádění selže.
            number = 0;
            // Pokud pracujeme v little endian, otočíme pole bajtů.
            if (BitConverter.IsLittleEndian)
            {
                try
                {
                    Array.Reverse(bytes);
                }
                catch (ArgumentNullException ane)
                {
                    log.Error("Predane pole bajtu je null.", ane);
                    return false;
                }
                catch (RankException re)
                {
                    log.Error("Predane pole bajtu je multidimenzionalni.", re);
                    return false;
                }
            }
            // Pole ve správně endianitě převedeme na 32bit int.
            try
            {
                number = BitConverter.ToInt32(bytes, 0);
                return true;
            }
            // Deklarovaná výjimka ArgumentNullException nemůže nastat,
            // protože pole bytes v této části již nemůže být null (odchyceno na předchozím try-catch).
            // (a případně jde o podtřídu ArgumentException :), stejně jako ArgumentOutOfRangeException).
            catch (ArgumentException ae)
            {
                log.Error("Predane pole bajtu nemuze byt prevedeno na 32bit int", ae);
                return false;
            }
        }

        /// <summary>
        /// Převede 64bit signed long na pole 8 bajtů v pořadí big endian.
        /// </summary>
        /// <param name="bytes">Výstupní parametr, přes které je předáno pole 8 bajtů v uspořádání big endian.</param>
        /// <param name="number">Číslo, které má být převedeno na pole bajtů.</param>
        /// <returns>Vždy vrátí true. Metoda vrací bool kvůli konsistenci s dalšími metodami (a pro případné budoucí změny).</returns>
        private bool LongToBigEndianBytes(out byte[] bytes, long number)
        {
            // Získáme pole 8 bajtů reprezentující číslo.
            bytes = BitConverter.GetBytes(number);
            // Pokud pracujeme v little endian, otočíme pořadí bajtů.
            if (BitConverter.IsLittleEndian)
            {
                // Zde nemůže otočení selhat, protože předchozí metoda GetBytes garantuje návrat 8 prvkového pole.
                Array.Reverse(bytes);
            }
            // Máme převedený long na pole 8 bajtů ve správné endianitě.
            return true;
        }

        /// <summary>
        //// Převede big endian pole 8 bajtů na 64bit signed integer.
        /// Metoda je odolná vůči chybám, v případě selhání vrátí false, ale nevyhodí výjimku.
        /// </summary>
        /// <param name="number">Výstupní parametr s převedeným číslem. Pokud metoda selže a vrátí false, tento argument bude vždy nastaven na 0.</param>
        /// <param name="bytes">Pole 8 bajtů, které mají být převedeny na 64bit long. Metoda počítá s tím, že uspořádání bajtů je big endian.</param>
        /// <returns>True, pokud převod pole bajtů na 64bit long proběhnul úspěšně, jinak false. Převedené číslo je předávané výstupním parametrem number.</returns>
        private bool BigEndianBytesToLong(out long number, byte[] bytes)
        {
            // Dummy výstupní hodnota pro případ, že převádění selže.
            number = 0;
            // Pokud pracujeme v little endian, otočíme pole bajtů.
            if (BitConverter.IsLittleEndian)
            {
                try
                {
                    Array.Reverse(bytes);
                }
                catch (ArgumentNullException ane)
                {
                    log.Error("Predane pole bajtu je null.", ane);
                    return false;
                }
                catch (RankException re)
                {
                    log.Error("Predane pole bajtu je multidimenzionalni.", re);
                    return false;
                }
            }
            // Pole ve správně endianitě převedeme na 64bit long.
            try
            {
                number = BitConverter.ToInt64(bytes, 0);
                return true;
            }
            // Deklarovaná výjimka ArgumentNullException nemůže nastat,
            // protože pole bytes v této části již nemůže být null (odchyceno na předchozím try-catch).
            // (a případně jde o podtřídu ArgumentException :), stejně jako ArgumentOutOfRangeException).
            catch (ArgumentException ae)
            {
                log.Error("Predane pole bajtu nemuze byt prevedeno na 64bit int", ae);
                return false;
            }
        }

        /* ==============================================================================================
         * SPECIÁLNÍ METODY VOLANÉ Z GRABBOVACÍHO VLÁKNA.
         * ==============================================================================================
         */
        /// <summary>
        /// Odešle klientovi snímek jako FRAME | FRAME_ID | TIMESTAMP | FRAME_DATA.
        /// </summary>
        /// <param name="frameId">ID zasísaleného snímku.</param>
        /// <param name="array">Data snímku.</param>
        public bool SendFrame(int frameId, long timestamp, byte[] array)
        {
            // Výsledek odeslání snímku.
            bool sendResult;
            // Výsledek zapsání snímku do výstupního bufferu.
            bool bufferWriteResult;

            // Ověřím, zda nebylo spojení s klientem již zrušeno (mám kam zapsat snímek?)
            if (ctx.outputBuffer == null)
            {
                log.Error("Nemohu odeslat snimek, vystupni buffer jiz byl zrusen.");
            }
            // Vynuluji pozici v bufferu.
            ctx.oPos = 0;
            // Zapíšu FRAME code, délku snímku a samotný snímek.
            bufferWriteResult = WriteFrameToOutputBuffer(frameId, timestamp, array);
            if (!bufferWriteResult)
            {
                log.Error("Nepodarilo se zapsat snimek do vystupniho bufferu.");
                return false;
            }

            // Odešlu snímek.
            sendResult = SendOutputBuffer();
            if (!sendResult)
            {
                log.Error("Nepodarilo se odeslat buffer se snimkem.");
                return false;
            }
            // Snímek odeslán.
            return true;
        }

        /// <summary>
        /// Odešle chybové hlášení z grabbovacího vlákna.
        /// </summary>
        /// <param name="errorMessage">Znění chybového hlášení, nesmí být null.</param>
        public void SendGrabbingError(string errorMessage)
        {
            // Ověřím, zda nebylo spojení s klientem již zrušeno (mám kam zapsat snímek?)
            if (ctx.outputBuffer == null)
            {
                log.Error("Nemohu odeslat chybovou zpravu, vystupni buffer jiz byl zrusen.");
                return;
            }
            // Odešlu zprávu.
            SendGrabbingErrorMessage(errorMessage);
        }
        /*==============================================================================================
         *==============================================================================================
         */
         
    }
}
