using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace UvexAdv.Nina.Plugin;

/// <summary>Process-lifetime cooperative claims across worker Profiles. These
/// are not camera handles and cannot prevent an unrelated external application
/// from opening a driver; the physical handoff is still commissioned.</summary>
internal sealed class PhotometryDeviceClaims : IDisposable
{
    private readonly List<NamedPipeServerStream> claims = [];
    internal PhotometryDeviceClaims(string cameraId, string wheelId, string focuserId)
    {
        try
        {
            foreach (var pair in new[] { ("Camera", cameraId), ("Wheel", wheelId), ("Focuser", focuserId) })
            {
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Item2)));
                claims.Add(new NamedPipeServerStream($"OpenAstroSpec.Photometry.Owner.v1.{pair.Item1}.{key}",
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var claim in claims) claim.Dispose(); claims.Clear(); }
}
