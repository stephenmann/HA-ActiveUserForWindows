namespace HaActiveUser.Agent.Configuration;

/// <summary>
/// A misconfiguration the operator has to correct. Reported as a single actionable line rather than
/// a stack trace, because restarting will never fix it.
/// </summary>
public sealed class ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
