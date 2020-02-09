using System.Configuration;
using System.Xml;
using WIC_SDK;

namespace WRPServer.Cameras

{
    /// <summary>
    /// Umožňuje vylistování připojených zařízení.
    /// </summary>
    public static class CameraManager
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly CameraCenter cameraCenter = new CameraCenter(ConfigurationManager.AppSettings["folderLicencePath"]);

        public static Camera GetCameraBySerialNumber(string serialNumber)
        {
            foreach (Camera cam in cameraCenter.Cameras)
            {
                if(cam.SerialNumber == serialNumber)
                {
                    return cam;
                }
            }
            return null;
        }

        public static string GetCameraListXML()
        {
            XmlDocument doc = new XmlDocument();
            XmlElement elCameras = (XmlElement)doc.AppendChild(doc.CreateElement("Cameras"));

            foreach (Camera cam in cameraCenter.Cameras)
            {
                XmlElement elCamera = (XmlElement)doc.CreateElement("Camera");
                elCamera.SetAttribute("Width", cam.Width.ToString());
                elCamera.SetAttribute("Height", cam.Height.ToString());
                elCamera.SetAttribute("CameraMaxFPS", cam.CameraMaxFPS.ToString());
                elCamera.SetAttribute("Version", cam.Version);
                elCamera.SetAttribute("ModelName", cam.ModelName);
                elCamera.SetAttribute("ManufacturerInfo", cam.ManufacturerInfo);
                elCamera.SetAttribute("SerialNumber", cam.SerialNumber);
                elCamera.SetAttribute("VendorName", cam.VendorName);
                elCameras.AppendChild(elCamera);
            }

            return doc.OuterXml;
        }
    }
}
