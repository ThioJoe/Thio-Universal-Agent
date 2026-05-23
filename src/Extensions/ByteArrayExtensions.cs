namespace Thio_Universal_Agent.Extensions;

/// <summary>
/// Extension methods for <see cref="T:byte[]"/>.
/// </summary>
public static class ByteArrayExtensions
{
    /// <summary>
    /// Converts the byte array to a Base64-encoded string.
    /// </summary>
    public static string ToBase64(this byte[] bytes) => Convert.ToBase64String(bytes);

    /// <summary>
    /// Converts the byte array to a Base64-encoded data URI string suitable for
    /// embedding in HTML or sending to an AI vision API (e.g. <c>data:image/png;base64,…</c>).
    /// </summary>
    /// <param name="bytes">The raw image bytes.</param>
    /// <param name="mimeType">The MIME type of the image (defaults to <c>image/png</c>).</param>
    public static string ToBase64DataUri(this byte[] bytes, string mimeType = "image/png")
        => $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
}
