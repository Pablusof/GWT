using System;

namespace GwtEmulator.Protocol;

[AttributeUsage(AttributeTargets.Class)]
public class MessagePortalAttribute : Attribute
{
    public TkMsgType PortalId { get; }

    public MessagePortalAttribute(TkMsgType pid) => PortalId = pid;
}
