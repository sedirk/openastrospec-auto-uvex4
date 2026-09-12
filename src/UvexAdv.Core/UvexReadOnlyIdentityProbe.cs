using System.Text.RegularExpressions;
using UvexAdv.Protocol;

namespace UvexAdv.Core;

public sealed record UvexSerialIdentity(string FirmwareVersion, string Description);

/// <summary>Only the two read-only identity queries; never initialization or motion.</summary>
public static class UvexReadOnlyIdentityProbe
{
    public static async Task<UvexSerialIdentity> ReadAsync(UvexProtocolSession session, CancellationToken cancellationToken)
    {
        var firmware = await session.SendAsync(UvexCommands.FirmwareVersion(), cancellationToken).ConfigureAwait(false);
        var description = await session.SendAsync(UvexCommands.Description(), cancellationToken).ConfigureAwait(false);
        return Validate(firmware, description);
    }

    public static UvexSerialIdentity Validate(UvexFrame? firmware, UvexFrame? description)
    {
        var version = firmware?.Arguments.FirstOrDefault();
        var name = description is null ? string.Empty : string.Join(' ', description.Arguments);
        if (firmware?.Code != "IVE1" || description?.Code != "IDE1" ||
            string.IsNullOrWhiteSpace(version) || version.Length > 64 || !char.IsDigit(version[0]) ||
            !Regex.IsMatch(name, @"\bUVEX\s*4i?\b", RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException("UVEX_SERIAL_IDENTITY_MISMATCH: The port did not return a UVEX4 firmware/description pair; no device control is authorized.");
        }

        return new UvexSerialIdentity(version, name);
    }
}
