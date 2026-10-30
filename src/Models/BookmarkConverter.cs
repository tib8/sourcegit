using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public sealed class BookmarkConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Bookmarks.FromSerialized(reader.GetInt32());
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(Bookmarks.ToSerialized(value));
        }
    }
}
