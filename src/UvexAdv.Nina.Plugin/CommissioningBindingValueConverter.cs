using System.IO;
using System.Text.Json;

namespace UvexAdv.Nina.Plugin;

internal static class CommissioningBindingValueConverter
{
    public static object Convert(JsonElement value, Type targetType, string name)
    {
        if (value.ValueKind == JsonValueKind.Null && targetType == typeof(double) &&
            string.Equals(name, nameof(UvexPluginSettings.QhyTargetTemperatureC), StringComparison.OrdinalIgnoreCase))
        {
            return double.NaN;
        }
        if (targetType == typeof(string)) return value.GetString() ?? string.Empty;
        if (targetType == typeof(bool)) return value.GetBoolean();
        if (targetType == typeof(int)) return value.GetInt32();
        if (targetType == typeof(short)) return value.GetInt16();
        if (targetType == typeof(double)) return value.GetDouble();
        if (targetType.IsEnum)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var raw) && Enum.IsDefined(targetType, raw))
                return Enum.ToObject(targetType, raw);
            if (value.ValueKind == JsonValueKind.String &&
                Enum.TryParse(targetType, value.GetString(), ignoreCase: true, out var parsed) &&
                parsed is not null && Enum.IsDefined(targetType, parsed))
                return parsed;
            throw new InvalidDataException($"bindings 设置 '{name}' 必须是已定义的数值或名称枚举值。");
        }
        throw new InvalidDataException($"bindings 设置 '{name}' 使用了不支持的类型 {targetType.Name}。");
    }
}
