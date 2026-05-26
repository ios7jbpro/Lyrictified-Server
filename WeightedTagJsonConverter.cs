using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyrictified.Server;

public sealed class WeightedTagJsonConverter : JsonConverter<WeightedTag>
{
    public override WeightedTag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new WeightedTag(reader.GetString() ?? "", 0);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Tag must be either a string or an object.");
        }

        var name = "";
        var score = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new WeightedTag(name, Math.Clamp(score, 0, 100));
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected tag property name.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "name":
                    name = reader.GetString() ?? "";
                    break;
                case "score":
                    score = reader.GetInt32();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Tag object was not closed.");
    }

    public override void Write(Utf8JsonWriter writer, WeightedTag value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteNumber("score", Math.Clamp(value.Score, 0, 100));
        writer.WriteEndObject();
    }
}
