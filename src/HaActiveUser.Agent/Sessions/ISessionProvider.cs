namespace HaActiveUser.Agent.Sessions;

public interface ISessionProvider
{
    IReadOnlyList<SessionSnapshot> GetSessions();
}
