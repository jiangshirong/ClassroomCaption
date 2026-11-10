namespace ClassroomCaption.Core;

public sealed record ApiCredentials(string AliyunKey)
{
    public bool Complete => !string.IsNullOrWhiteSpace(AliyunKey);
    public override string ToString() => "ApiCredentials [REDACTED]";
}
