using System.Collections.Concurrent;

namespace mobileappbackend1.Services
{
    /// <summary>
    /// Thread-safe, in-memory tracker for online users.
    /// Registered as a singleton so all hub instances share the same state.
    /// Tracks connection count per user so presence is only toggled on first connect / last disconnect.
    /// </summary>
    public class PresenceTracker
    {
        private readonly ConcurrentDictionary<string, HashSet<string>> _onlineUsers = new();
        private readonly object _lock = new();

        /// <summary>
        /// Adds a connection. Returns true if this is the user's FIRST connection (went online).
        /// </summary>
        public bool UserConnected(string userId, string connectionId)
        {
            lock (_lock)
            {
                if (!_onlineUsers.TryGetValue(userId, out var connections))
                {
                    connections = new HashSet<string>();
                    _onlineUsers[userId] = connections;
                }

                connections.Add(connectionId);
                return connections.Count == 1;
            }
        }

        /// <summary>
        /// Removes a connection. Returns true if this was the user's LAST connection (went offline).
        /// </summary>
        public bool UserDisconnected(string userId, string connectionId)
        {
            lock (_lock)
            {
                if (!_onlineUsers.TryGetValue(userId, out var connections))
                    return false;

                connections.Remove(connectionId);
                if (connections.Count == 0)
                {
                    _onlineUsers.TryRemove(userId, out _);
                    return true;
                }

                return false;
            }
        }

        public bool IsOnline(string userId)
        {
            return _onlineUsers.ContainsKey(userId);
        }

        public List<string> GetOnlineUsers()
        {
            lock (_lock)
            {
                return _onlineUsers.Keys.ToList();
            }
        }
    }
}
