using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

internal static class TreasureLuaGenerator
{
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

            string cid = GetRequiredAttribute(element, "CID");
            string section = GetRequiredAttribute(
                element,
                "SectionUID");
            string x = GetRequiredAttribute(element, "PosX");
            string y = GetRequiredAttribute(element, "PosY");
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

    private static string GetRequiredAttribute(
        XmlElement element,
        string localName)
    {
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (string.Equals(
                attribute.LocalName,
                localName,
                StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        throw new InvalidDataException(
            "The extracted treasure data is missing attribute '" +
            localName +
            "'.");
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
