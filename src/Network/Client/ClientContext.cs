using System.Collections.Generic;
using System.Net.Sockets;
using System.IO;
using System.Configuration;
using System;
using WRPServer.Network.Enum;
using WIC_SDK;

namespace WRPServer.Network.Client
{
    /// <summary>
    /// Wrapper pro datové struktury definující kontext komunikace s klientem.
    /// Ukládá ID klienta, seznam zamčených kamer, výstupní buffer, stav obsluhy atd..
    /// </summary>
    public class ClientContext
    {
        // Socket, přes který probíhá komunikace s klientem. Streamy, přes které je socket čten.
        // BinaryReader má kontrolu nad socketem (tzn. zavřením BinaryReaderu se zavírá i Socket).
        public Socket socket = null;
        public NetworkStream socketStream;
        public BinaryReader binReader;

        // Aktuální stav klienta
        public ClientState clientState;

        // Flag, který značí, zda si klient přeje pokračovat v komunikaci.
        // Flag je nastaven na true, dokud není přijata CLOSE action.
        // Použit je v centrální komunikační smyčce HandleClient, kde ovládá, zda má obsluha pokračovat nebo skončit.
        public bool continueCommunication;

        // Unikátní ID, kterým se klient ohlašuje. 
        // Toto ID klient dostává při začátku komunikace od ServerContext. Unikátní napříč běžící instancí aplikace.
        public int clientId;

        // Buffer pro odchozí zprávy. 
        // Proměnná oPos udržuje aktuální pozici ve Bufferu (vždy ukazuje na první nezapsanou pozici).
        public int oPos;
        public byte[] outputBuffer = new byte[Convert.ToUInt32(ConfigurationManager.AppSettings["clientOutputBufferSize"])];
        public bool bufferReadyToSend = false;

        // Kamery, které má klient vypůjčené. 
        // Klient má k uzamčené kameře výhradní přístup.
        // Slovník mapuje unikátní ID kamery na Proxy objekt kamery.
        public Dictionary<int, Camera> cameras = new Dictionary<int, Camera>();

        // Kontext pro continous grabbing.
        // ID snímací kamery.
        public int continuoslyGrabbingCameraId;
        // Proxy objekt pro snímací vlákno.
        public ContinuousGrabProvider continousGrabProvider;
        // Maximální ID nasnímaného obrázku.
        public int maxImageId;
        // Maximální ID nasnímaného obrázku, které klient potvrrdil.
        public int maxAcknowledgedImageId;
    }
}
