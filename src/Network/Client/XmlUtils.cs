using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace GenicamConnector.Network.Controller.Sockets.Client
{
    public class XmlUtils
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal static bool translateXmlToChangesList(List<KeyValuePair<string, string>> changes, out XmlDocument xmlOutputDocument, string xml)
        {
            // Proměnné.
            // Načtené vstupní XML.
            XmlDocument xmlInputDocument;

            // Dummy hodntoty pro výstupní parametr v případě neúspěchu.
            xmlOutputDocument = null;
            // Načtu vstupní XML.
            // Metoda LoadXml ze string vzhazuje asi 8 různých výjimek.
            // Zde odchytím všechny najednou, protože žádnou z nich nejsem z toho místa schopen vyřešit.
            // Viz https://msdn.microsoft.com/en-us/library/875kz807(v=vs.110).aspx
            xmlInputDocument = new XmlDocument();
            try
            {
                xmlInputDocument.LoadXml(xml);
            }
            catch (Exception e)
            {
                log.Error("Nepodarilo se nacist uzivatelem zadane XML", e);
                return false;
            }

            // Vytvořím výstupní XML, nastavím deklaraci ASCII kódování.
            // Kořenový element výstupního XML.
            XmlElement responseElement;
            try
            {
                // Založím výstupní XML.
                xmlOutputDocument = new XmlDocument();
                // XML deklarace kódování před root element.
                XmlDeclaration xmlDeclaration = xmlOutputDocument.CreateXmlDeclaration("1.0", "US-ASCII", null);
                XmlElement outputXmlRootElement = xmlOutputDocument.DocumentElement;
                xmlOutputDocument.InsertBefore(xmlDeclaration, outputXmlRootElement);
                // Vytvořím kořenový element výstupního XML.
                responseElement = xmlOutputDocument.CreateElement(string.Empty, "Root", string.Empty);
                xmlOutputDocument.AppendChild(responseElement);
            }
            catch (Exception e)
            {
                log.Error("Nepodarilo se vytvorit vystupni XML", e);
                return false;
            }

            // Projdu vstupní XML a relevantní uzly převedu na dvojice key value pro nastavení.
            // Všechny Setting uzly přes celý dokument.
            try
            {

                XmlNodeList settingNodesList = xmlInputDocument.SelectNodes("//Setting");
                foreach (XmlNode n in settingNodesList)
                {
                    // Načtu první uzel Name z aktuálního uzlu (proto tecka)
                    XmlNode nameNode = n.SelectSingleNode(".//Name");
                    // Načtu první uzel Value z aktuálního uzlu
                    XmlNode valueNode = n.SelectSingleNode(".//Value");
                    // Pokud XML settings element neobsahuje pozadovane elementy
                    if (nameNode == null || valueNode == null)
                    {
                        XmlElement settingElement = xmlOutputDocument.CreateElement(string.Empty, "Setting", string.Empty);

                        string name = nameNode == null ? "null" : nameNode.InnerText;
                        string value = valueNode == null ? "null" : valueNode.InnerText;
                        XmlElement nameElement = xmlOutputDocument.CreateElement(string.Empty, "Name", string.Empty);
                        XmlText nameText = xmlOutputDocument.CreateTextNode(name);
                        nameElement.AppendChild(nameText);

                        settingElement.AppendChild(nameElement);

                        XmlElement valueElement = xmlOutputDocument.CreateElement(string.Empty, "Value", string.Empty);
                        XmlText valueText = xmlOutputDocument.CreateTextNode(value);
                        valueElement.AppendChild(valueText);
                        settingElement.AppendChild(valueElement);

                        XmlElement changedElement = xmlOutputDocument.CreateElement(string.Empty, "Changed", string.Empty);
                        XmlText changedText = xmlOutputDocument.CreateTextNode(false.ToString());
                        changedElement.AppendChild(changedText);
                        settingElement.AppendChild(changedElement);

                        XmlElement errorMessageElement = xmlOutputDocument.CreateElement(string.Empty, "ErrorMessage", string.Empty);
                        XmlText errorMessageText = xmlOutputDocument.CreateTextNode("Name or Value element does not exist within the enclosing Setting element.");
                        errorMessageElement.AppendChild(errorMessageText);
                        settingElement.AppendChild(errorMessageElement);

                        responseElement.AppendChild(settingElement);
                    }
                    else
                    {
                        string name = nameNode.InnerText;
                        string value = valueNode.InnerText;
                        log.Debug(name + " -> " + value);
                        changes.Add(new KeyValuePair<string, string>(name, value));
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                log.Error("Nastal problem pri transformaci vstupniho konfiguracniho XML na seznam prikazu s nastavenim pro pylon.", e);
                return false;
            }

        }
    }
}
