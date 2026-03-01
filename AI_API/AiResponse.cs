namespace Thio_Universal_Agent.AI_API;

/// <summary>Represents the result of an AI prompt call.</summary>
public record AiResponse(bool Success, string Text, string? ErrorMessage = null);
