using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Radar;

[JsonConverter(typeof(StringEnumConverter))]
public enum TargetType
{
    Tile,
    Entity
}
