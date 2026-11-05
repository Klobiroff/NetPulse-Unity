using System;
using UnityEngine.Scripting;
using NetPulse.Common;

namespace NetPulse
{
    public enum NetState
    {
        Disconnected,
        Connecting,
        Authenticating,
        Connected,
        InLobby,
        JoiningRoom,
        InRoom,
        Leaving,
        Disconnecting
    }

    public enum OwnershipMode
    {
        Fixed,
        Takeover,
        Request
    }

    public enum RpcTarget
    {
        All,
        Others,
        MasterClient,
        Owner,
        AllBuffered,
        OthersBuffered
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NetRpcAttribute : PreserveAttribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SyncedAttribute : PreserveAttribute
    {
    }

    public readonly struct NetRpcInfo
    {
        public NetRpcInfo(Player sender, int senderActorId, double serverTime)
        {
            Sender = sender;
            SenderActorId = senderActorId;
            ServerTime = serverTime;
        }

        public Player Sender { get; }

        public int SenderActorId { get; }

        public double ServerTime { get; }
    }

    public sealed class RaiseEventOptions
    {
        public ReceiverGroup Receivers { get; set; } = ReceiverGroup.Others;

        public int[] TargetActors { get; set; }

        public byte InterestGroup { get; set; }

        public CachingOption Caching { get; set; } = CachingOption.DoNotCache;

        public DeliveryMode Delivery { get; set; } = DeliveryMode.ReliableOrdered;

        public ObjectOwnershipMode OwnershipMode { get; set; }
    }
}
