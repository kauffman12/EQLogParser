using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  // Minimal TGA loader for uncompressed 24/32-bit images. Not a full TGA implementation.
  internal static class TgaLoader
  {
    public static BitmapSource Load(string path)
    {
      using var fs = File.OpenRead(path);
      using var br = new BinaryReader(fs);

      // Header fields
      var idLength = br.ReadByte();
      var colorMapType = br.ReadByte();
      var imageType = br.ReadByte();

      // Only support uncompressed true-color images (2)
      if (imageType != 2)
        throw new NotSupportedException("Only uncompressed true-color TGA images are supported.");

      // skip color map specification (5 bytes)
      br.ReadBytes(5);

      // image specification
      var xOrigin = br.ReadInt16();
      var yOrigin = br.ReadInt16();
      var width = br.ReadInt16();
      var height = br.ReadInt16();
      var pixelDepth = br.ReadByte();
      var descriptor = br.ReadByte();

      // skip id field
      if (idLength > 0) br.ReadBytes(idLength);

      var bytesPerPixel = pixelDepth / 8;
      if (bytesPerPixel < 3 || (bytesPerPixel != 3 && bytesPerPixel != 4))
        throw new NotSupportedException("Only 24-bit and 32-bit TGA images supported.");

      var stride = width * bytesPerPixel;
      var pixelData = br.ReadBytes(Math.Abs(stride) * height);

      // TGA stores in BGR(A) bottom-up by default. We'll convert to BGRA32 top-down.
      var outStride = width * 4;
      var outPixels = new byte[height * outStride];

      for (int y = 0; y < height; y++)
      {
        var srcRow = y * stride;
        var dstRow = (height - 1 - y) * outStride; // flip vertically
        for (int x = 0; x < width; x++)
        {
          var src = srcRow + x * bytesPerPixel;
          var dst = dstRow + x * 4;
          // B
          outPixels[dst + 0] = pixelData[src + 0];
          // G
          outPixels[dst + 1] = pixelData[src + 1];
          // R
          outPixels[dst + 2] = pixelData[src + 2];
          // A
          outPixels[dst + 3] = bytesPerPixel == 4 ? pixelData[src + 3] : (byte)255;
        }
      }

      var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, outPixels, outStride);
      bmp.Freeze();
      return bmp;
    }
  }
}
