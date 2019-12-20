using System;
using System.Collections.Generic;
using System.Configuration;
using WIC_SDK;

/// <summary>
/// Třída je převzatá z ukázkového kódu pro .NET z Basler Pylon examples
/// a následně modifikována.
/// </summary>
namespace WRPServer.Cameras

{
    /// <summary>
    /// Umožňuje vylistování připojených zařízení.
    /// </summary>
    public static class CameraManager
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly CameraCenter cameraCenter = new CameraCenter(ConfigurationManager.AppSettings["folderLicencePath"]);
        /// <summary>
        /// Drží data o zařízení.
        /// </summary>
        public class Device
        {
            // Serial ID zařízení
            public string SerialID;
            // Jméno výrobce (značka kamery)
            public string VendorName;
            // Model kamery
            public string ModelName;
            // Maximální šířka rozlišení
            public int Width;
            // Maximální výška rozlišení
            public int Height;
        }

        /// <summary>
        /// Vylistuje všechny připojené zařízení.
        /// </summary>
        /// <returns>
        /// List, ve kterém je pro každé zařízení instance Device.
        /// Device obsahuje informace o SerialID a aktuálním ID (jde o pořadí ve vylistování).
        /// </returns>
        public static List<Device> EnumerateDevices()
        {
            // Nový List
            List<Device> list = new List<Device>();

            uint i = 0;
            foreach (Camera cam in cameraCenter.Cameras)
            {
                Device device = new Device();
                device.SerialID = cam.SerialNumber;
                device.VendorName = cam.VendorName;
                device.ModelName= cam.ModelName;
                device.Width = cam.Width;
                device.Height = cam.Height;
                list.Add(device);
            }
            return list;
        }
    }
}
