namespace Thio_Universal_Agent;


public static class Globals
{
    /// <summary>
    /// Mirrors <see cref="AgentConfig.EnableDebugMode"/>. Set once at startup from config;
    /// read throughout the codebase as a fast static flag.
    /// </summary>
    internal static bool ENABLE_TESTING = false;
}
