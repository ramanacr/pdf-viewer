using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;

namespace PdfEngine.Vector.PdfA.Xmp;

/// <summary>
/// Writes XMP properties as a packet PDF/A accepts: UTF-8, an xpacket header without bytes or
/// encoding attributes, one rdf:Description per schema, structures as rdf:parseType="Resource",
/// arrays as rdf:Bag/Seq/Alt and language alternatives with xml:lang.
/// </summary>
public static class XmpWriter
{
    public static byte[] Write(IEnumerable<XmpProperty> properties)
    {
        var props = properties.ToList();
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal); // namespace → prefix
        var used = new HashSet<string>(StringComparer.Ordinal) { "x", "rdf", "xml" };
        string PrefixFor(string ns, string wanted)
        {
            if (prefixes.TryGetValue(ns, out var p)) return p;
            if (string.IsNullOrEmpty(wanted) || !used.Add(wanted))
            {
                int i = 1;
                while (!used.Add(wanted = "ns" + i)) i++;
            }
            prefixes[ns] = wanted;
            return wanted;
        }
        void Collect(XmpValue v)
        {
            foreach (var f in v.Fields) { PrefixFor(f.Namespace, f.Prefix); Collect(f.Value); }
            foreach (var i in v.Items) Collect(i);
        }
        foreach (var p in props) { PrefixFor(p.Namespace, p.Prefix); Collect(p.Value); }

        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n <rdf:RDF xmlns:rdf=\"").Append(XmpPacket.RdfNs).Append('"');
        foreach (var (ns, prefix) in prefixes) sb.Append("\n   xmlns:").Append(prefix).Append("=\"").Append(Escape(ns)).Append('"');
        sb.Append(">\n");
        foreach (var schema in props.GroupBy(p => p.Namespace))
        {
            sb.Append("  <rdf:Description rdf:about=\"\">\n");
            foreach (var p in schema) WriteProperty(sb, prefixes[p.Namespace] + ":" + p.Name, p.Value, prefixes, 3);
            sb.Append("  </rdf:Description>\n");
        }
        sb.Append(" </rdf:RDF>\n</x:xmpmeta>\n");
        // Padding lets other tools update the packet in place.
        for (int i = 0; i < 20; i++) sb.Append(new string(' ', 99)).Append('\n');
        sb.Append("<?xpacket end=\"w\"?>");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static void WriteProperty(StringBuilder sb, string qname, XmpValue v, Dictionary<string, string> prefixes, int indent)
    {
        string pad = new(' ', indent);
        string lang = v.Lang is { } l ? $" xml:lang=\"{Escape(l)}\"" : string.Empty;
        switch (v.Kind)
        {
            case XmpKind.Simple when v.IsUri:
                sb.Append(pad).Append('<').Append(qname).Append(lang).Append(" rdf:resource=\"").Append(Escape(v.Text)).Append("\"/>\n");
                break;
            case XmpKind.Simple:
                sb.Append(pad).Append('<').Append(qname).Append(lang).Append('>').Append(Escape(v.Text)).Append("</").Append(qname).Append(">\n");
                break;
            case XmpKind.Struct:
                sb.Append(pad).Append('<').Append(qname).Append(lang).Append(" rdf:parseType=\"Resource\">\n");
                foreach (var f in v.Fields) WriteProperty(sb, prefixes[f.Namespace] + ":" + f.Name, f.Value, prefixes, indent + 1);
                sb.Append(pad).Append("</").Append(qname).Append(">\n");
                break;
            default:
                string container = v.Kind switch { XmpKind.Bag => "rdf:Bag", XmpKind.Seq => "rdf:Seq", _ => "rdf:Alt" };
                sb.Append(pad).Append('<').Append(qname).Append(">\n").Append(pad).Append(' ').Append('<').Append(container).Append(">\n");
                foreach (var item in v.Items) WriteProperty(sb, "rdf:li", item, prefixes, indent + 2);
                sb.Append(pad).Append(' ').Append("</").Append(container).Append(">\n").Append(pad).Append("</").Append(qname).Append(">\n");
                break;
        }
    }

    private static string Escape(string s) => SecurityElement.Escape(s) ?? string.Empty;

    // ------------------------------------------------------------------ building values

    public static XmpValue Simple(string text) => new() { Kind = XmpKind.Simple, Text = text };

    public static XmpValue LangAlt(string text)
    {
        var item = new XmpValue { Kind = XmpKind.Simple, Text = text };
        item.Qualifiers.Add(new XmpProperty(XmpValue.XmlNs, "xml", "lang", Simple("x-default")));
        var alt = new XmpValue { Kind = XmpKind.Alt };
        alt.Items.Add(item);
        return alt;
    }

    public static XmpValue Array(XmpKind kind, IEnumerable<string> items)
    {
        var a = new XmpValue { Kind = kind };
        foreach (var i in items) a.Items.Add(Simple(i));
        return a;
    }
}
