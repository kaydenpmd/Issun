namespace Issun.Presentation;

/// <summary>Premultiplied BGRA pixels, top row first, as WinUI's bitmaps take them.</summary>
public sealed record Pixels(byte[] Bgra, int Width, int Height);
