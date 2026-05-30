namespace Thio_Universal_Agent.AI_API.Onnx;

public enum OnnxExecutionProvider
{
    FollowConfig,
    CPU,
    DML,
    CUDA,
    OpenVINO,
    QNN,
    WebGPU,
    NvTensorRtRtx
}

/// <summary>Configuration for local ONNX Runtime GenAI models.</summary>
public class OnnxConfig : IAiProviderConfig
{
    public string ProviderName => "Local ONNX";

    // Kept for interface compatibility. Local ONNX models do not require an API key.
    public string? ApiKey { get; set; }

    [ConfigField("Model Folder", Description = "Path to a local ONNX Runtime GenAI model folder containing genai_config.json, tokenizer files, and any chat template assets.")]
    public string Model { get; set; } = string.Empty;

    [ConfigField("Execution Provider", Description = "Choose the execution provider to request. FollowConfig uses the model/default runtime selection. The ONNX section also reports which providers and devices the installed runtime actually sees on this machine.")]
    public OnnxExecutionProvider ExecutionProvider { get; set; } = OnnxExecutionProvider.FollowConfig;

    [ConfigField("Device ID", Description = "Optional device id for execution providers that support device selection. On Windows with DirectML, use the DxgiAdapterNumber reported in the ONNX capabilities list or device dropdown.")]
    public int? DeviceId { get; set; }

    [ConfigField("Use Sampling", Description = "Enable temperature / top-p / top-k sampling. Disable for greedy decoding.")]
    public bool UseSampling { get; set; }

    [ConfigField("Temperature", Description = "Sampling temperature when Use Sampling is enabled.")]
    public float? Temperature { get; set; }

    [ConfigField("Top P", Description = "Top-p nucleus sampling cutoff when Use Sampling is enabled.")]
    public float? TopP { get; set; }

    [ConfigField("Top K", Description = "Top-k sampling limit when Use Sampling is enabled.")]
    public int? TopK { get; set; }

    [ConfigField("Max Output Tokens", Description = "Maximum number of newly generated tokens to produce for each response.")]
    public int? MaxOutputTokens { get; set; } = 1024;

    [ConfigField("Model Type Override", Description = "Optional override when the model reports an unexpected type string. Usually leave blank.")]
    public string? ModelTypeOverride { get; set; }

    public double? InputPricePerMillionTokens { get; set; }

    public double? OutputPricePerMillionTokens { get; set; }

    public double? CachedInputPricePerMillionTokens { get; set; }

    public OnnxConfig() { }

    public OnnxConfig(IConfigurationSection section)
    {
        ConfigSectionBinder.Bind(this, section);
    }
}