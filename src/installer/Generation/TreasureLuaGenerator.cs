using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

internal static class TreasureLuaGenerator
{
    private const string XmlNamespace =
        "http://leigient.549n.com/schema/SectionActorInfo";

    public static int Generate(
        string xmlPath,
        string outputPath)
    {
        XmlDocument document = new XmlDocument
        {
            XmlResolver = null
        };
        document.Load(xmlPath);
        XmlNodeList nodes = document.SelectNodes(
            "//*[local-name()='SectionActorData']");
        if (nodes == null || nodes.Count < 1000)
        {
            throw new InvalidDataException(
                "The extracted treasure data is incomplete.");
        }

        List<string> lines = new List<string>
        {
            "return {"
        };
        foreach (XmlNode node in nodes)
        {
            XmlElement element = node as XmlElement;
            if (element == null)
            {
                continue;
            }

            string cid =
                element.GetAttribute("CID", XmlNamespace);
            string section = element.GetAttribute(
                "SectionUID",
                XmlNamespace);
            string x =
                element.GetAttribute("PosX", XmlNamespace);
            string y =
                element.GetAttribute("PosY", XmlNamespace);
            ValidateInteger(cid, "CID");
            ValidateInteger(section, "SectionUID");
            ValidateNumber(x, "PosX");
            ValidateNumber(y, "PosY");
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "    {{ save_id = {0}, section = \"{1}\", " +
                "x = {2}, y = {3} }},",
                cid,
                section,
                x,
                y));
        }
        lines.Add("}");
        File.WriteAllLines(
            outputPath,
            lines,
            new UTF8Encoding(false));
        return nodes.Count;
    }

    private static void ValidateInteger(
        string value,
        string fieldName)
    {
        ulong ignored;
        if (!ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ignored))
        {
            throw new InvalidDataException(
                fieldName + " contains an invalid value.");
        }
    }

    private static void ValidateNumber(
        string value,
        string fieldName)
    {
        double parsed;
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            throw new InvalidDataException(
                fieldName + " contains an invalid value.");
        }
    }
}
