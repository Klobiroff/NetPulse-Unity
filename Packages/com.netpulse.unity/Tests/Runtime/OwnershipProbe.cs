using System.Collections.Generic;

namespace NetPulse.Tests
{
    public sealed class OwnershipProbe : NetworkBehaviour
    {
        public List<Player> Requests { get; } = new();

        public int OwnerChanges { get; private set; }

        public override void OnOwnershipRequest(Player requester) => Requests.Add(requester);

        public override void OnOwnerChanged(Player previous, Player current) => OwnerChanges++;
    }
}
