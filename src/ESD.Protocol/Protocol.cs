namespace ESD.Protocol;

public enum Command : ushort
{
    Heartbeat       = 0x0001,
    StatusReport    = 0x0002,
    WorkerScan      = 0x0010,
    Attach          = 0x0011,
    Detach          = 0x0012,
    EsdCheck        = 0x0013,
    GetInfo         = 0x0020,
    SetConfig       = 0x0021,
    AlarmTrigger    = 0x0030,
    AlarmClear      = 0x0031,
    LockStation     = 0x0032,
    UnlockStation   = 0x0033,
    LogUpload       = 0x0040,
    FirmwareUpdate  = 0x0050,
    Reboot          = 0x0051,
    Ack             = 0x00F0,
    Nack            = 0x00F1,
    Error           = 0x00FF,
}

[Flags]
public enum FrameFlags : byte { None = 0, AckRequested = 1, Encrypted = 2, Error = 4 }

public enum ErrorCode : byte
{
    Ok = 0, BadVersion = 1, BadLength = 2, BadCrc = 3, BadMac = 4,
    Replay = 5, UnknownCommand = 6, DeviceNotAuthorized = 7, Busy = 8, InvalidPayload = 9,
}

public enum AlarmReason : byte
{
    EsdWristbandFail = 0x01, EsdFootwearFail = 0x02,
    UnauthorizedWorker = 0x03, StationTimeout = 0x04, ManualTrigger = 0xFF,
}

public sealed record EsdFrame(
    byte Version, FrameFlags Flags, uint DeviceId, Command Command,
    uint Sequence, uint Timestamp, byte[] Payload, byte[] AuthTag);

public static class ProtocolConstants
{
    public const byte Sof1 = 0xAA, Sof2 = 0x55, Eof1 = 0x0D, Eof2 = 0x0A, Version = 1;
    public const int AuthTagSize = 16, MaxPayload = 256;
}
