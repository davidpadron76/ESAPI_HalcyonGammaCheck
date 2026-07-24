using System;
using System.IO;

namespace VMS.TPS
{
    // Registro mínimo de auditoría (qué análisis se ejecutaron y qué se exportó) para
    // trazabilidad institucional. Debe ser "best effort": un fallo al escribir el log
    // nunca debe interrumpir el flujo de análisis/exportación clínico.
    public static class ActivityLogger
    {
        private static readonly string LogDir = @"C:\Temp\PortalDosimetryReports\Logs";

        public static void Log(string message)
        {
            try
            {
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
                string file = Path.Combine(LogDir, $"HalcyonQA_Activity_{DateTime.Now:yyyyMM}.log");
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Environment.UserName}\t{message}";
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch
            {
                // Intencional: el registro de auditoría no debe bloquear el análisis clínico.
            }
        }
    }
}
