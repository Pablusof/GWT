namespace GwtEmulator.Protocol;

public interface IRequest
{
    TkMsgType MsgType { get; }
    IRequest ReadByte(ByteArray byteArray);
    void WriteByte(ByteArray byteArray);
}

public abstract class BaseRequest : IRequest
{
    public TkMsgType MsgType { get; set; }

    public virtual IRequest ReadByte(ByteArray byteArray) => this;
    public virtual void WriteByte(ByteArray byteArray) { }
}
