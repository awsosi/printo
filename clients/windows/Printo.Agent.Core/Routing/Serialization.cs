using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printo.Agent.Core.Routing;

/// <summary>
/// JSON wiring for the rule bundle and the conformance fixtures.
/// </summary>
/// <remarks>
/// The schema is authored in TypeScript, so the .NET side adapts to it rather than the other
/// way round: camelCase names, a predicate encoded as a single-key object, and a rectangle
/// encoded as either a string or an object. Getting this exactly right is what lets the same
/// bundle bytes drive both engines.
/// </remarks>
public static class RoutingJson
{
    /// <summary>The one options instance used everywhere; nothing else may configure JSON.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new PredicateConverter());
        return options;
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"null while deserializing {typeof(T).Name}");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Reads a rectangle written either as a keyword (<c>"inkBox"</c>) or as an object
/// (<c>{ "unit": "mm", "xMm": 10, ... }</c>).
/// </summary>
public sealed class RectSpecConverter : JsonConverter<RectSpec>
{
    public override RectSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new RectSpec { Named = reader.GetString() };
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var unit = element.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() : null;

        static double Read(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetDouble();
                }
            }

            return 0;
        }

        return new RectSpec
        {
            Unit = unit,
            X = Read(element, "xMm", "x"),
            Y = Read(element, "yMm", "y"),
            W = Read(element, "widthMm", "w"),
            H = Read(element, "heightMm", "h"),
        };
    }

    public override void Write(Utf8JsonWriter writer, RectSpec value, JsonSerializerOptions options)
    {
        if (value.Named is not null)
        {
            writer.WriteStringValue(value.Named);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("unit", value.Unit ?? "mm");
        if (value.Unit == "pageFraction")
        {
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("w", value.W);
            writer.WriteNumber("h", value.H);
        }
        else
        {
            writer.WriteNumber("xMm", value.X);
            writer.WriteNumber("yMm", value.Y);
            writer.WriteNumber("widthMm", value.W);
            writer.WriteNumber("heightMm", value.H);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Reads <c>"auto"</c> or a numeric quarter turn.</summary>
public sealed class RotateSpecConverter : JsonConverter<RotateSpec>
{
    public override RotateSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return RotateSpec.Auto;
            }

            return RotateSpec.Fixed(int.Parse(text ?? "0", System.Globalization.CultureInfo.InvariantCulture));
        }

        return RotateSpec.Fixed(reader.GetInt32());
    }

    public override void Write(Utf8JsonWriter writer, RotateSpec value, JsonSerializerOptions options)
    {
        if (value.IsAuto)
        {
            writer.WriteStringValue("auto");
            return;
        }

        writer.WriteNumberValue(value.Degrees);
    }
}

/// <summary>
/// Reads the predicate union, which is encoded as an object with exactly one known key.
/// </summary>
/// <remarks>
/// A discriminator field would be easier to parse but harder to write by hand, and these
/// rules are hand-written and hand-reviewed as often as they are generated by the admin UI.
/// An unknown key is an error rather than a silently-ignored rule: a bundle that references
/// a predicate this agent version does not implement must fail loudly, not route pages with
/// half a condition.
/// </remarks>
public sealed class PredicateConverter : JsonConverter<Predicate>
{
    public override Predicate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"predicate must be an object, found {element.ValueKind}");
        }

        // Options without this converter, to deserialize the nested condition objects.
        var inner = new JsonSerializerOptions(options);
        for (var index = inner.Converters.Count - 1; index >= 0; index--)
        {
            if (inner.Converters[index] is PredicateConverter)
            {
                inner.Converters.RemoveAt(index);
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "all":
                    return new AllPredicate { All = ReadList(property.Value, options) };
                case "any":
                    return new AnyPredicate { Any = ReadList(property.Value, options) };
                case "not":
                    return new NotPredicate { Not = Read(property.Value, options) };
                case "text":
                    return new TextPredicate { Text = Require<TextCondition>(property.Value, inner) };
                case "ocr":
                    return new OcrPredicate { Ocr = Require<OcrCondition>(property.Value, inner) };
                case "barcode":
                    return new BarcodePredicate { Barcode = Require<BarcodeCondition>(property.Value, inner) };
                case "image":
                    return new ImagePredicate { Image = Require<ImageCondition>(property.Value, inner) };
                case "geometry":
                    return new GeometryPredicate { Geometry = Require<GeometryCondition>(property.Value, inner) };
                case "carrier":
                    return new CarrierPredicate { Carrier = Require<CarrierCondition>(property.Value, inner) };
                case "pageIndex":
                    return new PageIndexPredicate { PageIndex = Require<PageIndexCondition>(property.Value, inner) };
                default:
                    throw new JsonException($"unknown predicate '{property.Name}'");
            }
        }

        throw new JsonException("empty predicate object");
    }

    public override void Write(Utf8JsonWriter writer, Predicate value, JsonSerializerOptions options)
    {
        var inner = new JsonSerializerOptions(options);
        for (var index = inner.Converters.Count - 1; index >= 0; index--)
        {
            if (inner.Converters[index] is PredicateConverter)
            {
                inner.Converters.RemoveAt(index);
            }
        }

        writer.WriteStartObject();
        switch (value)
        {
            case AllPredicate all:
                writer.WritePropertyName("all");
                WriteList(writer, all.All, options);
                break;
            case AnyPredicate any:
                writer.WritePropertyName("any");
                WriteList(writer, any.Any, options);
                break;
            case NotPredicate not:
                writer.WritePropertyName("not");
                Write(writer, not.Not, options);
                break;
            case TextPredicate text:
                writer.WritePropertyName("text");
                JsonSerializer.Serialize(writer, text.Text, inner);
                break;
            case OcrPredicate ocr:
                writer.WritePropertyName("ocr");
                JsonSerializer.Serialize(writer, ocr.Ocr, inner);
                break;
            case BarcodePredicate barcode:
                writer.WritePropertyName("barcode");
                JsonSerializer.Serialize(writer, barcode.Barcode, inner);
                break;
            case ImagePredicate image:
                writer.WritePropertyName("image");
                JsonSerializer.Serialize(writer, image.Image, inner);
                break;
            case GeometryPredicate geometry:
                writer.WritePropertyName("geometry");
                JsonSerializer.Serialize(writer, geometry.Geometry, inner);
                break;
            case CarrierPredicate carrier:
                writer.WritePropertyName("carrier");
                JsonSerializer.Serialize(writer, carrier.Carrier, inner);
                break;
            case PageIndexPredicate pageIndex:
                writer.WritePropertyName("pageIndex");
                JsonSerializer.Serialize(writer, pageIndex.PageIndex, inner);
                break;
            default:
                throw new JsonException($"unsupported predicate type {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    private static IReadOnlyList<Predicate> ReadList(JsonElement element, JsonSerializerOptions options)
    {
        var items = new List<Predicate>();
        foreach (var item in element.EnumerateArray())
        {
            items.Add(Read(item, options));
        }

        return items;
    }

    private static void WriteList(Utf8JsonWriter writer, IReadOnlyList<Predicate> items, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in items)
        {
            new PredicateConverter().Write(writer, item, options);
        }

        writer.WriteEndArray();
    }

    private static Predicate Read(JsonElement element, JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(element.GetRawText()));
        reader.Read();
        return new PredicateConverter().Read(ref reader, typeof(Predicate), options);
    }

    private static T Require<T>(JsonElement element, JsonSerializerOptions options) =>
        element.Deserialize<T>(options) ?? throw new JsonException($"null {typeof(T).Name}");
}
