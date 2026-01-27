using System;
using System.IO;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace AssetsManager.Utils
{
    public static class SvgUtils
    {
        /// <summary>
        /// Convierte un array de bytes SVG en un ImageSource nativo de WPF (DrawingImage).
        /// </summary>
        public static ImageSource LoadSvg(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            try
            {
                using (var stream = new MemoryStream(data))
                {
                    WpfDrawingSettings settings = new WpfDrawingSettings
                    {
                        IncludeRuntime = false,
                        TextAsGeometry = true
                    };

                    StreamSvgConverter converter = new StreamSvgConverter(settings);
                    
                    // SharpVectors necesita un stream de salida aunque no lo usemos para DrawingImage
                    using (MemoryStream dummyOutputStream = new MemoryStream())
                    {
                        converter.Convert(stream, dummyOutputStream);
                    }

                    if (converter.Drawing == null) return null;

                    DrawingImage drawingImage = new DrawingImage(converter.Drawing);
                    drawingImage.Freeze(); // Importante para rendimiento y acceso desde otros hilos
                    return drawingImage;
                }
            }
            catch (Exception)
            {
                // El error será capturado y logueado por el servicio que llame a esta utilidad
                return null;
            }
        }
    }
}
