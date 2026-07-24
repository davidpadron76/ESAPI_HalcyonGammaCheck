using VMS.TPS.Common.Model.API;

namespace VMS.TPS
{
    // Copia inmutable de los datos de una PortalDoseImage necesarios para el cálculo de gamma.
    // Se construye en el hilo de UI (donde es seguro llamar a ESAPI) para que el cálculo puro
    // pueda ejecutarse después en un hilo de fondo sin volver a tocar objetos ESAPI.
    public class PortalDoseSnapshot
    {
        public string FieldId { get; set; }
        public string Date { get; set; }
        public int[,] Voxels { get; set; }
        public int XSize { get; set; }
        public int YSize { get; set; }
        public double XRes { get; set; }
        public double YRes { get; set; }

        public static PortalDoseSnapshot From(PortalDoseImage img, string fieldIdOverride = null)
        {
            int sx = img.XSize;
            int sy = img.YSize;
            var buff = new int[sx, sy];
            img.GetVoxels(0, buff);

            return new PortalDoseSnapshot
            {
                FieldId = fieldIdOverride ?? img.Beam?.Id,
                Date = img.CreationDateTime.HasValue ? img.CreationDateTime.Value.ToShortDateString() : "N/A",
                Voxels = buff,
                XSize = sx,
                YSize = sy,
                XRes = img.XRes,
                YRes = img.YRes
            };
        }
    }
}
