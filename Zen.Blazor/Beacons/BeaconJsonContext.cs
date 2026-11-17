using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zen.Blazor.Beacons;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(BeaconEntry))]
[JsonSerializable(typeof(AcceptedBeacon))]
[JsonSerializable(typeof(BeaconSummary))]
[JsonSerializable(typeof(BeaconPage))]
[JsonSerializable(typeof(BeaconKeys))]
[JsonSerializable(typeof(StorageStatus))]
public sealed partial class BeaconJsonContext : JsonSerializerContext;
