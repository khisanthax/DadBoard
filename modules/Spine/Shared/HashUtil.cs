using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DadBoard.Spine.Shared;

public static class HashUtil
{
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
