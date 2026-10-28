using System.Security.Cryptography;
using System.Text;

namespace AgenticWorkflow.Ingestion;

public static class ChunkIds
{
    /// <summary>
    /// base64url(SHA-256(sourcePath + "#" + chunkIndex)) (data-model 2.1). <paramref name="sourcePath"/> is the path
    /// relative to the knowledge root with '/' separators, so ids do not depend on the machine or the OS.
    /// </summary>
    public static string For(string sourcePath, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourcePath}#{chunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
