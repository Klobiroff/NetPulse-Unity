using System;

namespace NetPulse
{
    public sealed class RoomKey<T>
    {
        public RoomKey(string name)
        {
            Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("Key name must not be empty.", nameof(name)) : name;
        }

        public string Name { get; }

        public override string ToString() => Name;
    }

    public sealed class PlayerKey<T>
    {
        public PlayerKey(string name)
        {
            Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("Key name must not be empty.", nameof(name)) : name;
        }

        public string Name { get; }

        public override string ToString() => Name;
    }
}
