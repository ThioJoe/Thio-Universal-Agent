using Microsoft.ML.OnnxRuntime;

namespace Thio_Universal_Agent.AI_API.Onnx;

internal static class OnnxRuntimeCapabilities
{
    internal static OnnxRuntimeCapabilitiesSnapshot GetSnapshot()
    {
        try
        {
            OrtEnv env = OrtEnv.Instance();
            IReadOnlyList<OrtHardwareDevice> hardwareDevices = env.GetHardwareDevices();
            IReadOnlyList<OrtEpDevice> epDevices = env.GetEpDevices();

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

    private static OnnxRuntimeHardwareDeviceInfo ToHardwareDeviceInfo(OrtHardwareDevice device)
        => new(
            Type: device.Type.ToString(),
            Vendor: device.Vendor,
            VendorId: device.VendorId,
            DeviceId: device.DeviceId,
            Metadata: ToDictionary(device.Metadata));

    private static OnnxRuntimeEpDeviceInfo ToEpDeviceInfo(OrtEpDevice device)
    {
        Dictionary<string, string> epMetadata = ToDictionary(device.EpMetadata);
        Dictionary<string, string> epOptions = ToDictionary(device.EpOptions);
        OnnxRuntimeHardwareDeviceInfo hardwareDevice = ToHardwareDeviceInfo(device.HardwareDevice);

        int? suggestedDeviceId = TryGetSuggestedDeviceId(epOptions)
            ?? TryGetSuggestedDeviceId(epMetadata)
            ?? TryGetSuggestedDeviceId(hardwareDevice.Metadata);

        return new OnnxRuntimeEpDeviceInfo(
            EpName: device.EpName,
            EpVendor: device.EpVendor,
            SuggestedDeviceId: suggestedDeviceId,
            EpMetadata: epMetadata,
            EpOptions: epOptions,
            HardwareDevice: hardwareDevice);
    }

    private static Dictionary<string, string> ToDictionary(OrtKeyValuePairs? pairs)
    {
        if (pairs is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            pairs.Refresh();
            return new Dictionary<string, string>(pairs.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
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