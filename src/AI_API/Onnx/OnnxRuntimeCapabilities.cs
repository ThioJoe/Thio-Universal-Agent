using System.Collections;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;

namespace Thio_Universal_Agent.AI_API.Onnx;

internal static class OnnxRuntimeCapabilities
{
    internal static OnnxRuntimeCapabilitiesSnapshot GetSnapshot()
    {
        try
        {
            // If VC Redist aren't there, it will cause a crash that won't be caught
            if (!System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "vcruntime140.dll")) ||
                !System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "vcruntime140_1.dll")))
            {
                return new OnnxRuntimeCapabilitiesSnapshot(
                    OrtVersion: null,
                    AvailableProviders: Array.Empty<string>(),
                    HardwareDevices: Array.Empty<OnnxRuntimeHardwareDeviceInfo>(),
                    EpDevices: Array.Empty<OnnxRuntimeEpDeviceInfo>(),
                    Error: "The Microsoft Visual C++ Redistributable is not installed on this machine. Please install it to use local ONNX models.");
            }

            OrtEnv env = OrtEnv.Instance();
            object[] hardwareDevices = TryInvokeSequence(env, "GetHardwareDevices");
            object[] epDevices = TryInvokeSequence(env, "GetEpDevices");

            return new OnnxRuntimeCapabilitiesSnapshot(
                OrtVersion: env.GetVersionString(),
                AvailableProviders: env.GetAvailableProviders(),
                HardwareDevices: hardwareDevices.Select(ToHardwareDeviceInfo).ToArray(),
                EpDevices: epDevices.Select(ToEpDeviceInfo).ToArray(),
                Error: null);
        }
        catch (Exception ex)
        {
            return new OnnxRuntimeCapabilitiesSnapshot(
                OrtVersion: null,
                AvailableProviders: Array.Empty<string>(),
                HardwareDevices: Array.Empty<OnnxRuntimeHardwareDeviceInfo>(),
                EpDevices: Array.Empty<OnnxRuntimeEpDeviceInfo>(),
                Error: ex.Message);
        }
    }

    internal static bool IsProviderAvailable(string providerName, out string? availabilityDetail)
    {
        OnnxRuntimeCapabilitiesSnapshot snapshot = GetSnapshot();
        if (snapshot.Error is not null)
        {
            availabilityDetail = $"Unable to query ONNX Runtime capabilities: {snapshot.Error}";
            return false;
        }

        if (snapshot.AvailableProviders.Any(candidate => MatchesProviderName(candidate, providerName)))
        {
            availabilityDetail = null;
            return true;
        }

        availabilityDetail = snapshot.AvailableProviders.Count == 0
            ? "ONNX Runtime did not report any available execution providers."
            : $"ONNX Runtime reports these providers: {string.Join(", ", snapshot.AvailableProviders)}.";
        return false;
    }

    internal static string GetProviderAvailabilityMessage(string providerName)
    {
        OnnxRuntimeCapabilitiesSnapshot snapshot = GetSnapshot();
        if (snapshot.Error is not null)
            return $"Capability query failed: {snapshot.Error}";

        string providerSummary = snapshot.AvailableProviders.Count == 0
            ? "none"
            : string.Join(", ", snapshot.AvailableProviders);

        OnnxRuntimeEpDeviceInfo[] matchingDevices = snapshot.EpDevices
            .Where(device => MatchesProviderName(device.EpName, providerName))
            .ToArray();

        if (matchingDevices.Length == 0)
            return $"ONNX Runtime reported these providers: {providerSummary}. No matching EP devices were reported for '{providerName}'.";

        string deviceSummary = string.Join(
            "; ",
            matchingDevices.Select(device =>
            {
                string hardwareSummary = $"{device.HardwareDevice.Type} {device.HardwareDevice.Vendor}";
                return device.SuggestedDeviceId is int suggestedDeviceId
                    ? $"{hardwareSummary} (suggested device_id={suggestedDeviceId})"
                    : hardwareSummary;
            }));

        return $"ONNX Runtime reported these providers: {providerSummary}. Matching EP devices: {deviceSummary}.";
    }

    private static OnnxRuntimeHardwareDeviceInfo ToHardwareDeviceInfo(object? device)
        => new(
            Type: GetPropertyValue(device, "Type")?.ToString() ?? "Unknown",
            Vendor: GetPropertyValue(device, "Vendor") as string ?? string.Empty,
            VendorId: GetUInt32PropertyValue(device, "VendorId"),
            DeviceId: GetUInt32PropertyValue(device, "DeviceId"),
            Metadata: ToDictionary(GetPropertyValue(device, "Metadata")));

    private static OnnxRuntimeEpDeviceInfo ToEpDeviceInfo(object? device)
    {
        Dictionary<string, string> epMetadata = ToDictionary(GetPropertyValue(device, "EpMetadata"));
        Dictionary<string, string> epOptions = ToDictionary(GetPropertyValue(device, "EpOptions"));
        OnnxRuntimeHardwareDeviceInfo hardwareDevice = ToHardwareDeviceInfo(GetPropertyValue(device, "HardwareDevice"));

        int? suggestedDeviceId = TryGetSuggestedDeviceId(epOptions)
            ?? TryGetSuggestedDeviceId(epMetadata)
            ?? TryGetSuggestedDeviceId(hardwareDevice.Metadata);

        return new OnnxRuntimeEpDeviceInfo(
            EpName: GetPropertyValue(device, "EpName") as string ?? string.Empty,
            EpVendor: GetPropertyValue(device, "EpVendor") as string ?? string.Empty,
            SuggestedDeviceId: suggestedDeviceId,
            EpMetadata: epMetadata,
            EpOptions: epOptions,
            HardwareDevice: hardwareDevice);
    }

    private static Dictionary<string, string> ToDictionary(object? pairs)
    {
        if (pairs is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            InvokeIfPresent(pairs, "Refresh");

            object? entries = GetPropertyValue(pairs, "Entries") ?? pairs;
            if (entries is IEnumerable enumerable)
            {
                Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

                foreach (object? entry in enumerable)
                {
                    if (entry is null)
                        continue;

                    if (entry is DictionaryEntry dictionaryEntry)
                    {
                        values[dictionaryEntry.Key?.ToString() ?? string.Empty] = dictionaryEntry.Value?.ToString() ?? string.Empty;
                        continue;
                    }

                    object? key = GetPropertyValue(entry, "Key");
                    if (key is null)
                        continue;

                    object? value = GetPropertyValue(entry, "Value");
                    values[key.ToString() ?? string.Empty] = value?.ToString() ?? string.Empty;
                }

                return values;
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object[] TryInvokeSequence(object target, string methodName)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
            return Array.Empty<object>();

        if (method.Invoke(target, Array.Empty<object>()) is not IEnumerable enumerable)
            return Array.Empty<object>();

        return enumerable.Cast<object?>().Where(item => item is not null).Cast<object>().ToArray();
    }

    private static void InvokeIfPresent(object target, string methodName)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(target, Array.Empty<object>());
    }

    private static object? GetPropertyValue(object? target, string propertyName)
        => target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static uint GetUInt32PropertyValue(object? target, string propertyName)
    {
        object? value = GetPropertyValue(target, propertyName);
        return value switch
        {
            uint typedValue => typedValue,
            int typedValue when typedValue >= 0 => (uint)typedValue,
            long typedValue when typedValue >= 0 => (uint)typedValue,
            string typedValue when uint.TryParse(typedValue, out uint parsedValue) => parsedValue,
            _ => 0,
        };
    }

    private static int? TryGetSuggestedDeviceId(IReadOnlyDictionary<string, string> entries)
    {
        foreach (string key in new[] { "device_id", "deviceId", "adapter_index", "adapterIndex", "adapter_id", "adapterId", "DxgiAdapterNumber", "dxgiAdapterNumber" })
        {
            if (entries.TryGetValue(key, out string? rawValue) && int.TryParse(rawValue, out int parsedValue))
                return parsedValue;
        }

        return null;
    }

    private static bool MatchesProviderName(string left, string right)
        => string.Equals(NormalizeProviderName(left), NormalizeProviderName(right), StringComparison.Ordinal);

    private static string NormalizeProviderName(string providerName)
        => providerName
            .Replace("ExecutionProvider", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
}

internal sealed record OnnxRuntimeCapabilitiesSnapshot(
    string? OrtVersion,
    IReadOnlyList<string> AvailableProviders,
    IReadOnlyList<OnnxRuntimeHardwareDeviceInfo> HardwareDevices,
    IReadOnlyList<OnnxRuntimeEpDeviceInfo> EpDevices,
    string? Error);

internal sealed record OnnxRuntimeHardwareDeviceInfo(
    string Type,
    string Vendor,
    uint VendorId,
    uint DeviceId,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record OnnxRuntimeEpDeviceInfo(
    string EpName,
    string EpVendor,
    int? SuggestedDeviceId,
    IReadOnlyDictionary<string, string> EpMetadata,
    IReadOnlyDictionary<string, string> EpOptions,
    OnnxRuntimeHardwareDeviceInfo HardwareDevice);