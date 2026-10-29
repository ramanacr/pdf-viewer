using System.Collections.Generic;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// The Matterhorn Protocol 1.1 failure conditions this checker can evaluate by machine, each with
/// the condition and how to put it right.
/// </summary>
internal static class PdfUaCheckpoints
{
    private static readonly Dictionary<string, (string Condition, string Fix)> All = new()
    {
        ["01-003"] = ("Content marked as an artifact is inside tagged content.", "Close the tagged sequence before the artifact, or tag the content as real content instead of as an artifact."),
        ["01-004"] = ("Tagged content is inside content marked as an artifact.", "Move the tagged content out of the artifact, or remove its tag if it is decoration."),
        ["01-005"] = ("Content is neither marked as an artifact nor tagged as real content.", "Tag the content in the structure tree (Acrobat: Autotag or the Reading Order tool), or mark it as an artifact if it is decoration, a running header or a page number."),
        ["01-006"] = ("A structure type is not appropriate for its content.", "Use a structure type of the PDF's own version; PDF 2.0 leaves out several PDF 1.7 types (use Aside, FENote or Sect instead)."),
        ["01-007"] = ("The Suspects entry in MarkInfo is true.", "Review the tags, then set /Suspects to false in the catalog's /MarkInfo."),
        ["02-001"] = ("A non-standard structure type is not mapped to a standard one.", "Add a RoleMap entry that maps the type, directly or through other types, to a standard type such as P, Span or Div."),
        ["02-003"] = ("The role map has a circular mapping.", "Change the RoleMap so that every chain ends at a standard type."),
        ["02-004"] = ("A standard structure type is remapped.", "Remove the RoleMap entry for the standard type; use a new type name if a different meaning is needed."),
        ["06-001"] = ("The document has no XMP metadata stream.", "Add XMP metadata to the catalog (Acrobat: File > Properties, or save as PDF/UA)."),
        ["06-002"] = ("The metadata does not identify the document as PDF/UA-1.", "Add pdfuaid:part with the value 1 to the XMP metadata, with the pdfuaid prefix."),
        ["06-003"] = ("The metadata has no dc:title.", "Give the document a title (File > Properties > Description > Title), which writes dc:title."),
        ["07-001"] = ("ViewerPreferences has no DisplayDocTitle entry.", "Set DisplayDocTitle to true (File > Properties > Initial View > Show: Document Title)."),
        ["07-002"] = ("DisplayDocTitle is false.", "Set DisplayDocTitle to true (File > Properties > Initial View > Show: Document Title)."),
        ["09-004"] = ("A table element is used in a way that does not conform to the table syntax.", "Nest tables as Table > (THead, TBody, TFoot) > TR > TH or TD, with at most one Caption first or last, and make every row cover the same number of columns."),
        ["09-005"] = ("A list element is used in a way that does not conform to the list syntax.", "Nest lists as L > LI > (Lbl, LBody), with an optional Caption as the first child of L; put nested lists inside LBody."),
        ["09-006"] = ("A table of contents element is used in a way that does not conform to its syntax.", "Nest TOCI elements directly in TOC; TOC may also hold nested TOC elements and one Caption as its first child."),
        ["10-001"] = ("A character cannot be mapped to Unicode.", "Use a font with a ToUnicode CMap or a standard encoding, or re-create the PDF from its source with embedded Unicode fonts."),
        ["11-001"] = ("The natural language of page content cannot be determined, or a language tag is not valid.", "Set the document language (File > Properties > Advanced > Language) or a Lang on the structure elements, using a valid tag such as en-US."),
        ["11-002"] = ("The natural language of alternate text, actual text or an expansion cannot be determined.", "Set the document language, or a Lang on the element that carries the text."),
        ["11-003"] = ("The natural language of the bookmarks cannot be determined.", "Set the document language (File > Properties > Advanced > Language)."),
        ["11-004"] = ("The natural language of an annotation's Contents cannot be determined.", "Set the document language, or a Lang on the annotation's structure element."),
        ["11-005"] = ("The natural language of a form field's tooltip (TU) cannot be determined.", "Set the document language, or a Lang on the field's Form structure element."),
        ["11-006"] = ("The natural language of the document metadata cannot be determined.", "Set the document language (File > Properties > Advanced > Language)."),
        ["13-004"] = ("A Figure has no alternate text.", "Give the figure alternate text that describes it (Acrobat: right-click the figure tag > Properties > Alternate Text), or mark it as an artifact if it is decoration."),
        ["14-002"] = ("The document uses numbered headings, but the first one is not H1.", "Make the first heading H1."),
        ["14-003"] = ("Numbered heading levels are skipped.", "Change the heading tags so that each level is at most one deeper than the one before (H1, then H2, then H3)."),
        ["14-006"] = ("A structure element contains more than one H.", "Put each unnumbered H in its own Sect, or use numbered headings H1 to H6."),
        ["14-007"] = ("The document uses both H and numbered headings (H1 to H6).", "Use either H inside nested sections or H1 to H6, not both."),
        ["15-003"] = ("A table header cell has no Scope and the table has no Headers and IDs.", "Set Scope (Row, Column or Both) on every TH, or give cells IDs and Headers (Acrobat: Table Editor > Table Cell Properties)."),
        ["15-005"] = ("A data cell's header cannot be determined.", "Set Scope on the header cells, or give the data cell a Headers attribute naming its header cells' IDs."),
        ["17-002"] = ("A Formula has no alternate text.", "Give the formula alternate text that reads it out (Acrobat: right-click the formula tag > Properties > Alternate Text)."),
        ["19-003"] = ("A Note has no ID.", "Give every Note structure element a unique ID."),
        ["19-004"] = ("Two Notes have the same ID.", "Give every Note structure element a unique ID."),
        ["20-001"] = ("An optional content configuration has no name.", "Give every configuration in /Configs a non-empty /Name."),
        ["20-002"] = ("The default optional content configuration has no name.", "Give the default configuration (/D) a non-empty /Name."),
        ["20-003"] = ("An optional content configuration has an AS entry.", "Remove /AS from the optional content configurations."),
        ["21-001"] = ("An embedded file's specification lacks F or UF.", "Re-attach the file so that its file specification has both /F and /UF (Acrobat writes both)."),
        ["25-001"] = ("The document has a dynamic XFA form.", "Convert the form to an AcroForm (static) form, then tag it."),
        ["26-001"] = ("The document is encrypted but has no permissions entry (P).", "Re-save the security settings so that the encryption dictionary has /P."),
        ["26-002"] = ("The encryption forbids assistive technology from reading the text.", "Allow \"Enable text access for screen reader devices\" in the security settings (permission bit 10)."),
        ["28-002"] = ("An annotation is not nested in an Annot structure element.", "Tag the annotation in an Annot element (Acrobat: Tags panel > Find > Unmarked Annotations, or Autotag)."),
        ["28-004"] = ("An annotation has no alternate description.", "Give the annotation a description in its Contents, or Alt on its Annot element."),
        ["28-005"] = ("A form field has no description.", "Give the field a tooltip (field Properties > General > Tooltip), which writes /TU."),
        ["28-006"] = ("An annotation's subtype is not defined in ISO 32000.", "Replace the annotation with one of a standard subtype, or mark its appearance as an artifact."),
        ["28-007"] = ("The document has a TrapNet annotation.", "Remove the TrapNet annotation."),
        ["28-008"] = ("A page with annotations has no Tabs entry.", "Set the page's tab order to Use Document Structure (Page Properties > Tab Order), which writes /Tabs /S."),
        ["28-009"] = ("A page's tab order does not follow the structure.", "Set the page's tab order to Use Document Structure (Page Properties > Tab Order), which writes /Tabs /S."),
        ["28-010"] = ("A widget annotation is not nested in a Form structure element.", "Tag the field in a Form element (Acrobat: Autotag, or Tags panel > Find > Unmarked Annotations)."),
        ["28-011"] = ("A link annotation is not nested in a Link structure element.", "Tag the link in a Link element together with its text (Acrobat: Autotag, or Tags panel > Find > Unmarked Links)."),
        ["28-012"] = ("A link annotation has no alternate description.", "Give the link a description in its Contents entry."),
        ["28-014"] = ("A media clip has no content type (CT).", "Give the media clip data dictionary a /CT with the clip's MIME type."),
        ["28-015"] = ("A media clip has no alternate description.", "Give the media clip data dictionary an /Alt describing it."),
        ["28-016"] = ("A file attachment's specification lacks F or UF.", "Re-attach the file so that its file specification has non-empty /F and /UF."),
        ["28-017"] = ("A PrinterMark annotation is in the structure tree.", "Remove the PrinterMark annotation from the structure tree; it is not content."),
        ["30-001"] = ("A reference XObject is present.", "Replace the reference XObject with its content."),
        ["30-002"] = ("A form XObject with tagged content is drawn more than once.", "Draw a tagged form XObject once, or copy it for each use so that each use has its own tags."),
        ["31-001"] = ("A CIDFont's character collection differs from its CMap's.", "Re-embed the font from the source application."),
        ["31-004"] = ("An embedded Type 2 CIDFont has no valid CIDToGIDMap.", "Re-embed the font from the source application."),
        ["31-005"] = ("A CMap is neither predefined nor embedded.", "Re-embed the font from the source application."),
        ["31-006"] = ("A CMap gives two writing modes.", "Re-embed the font from the source application."),
        ["31-007"] = ("A CMap refers to a CMap that is not predefined.", "Re-embed the font from the source application."),
        ["31-008"] = ("A font is not embedded.", "Embed the font (Acrobat: Preflight > Embed missing fonts, or print to PDF with fonts embedded)."),
        ["31-009"] = ("An embedded font program cannot be read or does not suit the font.", "Re-embed the font from the source application."),
        ["31-010"] = ("A subset font's CharSet does not list every glyph.", "Re-embed the font from the source application."),
        ["31-011"] = ("A subset font's CIDSet does not list every glyph.", "Re-embed the font from the source application."),
        ["31-012"] = ("A glyph's width differs from the font program's.", "Re-embed the font from the source application."),
        ["31-013"] = ("A non-symbolic TrueType font has no Unicode or Mac Roman cmap.", "Re-embed the font from the source application."),
        ["31-014"] = ("A non-symbolic TrueType font has neither the MacRoman nor the WinAnsi encoding.", "Re-embed the font from the source application."),
        ["31-016"] = ("A symbolic TrueType font has an Encoding entry.", "Re-embed the font from the source application."),
        ["31-017"] = ("A symbolic TrueType font's cmap does not suit it.", "Re-embed the font from the source application."),
        ["31-030"] = ("Text uses the .notdef glyph.", "Re-create the text with a font that has every glyph used, or remove the characters (Preflight > Remove .notdef glyphs)."),
        ["XX-000"] = ("Part of the document could not be checked.", "Repair the file (save it again from its source application) and check again."),
    };

    /// <summary>The failure condition the checkpoint describes.</summary>
    public static string Condition(string checkpoint) => All.TryGetValue(checkpoint, out var e) ? e.Condition : string.Empty;

    /// <summary>How to fix the checkpoint's failure.</summary>
    public static string Fix(string checkpoint) => All.TryGetValue(checkpoint, out var e) ? e.Fix : string.Empty;

    public static IEnumerable<string> Known => All.Keys;
}
