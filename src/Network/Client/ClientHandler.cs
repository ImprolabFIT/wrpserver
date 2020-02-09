using System;
using System.IO;
using System.Threading;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using WRPServer.Network.Enum;
using WRPServer.Network.Server;
using WRPServer.Cameras;
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
            ctx.grabber = new WICGrabber(this);
            // Konec inicializace.


            bool actionResult = true;
            // Přijatá akce od klienta.
            byte receivedAction;
            
            // Výsledek odeslání bufferu klientovi.
            bool sendingBufferResult;

            // Centrální smyčka obsluhy.
            // Nastavím pokračování komunikace - flag je přepnut při dobrovolném vypnutí kanálu klientem.
            ctx.continueCommunication = true;
            // Nastavím v kontextu prvotní stav
            ctx.clientState = ClientState.CONNECTED;
            ctx.receivedMessageType = MessageType.NOT_ASSIGNED;
            while (true)
            {
                // Začátek běžné smyčky.
                // Přečtení požadované akce od klienta.
                if (WaitingForMessageInState(ctx.clientState))
                {
                    if (!ReadSafelyByteFromSocket(out receivedAction))
                    {
                        log.Error("Nepodarilo se precist pozadovanou akci od klienta, ukoncuji obsluhu klienta.");
                        DisposeAllResources();
                        return;
                    }
                    ctx.receivedMessageType = (MessageType)receivedAction;
                    log.Debug("Prijata zprava typu " + ctx.receivedMessageType.ToString());
                    if (!ReadSafelyUInt32FromSocket(out ctx.receivedPayloadLength))
                    {
                        log.Error("Nepodarilo se precist delku payloadu v prichozi zprave typu " + ctx.receivedMessageType.ToString());
                        return;
                    }
                }

                // Na základě aktuálního stavu je rozhodnuto o zpracování akce.
                log.Debug("Aktualni stav ClientHandler je " + ctx.clientState.ToString());
                switch (ctx.clientState)
                {
                    case ClientState.CONNECTED:
                        if(ctx.receivedMessageType == MessageType.GET_CAMERA_LIST)
                        {
                            ctx.nextState = ClientState.GET_CAMERA_LIST;
                        }
                        else if(ctx.receivedMessageType == MessageType.OPEN_CAMERA)
                        {
                            ctx.nextState = ClientState.OPEN_CAMERA;
                        }
                        else
                        {
                            log.Error("Nepodarilo se nalezt obsluhu ze stavu "+ctx.clientState.ToString()+" pro zpravu "+ ctx.receivedMessageType.ToString());
                            actionResult = false;
                        }
                        break;
                    case ClientState.GET_CAMERA_LIST:
                        actionResult = HandleGetCameraListState();
                        break;
                    case ClientState.OPEN_CAMERA:
                        actionResult = HandleOpenCameraState();
                        break;
                    case ClientState.CLOSE_CAMERA:
                        actionResult = HandleCloseCameraState();
                        break;
                    case ClientState.CAMERA_SELECTED:
                        if (ctx.receivedMessageType == MessageType.CLOSE_CAMERA)
                        {
                            ctx.nextState = ClientState.CLOSE_CAMERA;
                        }
                        else if (ctx.receivedMessageType == MessageType.GET_FRAME)
                        {
                            ctx.nextState = ClientState.GET_FRAME;
                        }
                        else if (ctx.receivedMessageType == MessageType.START_CONTINUOUS_GRABBING)
                        {
                            ctx.nextState = ClientState.START_CONTINUOUS_GRABBING;
                        }
                        else
                        {
                            log.Error("Nepodarilo se nalezt obsluhu ze stavu "+ctx.clientState.ToString()+" pro zpravu "+ ctx.receivedMessageType.ToString());
                            actionResult = false;
                        }
                        break;
                    case ClientState.GET_FRAME:
                        actionResult = HandleGetFrameState();
                        break;
                    case ClientState.START_CONTINUOUS_GRABBING:
                        actionResult = HandleStartContinuousGrabbingState();
                        break;
                    case ClientState.STOP_CONTINUOUS_GRABBING:
                        actionResult = HandleStopContinuousGrabbingState();
                        break;
                    case ClientState.CONTINUOUS_GRABBING:
                        // If message is ready to receive, check it
                        // If the received message is ACK_CONT_GRAB, continue
                        // Otherwise check if
                        if (ctx.receivedMessageType == MessageType.STOP_CONTINUOUS_GRABBING)
                        {
                            ctx.nextState = ClientState.STOP_CONTINUOUS_GRABBING;
                        }
                        else if (ctx.receivedMessageType == MessageType.ACK_CONTINUOUS_GRABBING)
                        {
                            actionResult = HandleACKContinuousGrabbingMessage();
                        }
                        else
                        {
                            log.Error("Nepodarilo se nalezt obsluhu ze stavu " + ctx.clientState.ToString() + " pro zpravu " + ctx.receivedMessageType.ToString());
                            actionResult = false;
                        }
                        break;
                    default:
                        // Sem bych se nikdy neměl dostat.
                        log.Error("ClientState je v neznamem stavu " + ctx.clientState.ToString()+".");
                        actionResult = false;
                        break;
                }
                log.Debug("Vysledek zpracovani akce: " + actionResult);
                if(!actionResult)
                {
                    log.Error("Obsluha pozadavku se nepodarila a komunikace bude ukoncena.");
                    DisposeAllResources();
                    return;   
                }

                // Pokud byla v ramci obsluhy vytvorena zprava, posli ji
                if(ctx.bufferReadyToSend)
                {
                    sendingBufferResult = SendOutputBuffer();
                    if (!sendingBufferResult)
                    {
                        log.Error("Zpravu se nepodarilo odeslat a komunikace bude ukoncena.");
                        DisposeAllResources();
                        return;
                    }

                    ctx.oPos = 0;
                    ctx.bufferReadyToSend = false;
                }
                // Prejdi do nasledujici stavu ulozeneho v ctx
                log.Debug("Posun ze stavu "+ctx.clientState.ToString()+" do stavu "+ctx.nextState.ToString());
                ctx.clientState = ctx.nextState;
            }
        }

        private bool WaitingForMessageInState(ClientState state)
        {
            return (
                state == ClientState.CONNECTED ||
                state == ClientState.CAMERA_SELECTED ||
                state == ClientState.CONTINUOUS_GRABBING
                );
        }
        
        private bool HandleGetCameraListState()
        {
            if(ctx.receivedPayloadLength != 0)
            {
                log.Error("Chybna delka payloadu pro zpravu typu "+ctx.receivedMessageType.ToString()+" by mela byt 0, ale je "+ctx.receivedPayloadLength);
                return false;
            }

            if(!AddByteToOutputBuffer((byte)MessageType.CAMERA_LIST))
            {
                log.Error("Nepodarilo se zapsat typ zpravy do vystupniho bufferu.");
                return false;
            }

            string sourceString = CameraManager.GetCameraListXML();
            UInt32 payloadLength = (UInt32)sourceString.Length; 
            if(!AddUInt32ToOutputBuffer(payloadLength))
            {
                log.Error("Nepodarilo se zapsat velikost "+payloadLength+" payloadu zpravy CAMERA_LIST do bufferu");
                return false;
            }
            
            log.Debug("Velikost payloadu pro zpravu typu CAMERA_LIST je "+ sourceString.Length);

            if (!AddStringToOutputBuffer(sourceString))
            {
                log.Warn("Nepodarilo se zapsat retezec obsahujici seznam kamer v XML do zpravy");
                return false;
            }
            ctx.nextState = ClientState.CONNECTED;
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool HandleOpenCameraState()
        {
            log.Debug("Zprava typu OpenCamera ma delku payloadu " + ctx.receivedPayloadLength);

            string serialNumber;
            bool readSerialNumber = ReadSafelyAsciiStringFromSocket(out serialNumber, (int)ctx.receivedPayloadLength);
            if (!readSerialNumber)
            {
                log.Warn("Nepodarilo se precist seriove cislo kamery o pozadovane delce "+ctx.receivedPayloadLength);
                return false;
            }
            Camera cam = CameraManager.GetCameraBySerialNumber(serialNumber);
            if(cam is null)
            {
                log.Warn("Nepodarilo se nalezt kameru se seriovym cislem "+serialNumber);
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_FOUND))
                {
                    log.Error("Nepodarilo se zapsat chybovou zpravu o nenalezeni kamery se seriovym cislem "+serialNumber);
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                ctx.bufferReadyToSend = true; 
            }
            ctx.grabber.AssignCameraDevice(cam);
            ctx.grabber.Connect(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
            if (!cam.IsConnected)
            {
                log.Warn("Nepodarilo se pripojit ke kamere se seriovym cislem " + serialNumber);
                ctx.grabber.Disconnect(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                ctx.grabber.UnassignCameraDevice();
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_RESPONDING))
                {
                    log.Error("Nepodarilo se zapsat chybovou zpravu " + ErrorCode.CAMERA_NOT_RESPONDING.ToString());
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                ctx.bufferReadyToSend = true;
                return true;
            }
            log.Debug("Kamera "+serialNumber+" byla pripojena");

            if(!FillBufferWithOkMessage())
            {
                log.Error("Nepodarilo se zapsat OK zpravu do vystupniho bufferu.");
                return false;
            }
            ctx.nextState = ClientState.CAMERA_SELECTED;
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool HandleCloseCameraState()
        {
            if (ctx.receivedPayloadLength != 0)
            {
                log.Error("Chybna delka payloadu pro zpravu typu " + ctx.receivedMessageType.ToString() + " by mela byt 0, ale je " + ctx.receivedPayloadLength);
                return false;
            }
            if(!ctx.grabber.HasCameraAssigned())
            {
                if(!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_OPEN))
                {
                    log.Error("Nebyla prirazena kamera a nepodarilo se poslat chybovou zpravu. Do tohoto stavu bych se nemel vubec dostat");
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                return true;
            }
            ctx.grabber.Disconnect(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
            ctx.grabber.UnassignCameraDevice();
            if(!FillBufferWithOkMessage())
            {
                log.Error("Nepodarilo se zapsat OK zpravu do bufferu");
                return false;
            }
            ctx.nextState = ClientState.CONNECTED;
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool HandleGetFrameState()
        {
            log.Debug("Zpracovavam pozadavek na jeden snimek.");

            if (ctx.receivedPayloadLength != 0)
            {
                log.Error("Chybna delka payloadu pro zpravu typu " + ctx.receivedMessageType.ToString() + " by mela byt 0, ale je " + ctx.receivedPayloadLength);
                return false;
            }
            if (!ctx.grabber.HasCameraAssigned())
            {
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_OPEN))
                {
                    log.Error("Nebyla prirazena kamera a nepodarilo se poslat chybovou zpravu. Do tohoto stavu bych se nemel vubec dostat");
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                return true;
            }

            float[] temperatures;
            UInt64 timestamp;
            UInt16 height, width;
            if (!ctx.grabber.StartAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS))
            {
                log.Warn("Akvizace kamery nebyla spustena do timeoutu");
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_ACQUIRING))
                {
                    log.Error("Nepodarilo se zapsat chybovou hlášku");
                    return false;
                }
                ctx.nextState = ClientState.CAMERA_SELECTED;
                ctx.bufferReadyToSend = true;
                return true;
            }
            if (!ctx.grabber.SingleFrame(out timestamp, out height, out width, out temperatures, WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS))
            {
                log.Warn("Nepodarilo se nacist single frame z kamery");
                if(!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_RESPONDING))
                {
                    log.Error("Nepodarilo se zapsat chybovou hlášku");
                    return false;
                }
                ctx.nextState = ClientState.CAMERA_SELECTED;
                ctx.bufferReadyToSend = true;
            }
            log.Debug("Ziskali jsme snimek!");
            if (!ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS))
            {
                log.Warn("Snimek byl ziskan, ale nepodarilo se vypnout akvizici");
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_RESPONDING))
                {
                    log.Error("Nepodarilo se zapsat chybovou hlášku");
                    return false;
                }
                ctx.nextState = ClientState.CAMERA_SELECTED;
                ctx.bufferReadyToSend = true;
                return true;
            }
            // Máme snímek, zapíšu ho do výstupního bufferu.
            UInt32 frameId = 0;
            if(!WriteFrameToOutputBuffer(frameId, timestamp, height, width, temperatures))
            {
                log.Error("Nepodarilo se zapsat snimek do vystupniho bufferu.");
                return false;
            }
            ctx.nextState = ClientState.CAMERA_SELECTED;
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool HandleStartContinuousGrabbingState()
        {
            log.Debug("Zpracovavam pozadavek na spusteni kontinualniho snimani.");

            if (ctx.receivedPayloadLength != 0)
            {
                log.Error("Chybna delka payloadu pro zpravu typu " + ctx.receivedMessageType.ToString() + " by mela byt 0, ale je " + ctx.receivedPayloadLength);
                return false;
            }
            if (!ctx.grabber.HasCameraAssigned())
            {
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_OPEN))
                {
                    log.Error("Nebyla prirazena kamera a nepodarilo se poslat chybovou zpravu. Do tohoto stavu bych se nemel vubec dostat");
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                return true;
            }
            if (!ctx.grabber.StartAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS))
            {
                log.Warn("Nepodarilo se spustit akvizici kamery");
                ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_ACQUIRING))
                {
                    log.Error("Nepodarilo se zapsat chybovou hlasku o timeoutu pri spusteni akvizice");
                    return false;
;               }
                ctx.nextState = ClientState.CAMERA_SELECTED;
                ctx.bufferReadyToSend = true;
                return true;
            }
            if(!ctx.grabber.StartGrabbing())
            {
                log.Error("Nepodarilo se vytvorit grabbovaci vlakno");
                return false;
            }
            if (!FillBufferWithOkMessage())
            {
                ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                log.Error("Nepodarilo se zapsat OK zpravu");
                return false;
            }
            ctx.nextState = ClientState.CONTINUOUS_GRABBING;
            ctx.bufferReadyToSend = true;
            return true;

        }

        private bool HandleStopContinuousGrabbingState()
        {
            log.Debug("Zpracovavam pozadavek na zastaveni kontinualniho snimani.");

            if (ctx.receivedPayloadLength != 0)
            {
                log.Error("Chybna delka payloadu pro zpravu typu " + ctx.receivedMessageType.ToString() + " by mela byt 0, ale je " + ctx.receivedPayloadLength);
                return false;
            }
            if (!ctx.grabber.HasCameraAssigned())
            {
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_OPEN))
                {
                    log.Error("Nebyla prirazena kamera a nepodarilo se poslat chybovou zpravu. Do tohoto stavu bych se nemel vubec dostat");
                    return false;
                }
                ctx.nextState = ClientState.CONNECTED;
                return true;
            }
            ctx.grabber.StopGrabbing();
            if (!ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS))
            {
                log.Warn("Nepodarilo se zastavit akvizici kamery");
                if (!WriteErrorToBuffer(ErrorCode.CAMERA_NOT_RESPONDING))
                {
                    ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                    log.Error("Nepodarilo se zapsat chybovou hlasku o timeoutu pri zastaveni akvizice");
                    return false;
                }
                ctx.nextState = ClientState.CONTINUOUS_GRABBING;
                ctx.bufferReadyToSend = true;
                return true;
            }
            if (!FillBufferWithOkMessage())
            {
                ctx.grabber.StopAcquisition(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                log.Error("Nepodarilo se zapsat OK zpravu");
                return false;
            }
            ctx.nextState = ClientState.CAMERA_SELECTED;
            ctx.bufferReadyToSend = true;
            return true;
        }

        private bool HandleACKContinuousGrabbingMessage()
        {
            throw new NotImplementedException();   
        }

//---------------------------------------------------------------------------------------------
// High-level functions for writing messages buffer
//---------------------------------------------------------------------------------------------

        private bool FillBufferWithOkMessage()
        {
            if (!AddByteToOutputBuffer((byte)MessageType.OK))
            {
                log.Error("Nepodarilo se zapsat typ zpravy "+MessageType.OK.ToString()+" do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt32ToOutputBuffer(0))
            {
                log.Error("Nepodarilo se zapsat velikost payloadu (0) pro " + MessageType.OK.ToString() + " zpravu");
                return false;
            }
            return true;
        }
        private bool WriteErrorToBuffer(ErrorCode errorCode)
        {
            if (!AddByteToOutputBuffer((byte)MessageType.ERROR))
            {
                log.Error("Nepodarilo se zapsat typ zpravy do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt32ToOutputBuffer(1))
            {
                log.Error("Nepodarilo se zapsat velikost payloadu pro chybovou hlasku.");
                return false;
            }
            if (!AddByteToOutputBuffer((byte)errorCode))
            {
                log.Error("Nepodarilo se zapsat chybovy kod do vystupniho bufferu.");
                return false;
            }
            return true;
        }

        private bool WriteFrameToOutputBuffer(UInt32 frameId, UInt64 timestamp, UInt16 height, UInt16 width, float[] temperatures)
        {
            byte[] bytes = null;
            FloatArrayToBigEndianByteArray(out bytes, temperatures);
            // Zápis do bufferu.
            if (!AddByteToOutputBuffer((byte)MessageType.FRAME))
            {
                log.Error("Nepodarilo se zapsat typ zpravy do vystupniho bufferu.");
                return false;
            }
            UInt32 payloadLength = Convert.ToUInt32(4 + 8 + 2 + 2 + bytes.Length);
            if (!AddUInt32ToOutputBuffer(payloadLength))
            {
                log.Error("Nepodarilo se zapsat velikost payloadu do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt32ToOutputBuffer(frameId))
            {
                log.Error("Nepodarilo se zapsat frameId do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt64ToOutputBuffer(timestamp))
            {
                log.Error("Nepodarilo se zapsat timestamp do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt16ToOutputBuffer(height))
            {
                log.Error("Nepodarilo se zapsat vysku snimku do vystupniho bufferu.");
                return false;
            }
            if (!AddUInt16ToOutputBuffer(width))
            {
                log.Error("Nepodarilo se zapsat sirku snimku do vystupniho bufferu.");
                return false;
            }
            if(!AddByteArrayToOutputBuffer(bytes))
            {
                log.Error("Nepodarilo se zapsat zakodovane teploty do vystupniho bufferu.");
                return false;
            }
            return true;
        }

//---------------------------------------------------------------------------------------------
// Low-level functions for writing datatypes to message buffer
//---------------------------------------------------------------------------------------------

        private bool AddByteToOutputBuffer(byte b)
        {
            if(ctx.oPos >= ctx.outputBuffer.Length - 1)
            {
                log.Error("Vystupni buffer byl preplnen a neni mozne poslat celou zpravu");
                return false;
            }
            ctx.outputBuffer[ctx.oPos] = b;
            ctx.oPos += 1;
            return true;
        }

        private bool AddUInt16ToOutputBuffer(UInt16 number)
        {
            byte[] intBytes;

            UInt16ToBigEndianBytes(out intBytes, number);
            bool byteArrayToBufferResults = AddByteArrayToOutputBuffer(intBytes);
            if (!byteArrayToBufferResults)
            {
                log.Error("Podarilo se prevest UInt16 na bajty, ale uz ne zapsat bajty do vystupniho bufferu.");
                return false;
            }
            return true;
        }

        private bool AddUInt32ToOutputBuffer(UInt32 number)
        {
            byte[] intBytes;
            
            UInt32ToBigEndianBytes(out intBytes, number);
            bool byteArrayToBufferResults = AddByteArrayToOutputBuffer(intBytes);
            if(!byteArrayToBufferResults)
            {
                log.Error("Podarilo se prevest UInt32 na bajty, ale uz ne zapsat bajty do vystupniho bufferu.");
                return false;
            }
            return true;
        }

        private bool AddUInt64ToOutputBuffer(UInt64 number)
        {
            byte[] intBytes;
            
            UInt64ToBigEndianBytes(out intBytes, number);
            bool byteArrayToBufferResults = AddByteArrayToOutputBuffer(intBytes);
            if(!byteArrayToBufferResults)
            {
                log.Error("Podarilo se prevest UInt64 na bajty, ale uz ne zapsat bajty do vystupniho bufferu.");
                return false;
            }
            return true;
        }

        private bool AddStringToOutputBuffer(string sourceString)
        {
            byte[] stringBytes;

            bool stringToBytesResult = StringToAsciiBytesArray(out stringBytes, sourceString);
            if (!stringToBytesResult)
            {
                log.Error("Nepodarilo se prevest string na pole bajtu, string nemuze byt pridan do vystupniho bufferu.");
                return false;
            }

            bool arrayCopyResult = AddByteArrayToOutputBuffer(stringBytes);
            if (!arrayCopyResult)
            {
                log.Error("Nepodarilo se nakopirovat pole bajtu s ASCII znaky do vystupniho buffery.");
                return false;
            }
            return true;
        }

        private bool AddByteArrayToOutputBuffer(byte[] stringBytes)
        {
            int bytesCount = stringBytes.Length;
            try
            {
                Array.Copy(stringBytes, 0, ctx.outputBuffer, ctx.oPos, bytesCount);
                ctx.oPos += bytesCount;
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

        //---------------------------------------------------------------------------------------------
        // Functions for converting datatypes to bytes
        //---------------------------------------------------------------------------------------------

        private bool FloatArrayToBigEndianByteArray(out byte[] bytes, float[] floats)
        {
            bytes = new byte[floats.Length * 4];
            for(int i=0; i < floats.Length; i++)
            {
                byte[] bytesOneFloat = BitConverter.GetBytes(floats[i]);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytesOneFloat);
                }
                Array.Copy(bytesOneFloat, 0, bytes, 4 * i, 4); 
            }
            return true;
        }

        private bool UInt16ToBigEndianBytes(out byte[] bytes, UInt16 number)
        {
            bytes = BitConverter.GetBytes(number);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return true;
        }

        private bool UInt32ToBigEndianBytes(out byte[] bytes, UInt32 number)
        {
            bytes = BitConverter.GetBytes(number);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return true;
        }

        private bool UInt64ToBigEndianBytes(out byte[] bytes, UInt64 number)
        {
            bytes = BitConverter.GetBytes(number);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return true;
        }

        private bool StringToAsciiBytesArray(out byte[] stringBytes, string sourceString)
        {
            stringBytes = null;
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

//---------------------------------------------------------------------------------------------
// Functions for extracting datatypes from socket
//---------------------------------------------------------------------------------------------

        private bool ReadSafelyUInt32FromSocket(out UInt32 number)
        {
            bool readBytesFromSocketResult;
            byte[] intBytes;
            bool transformBytesToIntResult;

            number = 0;
            readBytesFromSocketResult = ReadSafelyBytesFromSocket(out intBytes, 4);
            if (!readBytesFromSocketResult)
            {
                log.Error("Nepodarilo se nacist potrebne 4 bajty ze socketu.");
                return false;
            }
            transformBytesToIntResult = BigEndianBytesToUInt32(out number, intBytes);
            if (!transformBytesToIntResult)
            {
                log.Error("Nepodarilo se prevest 4 bajty nactene ze site na UInt32.");
                return false;
            }
            else
            {
                return true;
            }
        }

        private bool BigEndianBytesToUInt32(out UInt32 number, byte[] bytes)
        {
            number = 0;
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
            try
            {
                number = BitConverter.ToUInt32(bytes, 0);
                return true;
            }
            catch (ArgumentException ae)
            {
                log.Error("Predane pole bajtu nemuze byt prevedeno na UInt32", ae);
                return false;
            }
        }

        private bool ReadSafelyAsciiStringFromSocket(out string s, int sLength)
        {
            byte[] byteArray;
            s = null;

            bool byteArrayReadResult = ReadSafelyBytesFromSocket(out byteArray, sLength);
            if (!byteArrayReadResult)
            {
                log.Error("Nepodarilo se nacist bajty stringu ze socketu.");
                return false;
            }
            try
            {
                s = Encoding.ASCII.GetString(byteArray);
                return true;
            }
            catch (ArgumentNullException ane)
            {
                log.Error("Nepodaril se prevod pole bajtu na string, pole bajtu je null", ane);
                return false;
            }
            catch (ArgumentException ae)
            {
                log.Error("Nepodarilo se prevest pole bajtu na string, pole bajtu obsahuje neplatne Unicode znaky", ae);
                return false;
            }
        }

        private bool ReadSafelyByteFromSocket(out byte b)
        {
            b = 255;
            try
            {
                log.Debug("Cekam na nacteni bajtu ze socketu.");
                b = ctx.binReader.ReadByte();
                log.Debug("Prijaty novy bajt od klienta: " + b);
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

        private bool ReadSafelyBytesFromSocket(out byte[] bytes, int numberOfBytes)
        {
            bytes = null;
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

//---------------------------------------------------------------------------------------------

        /// <summary>
        /// Zapíše výstupní buffer z klientského kontextu do NetworkStreamu a okamžitě ho odešle.
        /// </summary>
        /// <returns>Pokud dojde k úspěšnému odeslání true, jinak false.</returns>
        private bool SendOutputBuffer()
        {
            log.Debug("Bude odeslan vystupni buffer.");
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
            catch (ObjectDisposedException)
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
            if(ctx.grabber.HasCameraAssigned())
            {
                ctx.grabber.Disconnect(WICGrabber.TIMEOUT_FOR_CAMERA_REQUESTS);
                ctx.grabber.UnassignCameraDevice();
            }
            ctx.grabber = null;
            // Smažu kontext pro continous grabbing
            //ctx.continousGrabProvider = null;
            //ctx.continuoslyGrabbingCameraId = -1;
            //ctx.maxImageId = 0;
            //ctx.maxAcknowledgedImageId = 0;
            // Výsledek
            return allCamerasClosingResult;
        }

        
        /* ==============================================================================================
         * SPECIÁLNÍ METODA VOLANÉ Z GRABBOVACÍHO VLÁKNA.
         * ==============================================================================================
         */
        public bool SendFrame(UInt32 frameId, UInt64 timestamp, UInt16 height, UInt16 width, float[] temperatures)
        {
            // Vynuluji pozici v bufferu.
            ctx.oPos = 0;
            // Zapíšu FRAME code, délku snímku a samotný snímek.
            if (!WriteFrameToOutputBuffer(frameId, timestamp, height, width, temperatures))
            {
                log.Error("Nepodarilo se zapsat snimek do vystupniho bufferu.");
                return false;
            }
            if (!SendOutputBuffer())
            {
                log.Error("Nepodarilo se odeslat buffer se snimkem.");
                return false;
            }
            log.Debug("Nepodarilo se odeslat buffer se snimkem.");

            // Snímek odeslán.
            return true;
        }
        
        /*==============================================================================================
         *==============================================================================================
         */
         
    }
}
