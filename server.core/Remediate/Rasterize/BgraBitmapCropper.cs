namespace server.core.Remediate.Rasterize;

public static class BgraBitmapCropper
{
    public static BgraBitmap Crop(BgraBitmap source, IntRect rect)
    {
        if (!source.IsValid)
        {
            throw new ArgumentException("Source bitmap is invalid.", nameof(source));
        }

        if (rect.IsEmpty)
        {
            return new BgraBitmap(Array.Empty<byte>(), 0, 0, 0);
        }

        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > source.WidthPx || rect.Y + rect.Height > source.HeightPx)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "Crop rectangle must be within the source bitmap.");
        }

        var croppedStride = rect.Width * 4;
        var cropped = new byte[croppedStride * rect.Height];

        for (var row = 0; row < rect.Height; row++)
        {
            var srcOffset = ((rect.Y + row) * source.StrideBytes) + (rect.X * 4);
            var dstOffset = row * croppedStride;
            Buffer.BlockCopy(source.BgraBytes, srcOffset, cropped, dstOffset, croppedStride);
        }

        return new BgraBitmap(cropped, rect.Width, rect.Height, croppedStride);
    }
}

