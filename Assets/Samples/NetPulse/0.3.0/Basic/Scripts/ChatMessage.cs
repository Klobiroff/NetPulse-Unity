using NetPulse;

namespace NetPulse.Samples.Basic
{
    public partial struct ChatMessage : INetMessage
    {
        public string Text;
        public int Number;
    }
}
