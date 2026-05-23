namespace Thio_Universal_Agent;


public static class Globals
{
    /// <summary>
    /// Mirrors <see cref="AgentConfig.EnableDebugMode"/>. Set once at startup from config;
    /// read throughout the codebase as a fast static flag.
    /// </summary>
    internal static bool ENABLE_TESTING = false;

    /// <summary>
    /// Mirrors <see cref="GeneralConfig.MaxQueueSize"/>. Set once at startup from config;
    /// read by <see cref="Thio_Universal_Agent.Logic.AgentActionParser"/> and the prompt builder.
    /// </summary>
    internal static int MAX_QUEUE_SIZE = 5;
}
