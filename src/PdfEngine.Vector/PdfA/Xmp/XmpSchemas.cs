using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PdfEngine.Vector.PdfA.Xmp;

/// <summary>
/// The predefined XMP schemas PDF/A allows without an extension schema (XMP Specification 2005,
/// ISO 19005-1 and -2), with the value type of every property and of every structure's fields,
/// and the checks of simple values' lexical forms.
/// </summary>
public static class XmpSchemas
{
    public const string Dc = "http://purl.org/dc/elements/1.1/";
    public const string Xmp = "http://ns.adobe.com/xap/1.0/";
    public const string XmpRights = "http://ns.adobe.com/xap/1.0/rights/";
    public const string XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    public const string XmpBJ = "http://ns.adobe.com/xap/1.0/bj/";
    public const string XmpTPg = "http://ns.adobe.com/xap/1.0/t/pg/";
    public const string XmpDM = "http://ns.adobe.com/xmp/1.0/DynamicMedia/";
    public const string Pdf = "http://ns.adobe.com/pdf/1.3/";
    public const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    public const string Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    public const string Tiff = "http://ns.adobe.com/tiff/1.0/";
    public const string Exif = "http://ns.adobe.com/exif/1.0/";
    public const string Aux = "http://ns.adobe.com/exif/1.0/aux/";
    public const string PdfaId = "http://www.aiim.org/pdfa/ns/id/";
    public const string PdfaExtension = "http://www.aiim.org/pdfa/ns/extension/";
    public const string PdfaSchema = "http://www.aiim.org/pdfa/ns/schema#";
    public const string PdfaProperty = "http://www.aiim.org/pdfa/ns/property#";
    public const string PdfaType = "http://www.aiim.org/pdfa/ns/type#";
    public const string PdfaField = "http://www.aiim.org/pdfa/ns/field#";
    public const string XmpIdq = "http://ns.adobe.com/xmp/Identifier/qual/1.0/";

    public const string StDim = "http://ns.adobe.com/xap/1.0/sType/Dimensions#";
    public const string StFnt = "http://ns.adobe.com/xap/1.0/sType/Font#";
    public const string XapG = "http://ns.adobe.com/xap/1.0/g/";
    public const string XapGImg = "http://ns.adobe.com/xap/1.0/g/img/";
    public const string StEvt = "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#";
    public const string StRef = "http://ns.adobe.com/xap/1.0/sType/ResourceRef#";
    public const string StVer = "http://ns.adobe.com/xap/1.0/sType/Version#";
    public const string StJob = "http://ns.adobe.com/xap/1.0/sType/Job#";

    /// <summary>A structure type: the namespace of its fields and their value types.</summary>
    public sealed record StructType(string Name, string Namespace, IReadOnlyDictionary<string, string> Fields);

    public static readonly IReadOnlyDictionary<string, StructType> Structs = BuildStructs();

    /// <summary>Property value types by schema namespace, then property name.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Schemas = BuildSchemas();

    /// <summary>Preferred prefixes of the predefined schemas (PDF/A-1 requires them).</summary>
    public static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>
    {
        [Dc] = "dc", [Xmp] = "xmp", [XmpRights] = "xmpRights", [XmpMM] = "xmpMM", [XmpBJ] = "xmpBJ", [XmpTPg] = "xmpTPg", [XmpDM] = "xmpDM",
        [Pdf] = "pdf", [Photoshop] = "photoshop", [Crs] = "crs", [Tiff] = "tiff", [Exif] = "exif", [Aux] = "aux", [PdfaId] = "pdfaid",
    };

    private static Dictionary<string, string> F(string spec) =>
        spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(' ', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, StructType> BuildStructs()
    {
        var list = new[]
        {
            new StructType("Dimensions", StDim, F("w Real; h Real; unit Text")),
            new StructType("Font", StFnt, F("fontName Text; fontFamily Text; fontFace Text; fontType Text; versionString Text; composite Boolean; fontFileName Text; childFontFiles Seq Text")),
            new StructType("Colorant", XapG, F("swatchName Text; mode Text; type Text; cyan Real; magenta Real; yellow Real; black Real; red Integer; green Integer; blue Integer; L Real; A Integer; B Integer")),
            new StructType("Thumbnail", XapGImg, F("height Integer; width Integer; format Text; image Text")),
            new StructType("ResourceEvent", StEvt, F("action Text; instanceID URI; parameters Text; softwareAgent AgentName; when Date; changed Text")),
            new StructType("ResourceRef", StRef, F("instanceID URI; documentID URI; versionID Text; renditionClass Text; renditionParams Text; manager AgentName; managerVariant Text; manageTo URI; manageUI URI; lastModifyDate Date; filePath URI; fromPart Text; toPart Text; maskMarkers Text; originalDocumentID Text; alternatePaths Seq URI")),
            new StructType("Version", StVer, F("comments Text; event ResourceEvent; modifyDate Date; modifier ProperName; version Text")),
            new StructType("Job", StJob, F("name Text; id Text; url URL")),
            new StructType("Flash", Exif, F("Fired Boolean; Return Integer; Mode Integer; Function Boolean; RedEyeMode Boolean")),
            new StructType("OECF", Exif, F("Columns Integer; Rows Integer; Names Seq Text; Values Seq Rational")),
            new StructType("CFAPattern", Exif, F("Columns Integer; Rows Integer; Values Seq Integer")),
            new StructType("DeviceSettings", Exif, F("Columns Integer; Rows Integer; Settings Seq Text")),
            new StructType("ProjectLink", XmpDM, F("type Text; path URI")),
            new StructType("Time", XmpDM, F("value Integer; scale Rational")),
            new StructType("Timecode", XmpDM, F("timeValue Text; timeFormat Text")),
            new StructType("Marker", XmpDM, F("name Text; comment Text; startTime Time; duration Time; location URI; target Text; type Text")),
            new StructType("Media", XmpDM, F("path URI; track Text; startTime Time; duration Time; managed Boolean; webStatement URI")),
            new StructType("ResampleParams", XmpDM, F("quality Text")),
            new StructType("BeatSpliceParams", XmpDM, F("riseInDecibel Real; riseInTimeDuration Time; useFileBeatsMarker Boolean")),
            new StructType("TimeScaleParams", XmpDM, F("quality Text; frameSize Real; frameOverlappingPercentage Real")),
        };
        return list.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildSchemas() => new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        [Dc] = F("contributor Bag ProperName; coverage Text; creator Seq ProperName; date Seq Date; description Lang Alt; format MIMEType; identifier Text; " +
                 "language Bag Locale; publisher Bag ProperName; relation Bag Text; rights Lang Alt; source Text; subject Bag Text; title Lang Alt; type Bag Text"),
        [Xmp] = F("Advisory Bag XPath; BaseURL URL; CreateDate Date; CreatorTool AgentName; Identifier Bag Text; Label Text; MetadataDate Date; ModifyDate Date; " +
                  "Nickname Text; Rating Real; Thumbnails Alt Thumbnail"),
        [XmpRights] = F("Certificate URL; Marked Boolean; Owner Bag ProperName; UsageTerms Lang Alt; WebStatement URL"),
        [XmpMM] = F("DerivedFrom ResourceRef; DocumentID URI; History Seq ResourceEvent; InstanceID URI; ManagedFrom ResourceRef; Manager AgentName; ManageTo URI; " +
                    "ManageUI URI; ManagerVariant Text; RenditionClass Text; RenditionParams Text; VersionID Text; Versions Seq Version; LastURL URL; RenditionOf ResourceRef; SaveID Integer"),
        [XmpBJ] = F("JobRef Bag Job"),
        [XmpTPg] = F("MaxPageSize Dimensions; NPages Integer; Fonts Bag Font; Colorants Seq Colorant; PlateNames Seq Text"),
        [XmpDM] = F("projectRef ProjectLink; videoFrameRate Text; videoFrameSize Dimensions; videoPixelAspectRatio Rational; videoPixelDepth Text; videoColorSpace Text; " +
                    "videoAlphaMode Text; videoAlphaPremultipleColor Colorant; videoAlphaUnityIsTransparent Boolean; videoCompressor Text; videoFieldOrder Text; pullDown Text; " +
                    "audioSampleRate Integer; audioSampleType Text; audioChannelType Text; audioCompressor Text; speakerPlacement Text; fileDataRate Rational; tapeName Text; " +
                    "altTapeName Text; startTimecode Timecode; altTimecode Timecode; duration Time; scene Text; shotName Text; shotDate Date; shotLocation Text; logComment Text; " +
                    "markers Seq Marker; contributedMedia Bag Media; absPeakAudioFilePath URI; relativePeakAudioFilePath URI; videoModDate Date; audioModDate Date; metadataModDate Date; " +
                    "artist Text; album Text; trackNumber Integer; genre Text; copyright Text; releaseDate Date; composer Text; engineer Text; tempo Real; instrument Text; " +
                    "introTime Time; outCue Time; relativeTimestamp Time; loop Boolean; numberOfBeats Real; key Text; stretchMode Text; timeScaleParams TimeScaleParams; " +
                    "resampleParams ResampleParams; beatSpliceParams BeatSpliceParams; timeSignature Text; scaleType Text"),
        [Pdf] = F("Keywords Text; PDFVersion Text; Producer AgentName; Trapped Text"),
        [Photoshop] = F("AuthorsPosition Text; CaptionWriter ProperName; Category Text; City Text; Country Text; Credit Text; DateCreated Date; Headline Text; " +
                        "Instructions Text; Source Text; State Text; SupplementalCategories Bag Text; TransmissionReference Text; Urgency Integer; ColorMode Integer; History Text; ICCProfile Text"),
        [Crs] = F("AutoBrightness Boolean; AutoContrast Boolean; AutoExposure Boolean; AutoShadows Boolean; BlueHue Integer; BlueSaturation Integer; Brightness Integer; " +
                  "CameraProfile Text; ChromaticAberrationB Integer; ChromaticAberrationR Integer; ColorNoiseReduction Integer; Contrast Integer; CropTop Real; CropLeft Real; " +
                  "CropBottom Real; CropRight Real; CropAngle Real; CropWidth Real; CropHeight Real; CropUnits Integer; Exposure Real; GreenHue Integer; GreenSaturation Integer; " +
                  "HasCrop Boolean; HasSettings Boolean; LuminanceSmoothing Integer; RawFileName Text; RedHue Integer; RedSaturation Integer; Saturation Integer; Shadows Integer; " +
                  "ShadowTint Integer; Sharpness Integer; Temperature Integer; Tint Integer; ToneCurve Seq Text; ToneCurveName Text; Version Text; VignetteAmount Integer; " +
                  "VignetteMidpoint Integer; WhiteBalance Text"),
        [Tiff] = F("ImageWidth Integer; ImageLength Integer; BitsPerSample Seq Integer; Compression Integer; PhotometricInterpretation Integer; Orientation Integer; " +
                   "SamplesPerPixel Integer; PlanarConfiguration Integer; YCbCrSubSampling Seq Integer; YCbCrPositioning Integer; XResolution Rational; YResolution Rational; " +
                   "ResolutionUnit Integer; TransferFunction Seq Integer; WhitePoint Seq Rational; PrimaryChromaticities Seq Rational; YCbCrCoefficients Seq Rational; " +
                   "ReferenceBlackWhite Seq Rational; DateTime Date; ImageDescription Lang Alt; Make ProperName; Model ProperName; Software AgentName; Artist ProperName; Copyright Lang Alt; NativeDigest Text"),
        [Exif] = F("ExifVersion Text; FlashpixVersion Text; ColorSpace Integer; ComponentsConfiguration Seq Integer; CompressedBitsPerPixel Rational; PixelXDimension Integer; " +
                   "PixelYDimension Integer; UserComment Lang Alt; RelatedSoundFile Text; DateTimeOriginal Date; DateTimeDigitized Date; ExposureTime Rational; FNumber Rational; " +
                   "ExposureProgram Integer; SpectralSensitivity Text; ISOSpeedRatings Seq Integer; OECF OECF; ShutterSpeedValue Rational; ApertureValue Rational; " +
                   "BrightnessValue Rational; ExposureBiasValue Rational; MaxApertureValue Rational; SubjectDistance Rational; MeteringMode Integer; LightSource Integer; " +
                   "Flash Flash; FocalLength Rational; SubjectArea Seq Integer; FlashEnergy Rational; SpatialFrequencyResponse OECF; FocalPlaneXResolution Rational; " +
                   "FocalPlaneYResolution Rational; FocalPlaneResolutionUnit Integer; SubjectLocation Seq Integer; ExposureIndex Rational; SensingMethod Integer; FileSource Integer; " +
                   "SceneType Integer; CFAPattern CFAPattern; CustomRendered Integer; ExposureMode Integer; WhiteBalance Integer; DigitalZoomRatio Rational; " +
                   "FocalLengthIn35mmFilm Integer; SceneCaptureType Integer; GainControl Integer; Contrast Integer; Saturation Integer; Sharpness Integer; " +
                   "DeviceSettingDescription DeviceSettings; SubjectDistanceRange Integer; ImageUniqueID Text; GPSVersionID Text; GPSLatitude GPSCoordinate; " +
                   "GPSLongitude GPSCoordinate; GPSAltitudeRef Integer; GPSAltitude Rational; GPSTimeStamp Date; GPSSatellites Text; GPSStatus Text; GPSMeasureMode Text; " +
                   "GPSDOP Rational; GPSSpeedRef Text; GPSSpeed Rational; GPSTrackRef Text; GPSTrack Rational; GPSImgDirectionRef Text; GPSImgDirection Rational; " +
                   "GPSMapDatum Text; GPSDestLatitude GPSCoordinate; GPSDestLongitude GPSCoordinate; GPSDestBearingRef Text; GPSDestBearing Rational; " +
                   "GPSDestDistanceRef Text; GPSDestDistance Rational; GPSProcessingMethod Text; GPSAreaInformation Text; GPSDifferential Integer; MakerNote Text; NativeDigest Text"),
        [Aux] = F("Lens Text; SerialNumber Text"),
        [PdfaId] = F("part Integer; amd Text; conformance Text; corr Text; rev Integer"),
    };

    private static readonly string[] TextTypes =
        { "Text", "URI", "URL", "MIMEType", "Locale", "AgentName", "ProperName", "RenderingIntent", "GUID", "XPath", "RenditionClass", "FrameCount", "FrameRate", "Choice" };

    private static readonly Regex IntegerForm = new(@"^[+-]?\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex RealForm = new(@"^[+-]?(\d+(\.\d*)?|\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RationalForm = new(@"^[+-]?\d+/[+-]?\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex GpsForm = new(@"^\d{1,3},\d{1,2}(,\d{1,2}|\.\d+)?[NSEW]$", RegexOptions.CultureInvariant);
    private static readonly Regex DateForm = new(
        @"^\d{4}(-\d{2}(-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?)?)?)?$", RegexOptions.CultureInvariant);

    /// <summary>Whether a simple value's text has the lexical form of a simple type.</summary>
    public static bool IsValidSimple(string type, string text) => type switch
    {
        "Integer" => IntegerForm.IsMatch(text),
        "Real" => RealForm.IsMatch(text),
        "Boolean" => text is "True" or "False",
        "Date" => DateForm.IsMatch(text.Trim()),
        "Rational" => RationalForm.IsMatch(text),
        "GPSCoordinate" => GpsForm.IsMatch(text),
        _ => true,
    };

    public static bool IsSimpleType(string type) => type is "Integer" or "Real" or "Boolean" or "Date" or "Rational" or "GPSCoordinate" || TextTypes.Contains(type)
                                                    || type.StartsWith("Closed Choice", StringComparison.Ordinal) || type.StartsWith("Open Choice", StringComparison.Ordinal);

    /// <summary>
    /// Why <paramref name="value"/> does not fit <paramref name="type"/>, or null when it does.
    /// <paramref name="structs"/> resolves structure types (the predefined ones and those an extension schema defines).
    /// </summary>
    public static string? Check(XmpValue value, string type, Func<string, StructType?> structs)
    {
        type = type.Trim();
        string lower = type.ToLowerInvariant();
        if (lower == "lang alt")
        {
            if (value.Kind != XmpKind.Alt) return "should be a language alternative (rdf:Alt)";
            foreach (var item in value.Items)
            {
                if (item.Kind != XmpKind.Simple) return "has a language alternative whose item is not text";
                if (item.Lang == null) return "has a language alternative item without xml:lang";
            }
            return null;
        }
        foreach (var (prefix, kind) in new[] { ("bag ", XmpKind.Bag), ("seq ", XmpKind.Seq), ("alt ", XmpKind.Alt) })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (value.Kind != kind) return $"should be an array of kind {kind} (rdf:{kind})";
            string itemType = type[prefix.Length..];
            foreach (var item in value.Items)
                if (Check(item, itemType, structs) is { } why) return "has an item that " + why;
            return null;
        }
        if (type.StartsWith("Closed Choice of ", StringComparison.OrdinalIgnoreCase)) type = type["Closed Choice of ".Length..];
        else if (type.StartsWith("Open Choice of ", StringComparison.OrdinalIgnoreCase)) type = type["Open Choice of ".Length..];
        if (IsSimpleType(type))
        {
            if (value.Kind != XmpKind.Simple) return $"should be a simple {type} value";
            return IsValidSimple(type, value.Text) ? null : $"is \"{Shorten(value.Text)}\", not a valid {type}";
        }
        if (structs(type) is { } st)
        {
            if (st.Fields.Count == 0) return null; // a type whose fields are not described cannot be checked further
            if (value.Kind != XmpKind.Struct) return $"should be a {st.Name} structure";
            foreach (var field in value.Fields)
            {
                if (field.Namespace != st.Namespace || !st.Fields.TryGetValue(field.Name, out var fieldType))
                    return $"has the field {field.Prefix}:{field.Name}, which a {st.Name} does not have";
                if (Check(field.Value, fieldType, structs) is { } why) return $"has a field {field.Name} that " + why;
            }
            return null;
        }
        return $"has the undefined value type \"{type}\"";
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..37] + "..." : s;

    public static StructType? Predefined(string name) => Structs.TryGetValue(name, out var s) ? s : null;

    /// <summary>
    /// Properties the XMP 2005 tables have that XMP 2004 (which PDF/A-1 refers to) does not: whole
    /// schemas (Camera Raw, auxiliary EXIF) and some later properties.
    /// </summary>
    public static bool NotIn2004(string ns, string name) => ns switch
    {
        Crs or Aux => true,
        Xmp => name is "Label" or "Rating",
        XmpTPg => name is "Colorants" or "Fonts" or "PlateNames",
        Pdf => name is "Trapped",
        Photoshop => name is "ColorMode" or "History" or "ICCProfile",
        _ => false,
    };

    /// <summary>Value types XMP 2004 gives differently from XMP 2005.</summary>
    public static readonly IReadOnlyDictionary<(string, string), string> Types2004 = new Dictionary<(string, string), string>
    {
        [(Photoshop, "SupplementalCategories")] = "Text",
        [(Exif, "GPSMeasureMode")] = "Integer",
    };
}
