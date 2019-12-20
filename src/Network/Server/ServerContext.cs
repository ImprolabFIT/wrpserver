using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using WIC_SDK;
using WRPServer.Cameras;
using WRPServer.Network.Enum;
using static WRPServer.Cameras.CameraManager;

namespace WRPServer.Network.Server
{
    /// <summary>
    /// Udržuje hlavní kontext serverové aplikace, tedy údaje o ID klientů v systémů, ID zamčených kamer, 
    /// zprostředkovává pro klienty přístup do Pylon knihovny (nastavení kamer, ne snímání dat).
    /// </summary>
    public class ServerContext
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // Zámek pro přístup k ID kamer CameraIds a BorrowedCameras
        private readonly object SyncLock = new object();
        // Generátor ID klientů.
        private Random Random;

        // HashMap BorrowedCameras se použije pouze v nouzové situaci, kdy je potřeba okamžitě vypnout celý
        // server a není prostor nutit klientská vlákna, aby si odemknuly kamery.
        private Dictionary<int, Camera> BorrowedCameras;
        // ID půjčených kamer, za běžného stavu asociované ImageProvider objekty poskytují klienti.
        private HashSet<string> CameraIds;
        // ID připojených klientů.
        private HashSet<int> ClientIds;

        /// <summary>
        /// Inicializuje kontext serveru.
        /// Je provedena inicializace Pylon knihovny a následně datové struktury pro generování ID klientů a ukládání ID zamčených kamer.
        /// </summary>
        public ServerContext()
        {
            // INICIALIZACE DATOVYCH STRUKTUR
            this.Random = new Random();
            this.BorrowedCameras = new Dictionary<int, Camera>();
            this.ClientIds = new HashSet<int>();
            this.CameraIds = new HashSet<string>();
        }

        /// <summary>
        /// Vygeneruje unikátní ID pro nového klienta.
        /// </summary>
        /// <returns>Unikátní ID pro nového klienta.</returns>
        public int CreateNewClientId()
        {
            while (true)
            {
                int r = this.Random.Next();
                if (!this.ClientIds.Contains(r))
                {
                    this.ClientIds.Add(r);
                    return r;
                }
            }
        }

        /// <summary>
        /// Ověří, zda v server detekuje aktivního klienta se zadaným ID.
        /// </summary>
        /// <param name="id">ID klienta.</param>
        /// <returns>True, pokud je uživatel s daným ID známý a veden jako aktivní, jinak false.</returns>
        public bool ExistClientId(int id)
        {
            return this.ClientIds.Contains(id);
        }

        /// <summary>
        /// Vypíše připojené kamery do XML (ASCII kódování), které je vráceno ve stringu.
        /// V případě chyby při tvoření výpisu je vygenerována chybová hláška.
        /// </summary>
        /// <returns>String obsahující XML s výpisem kamer. V případě chyby je místo XML vrácena chybová hláška.</returns>
        public string ListDevicesAsXml()
        {
        
            List<Device> devices;
            try
            {
                devices = CameraManager.EnumerateDevices();
            }
            catch (Exception e)
            {
                devices = new List<Device>();
                log.Error("Nastal problem pri vylistovani kamer", e);
            }
            return CreateDevicesXml(devices);
        }

        /// <summary>
        /// Okamžitě uvolní veškeré prostředky alokované Pylon knihovnou (uzavře kamery a terminuje knihovnu).
        /// Metoda nebere ohled na běžící vlákna používající kamery.
        /// Metoda je určená pouze k nouzovému vrácení prostředků po volání shutdown hook nebo po detekci neodchycené výjimky.
        /// </summary>
        //public void ReleasePylonResources()
        //{
        //    log.Info("Volano ReleasePylonResources!");
        //    lock (SyncLock)
        //    {
        //        // Dealokace všech kamer.
        //        log.Info("Provádím dealokaci všech Pylon kamer před ukončením Pylonu.");
        //        foreach (KeyValuePair<int, ImageProvider> entry in BorrowedCameras)
        //        {
        //            log.Debug("Nouzove odemykam kameru " + entry.Key);
        //            try
        //            {
        //                entry.Value.Close();
        //                log.Debug("Kamera " + entry.Key + " odemcena.");
        //            }
        //            catch (Exception e)
        //            {
        //                log.Warn("Pri uzavirani kamery " + entry.Key + " nastala chyba: ", e);
        //            }
        //        }
        //    }
        //    // Deinicializace Pylonu
        //    log.Info("Terminuji Pylon knihovnu.");
        //    PylonC.NET.Pylon.Terminate();
        //}

        /// <summary>
        /// Zamkne pro klienta kameru se zadaným ID, pokud je daná kamera dostupná a nezamčená jiným uživatelem.
        /// </summary>
        /// <param name="camId">ID kamery, kterou chce klient zamknout.</param>
        /// <param name="response">Výstupní parametr, kterým metoda oznamuje, jak operace zamčení proběhla (OK při úspěšném zamčení, ERROR při neúspěchu).</param>
        /// <param name="resultDescription">Výstupní parametr, kterým se přenáší případná chybová zpráva, když nedojde k úspěšnému uzamčení.</param>
        /// <returns></returns>
        //public Camera LockCamera(string camId, out ResponseCode response, out string resultDescription)
        //{
        //    log.Debug("Volana metoda LockCamera pro camId " + camId);
        //    lock (SyncLock)
        //    {
        //        try
        //        {
        //            // Je kamera zamcena?
        //            if (CameraIds.Contains(camId))
        //            {
        //                throw new Exception("Camera with the given ID is already locked.");
        //            }
        //
        //            List<Device> devices = CameraManager.EnumerateDevices();
        //            foreach (Device device in devices)
        //            {
        //                Console.WriteLine("serial id: " + device.SerialID + ", camId: " + camId);
        //                if (device.SerialID == camId)
        //                {
        //                    ImageProvider imageProvider = new ImageProvider();
        //                    imageProvider.Open(device.Index);
        //                    if (!imageProvider.IsOpen)
        //                    {
        //                        throw new Exception("Camera was not opened even though no explicit error has been thrown.");
        //                    }
        //                    else
        //                    {
        //                        // Zapisu, ze kamera je zamknuta
        //                        CameraIds.Add(camId);
        //                        BorrowedCameras.Add(camId, imageProvider);
        //                        response = ResponseCode.OK;
        //                        resultDescription = "Camera handle locked";
        //                        return imageProvider;
        //                    }
        //                }
        //            }
        //
        //            log.Error("Nezdarilo se ziskat handle na kameru s ID " + camId);
        //            response = ResponseCode.ERROR;
        //            resultDescription = "Unable to fetch camera handle - camera with given SerialID does not exist (sid=" + camId + ")";
        //            return null;
        //
        //        }
        //        catch (Exception e)
        //        {
        //            log.Error("Nezdarilo se ziskat handle na kameru s ID " + camId, e);
        //            response = ResponseCode.ERROR;
        //            resultDescription = "Unable to fetch camera handle: " + e.Message;
        //            return null;
        //        }
        //    }
        //}

        /// <summary>
        /// Odemkne kameru, tzn. umožní dalším klientům její zamknutí a použití.
        /// Metoda odchytává výjimky padající z Pylonu, volající se tedy o ně nemusí starat.
        /// </summary>
        /// <param name="imProv">ImageProvider asociovaný s odemykanou kamerou.</param>
        /// <param name="camId">Tovární ID odemykané kamery.</param>
        //public bool UnlockCamera(ImageProvider imProv, string camId)
        //{
        //    log.Debug("Odemykam kameru s globalnim ID " + camId);
        //    // Potřebuji výhradní přístup k seznamu kamer.
        //    lock (SyncLock)
        //    {
        //        try
        //        {
        //            // Odstraním ID z množiny aktuálně zamčených kamer.
        //            if (!CameraIds.Contains(camId))
        //            {
        //                log.Error("Mnozina ID vypujcenych kamer nebobsahuje vracenou kameru s globalnim ID " + camId + ". Uzavreni kamery nebude provedeno!");
        //                return false;
        //            }
        //            else
        //            {
        //                log.Debug("Globalni ID kamery " + camId + " odstraneno z mnoziny zamcenych kamer.");
        //                CameraIds.Remove(camId);
        //                BorrowedCameras.Remove(camId);
        //            }
        //
        //            // Pokud je kamera stále otevřená, zavřu Pylon proxy objekt.
        //            if (imProv.IsOpen)
        //            {
        //                log.Debug("ImageProvider kamery otevren, provadim zavreni proxy objektu pro globalni ID " + camId);
        //                imProv.Close();
        //            }
        //
        //            // Pokud nenastala výjimka, kamera je odblokovaná pro ostatní.
        //            log.Debug("Kamera s globalnim ID " + camId + " uspesne odemcena.");
        //            return true;
        //        }
        //        catch (Exception e)
        //        {
        //            log.Error("Nezdarilo se uzavirani kamery " + camId, e);
        //            return false;
        //        }
        //    }
        //}

        private string CreateDevicesXml(List<Device> devices)
        {
            // Zalozit XML
            XmlDocument doc = new XmlDocument();
            // XML deklarace
            XmlDeclaration xmlDeclaration = doc.CreateXmlDeclaration("1.0", "US-ASCII", null);
            XmlElement root = doc.DocumentElement;
            doc.InsertBefore(xmlDeclaration, root);
            // Response
            XmlElement responseElement = doc.CreateElement(string.Empty, "Response", string.Empty);
            doc.AppendChild(responseElement);
            // Cameras
            XmlElement camerasElement = doc.CreateElement(string.Empty, "Cameras", string.Empty);
            responseElement.AppendChild(camerasElement);
        
            foreach (Device d in devices)
            {
                // Camera XML
                XmlElement cameraElement = doc.CreateElement(string.Empty, "Camera", string.Empty);
                // Vlastnosti kamery
                // Device ID
                XmlElement deviceIdElement = doc.CreateElement(string.Empty, "SerialNumber", string.Empty);
                XmlText deviceIdtext = doc.CreateTextNode(d.SerialID.ToString());
                deviceIdElement.AppendChild(deviceIdtext);
                cameraElement.AppendChild(deviceIdElement);
                // VendorName
                XmlElement vendorNameElement = doc.CreateElement(string.Empty, "VendorName", string.Empty);
                XmlText vendorNameText = doc.CreateTextNode(d.VendorName);
                vendorNameElement.AppendChild(vendorNameText);
                cameraElement.AppendChild(vendorNameElement);
                // ModelName
                XmlElement modelNameElement = doc.CreateElement(string.Empty, "ModelName", string.Empty);
                XmlText modelNameText = doc.CreateTextNode(d.ModelName);
                modelNameElement.AppendChild(modelNameText);
                cameraElement.AppendChild(modelNameElement);
                // Pridani kamery do XML
                camerasElement.AppendChild(cameraElement);
            }
            using (var stringWriter = new StringWriter())
            using (var xmlTextWriter = XmlWriter.Create(stringWriter))
            {
                doc.WriteTo(xmlTextWriter);
                xmlTextWriter.Flush();
                return (stringWriter.GetStringBuilder().ToString());
            }
        }

        //private string GetParamAsString(PylonC.NET.PYLON_DEVICE_HANDLE hDev, string featureName)
        //{
        //    // Strom nastaveni
        //    PylonC.NET.NODEMAP_HANDLE hNodeMap;
        //    // Uzel ve stromu nastaveni
        //    PylonC.NET.NODE_HANDLE hNode;
        //
        //    // Strom nastaveni pro zadanou kameru
        //    hNodeMap = PylonC.NET.Pylon.DeviceGetNodeMap(hDev);
        //    // Uzel pro pozadovane nastaveni kamery
        //    hNode = PylonC.NET.GenApi.NodeMapGetNode(hNodeMap, featureName);
        //
        //    // Pokud pozadovany uzel neexistuje
        //    if (!hNode.IsValid)
        //    {
        //        return "";
        //    }
        //    // Pokud pozadovany uzel neni citelny
        //    if (!PylonC.NET.GenApi.NodeIsReadable(hNode))
        //    {
        //
        //        return "";
        //    }
        //    string valueString = PylonC.NET.GenApi.NodeToString(hNode);
        //    return valueString;
        //}
    }
}
