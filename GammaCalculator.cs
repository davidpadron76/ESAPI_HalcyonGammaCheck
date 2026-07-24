using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace VMS.TPS
{
    public class GammaCalculator
    {
        private const double DTA_CRITERIA = 3.0; // mm
        private const double DOSE_CRITERIA_PERCENT = 0.03; // 3%
        private const double THRESHOLD_PERCENT = 0.10; // 10%
        private const double PASS_LIMIT = 95.0; // %

        // Cálculo puro sobre arrays ya extraídos (sin dependencias de ESAPI): seguro de llamar
        // desde un hilo de fondo y de paralelizar.
        public static AnalysisResult Evaluate(PortalDoseSnapshot imgRef, PortalDoseSnapshot imgEval, int sessionIdx)
        {
            string fieldId = imgRef.FieldId ?? imgEval.FieldId ?? "N/A";
            string dateStr = imgEval.Date;

            int sizeX = imgRef.XSize;
            int sizeY = imgRef.YSize;
            double resX = imgRef.XRes;

            if (imgEval.XSize != sizeX || imgEval.YSize != sizeY)
            {
                return new AnalysisResult
                {
                    FieldId = fieldId,
                    Date = dateStr,
                    SessionNumber = sessionIdx,
                    GammaPassRate = 0,
                    Status = "ERROR",
                    Details = $"Tamaño de imagen distinto (Ref: {sizeX}x{sizeY}, Eval: {imgEval.XSize}x{imgEval.YSize})"
                };
            }

            if (Math.Abs(imgEval.XRes - resX) > 1e-6 || Math.Abs(imgEval.YRes - imgRef.YRes) > 1e-6)
            {
                return new AnalysisResult
                {
                    FieldId = fieldId,
                    Date = dateStr,
                    SessionNumber = sessionIdx,
                    GammaPassRate = 0,
                    Status = "ERROR",
                    Details = $"Resolución distinta (Ref: {resX:F3}mm, Eval: {imgEval.XRes:F3}mm)"
                };
            }

            int[,] buffRef = imgRef.Voxels;
            int[,] buffEval = imgEval.Voxels;

            int maxDoseRef = 0;
            int maxDoseEval = 0;
            for (int x = 0; x < sizeX; x++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    if (buffRef[x, y] > maxDoseRef) maxDoseRef = buffRef[x, y];
                    if (buffEval[x, y] > maxDoseEval) maxDoseEval = buffEval[x, y];
                }
            }

            if (maxDoseRef <= 0) return new AnalysisResult { FieldId = fieldId, Date = dateStr, SessionNumber = sessionIdx, Status = "ERROR", Details = "Ref Vacía" };

            // Auto-Alineación (Using each image's own max dose for thresholding)
            Point comRef = GetCenterOfMass(buffRef, sizeX, sizeY, maxDoseRef * 0.2);
            Point comEval = GetCenterOfMass(buffEval, sizeX, sizeY, maxDoseEval * 0.2);

            int shiftX = (int)Math.Round(comRef.X - comEval.X);
            int shiftY = (int)Math.Round(comRef.Y - comEval.Y);

            // Gamma
            double doseTol = maxDoseRef * DOSE_CRITERIA_PERCENT;
            double distTolSq = DTA_CRITERIA * DTA_CRITERIA;
            double thresholdVal = maxDoseRef * THRESHOLD_PERCENT;
            int searchRadiusX = (int)Math.Ceiling(DTA_CRITERIA / resX) + 1;

            // El patrón de desplazamientos (dx, dy) a explorar es el mismo para todos los puntos
            // de la imagen (depende solo del radio y la resolución), así que se calcula una única
            // vez, ordenado por distancia ascendente, para poder cortar la búsqueda en cuanto un
            // punto cumple el criterio de gamma en vez de recorrer siempre toda la ventana.
            var searchOffsets = BuildSearchOffsets(searchRadiusX, resX, distTolSq);

            int pointsEvaluated = 0;
            int pointsPassed = 0;
            object sumLock = new object();

            // Cada fila (x) es independiente: se reparte entre hilos y se combinan los totales al final.
            Parallel.For(0, sizeX,
                () => (evaluated: 0, passed: 0),
                (x, loopState, local) =>
                {
                    int localEvaluated = local.evaluated;
                    int localPassed = local.passed;

                    for (int y = 0; y < sizeY; y++)
                    {
                        double dRef = buffRef[x, y];
                        if (dRef < thresholdVal) continue;

                        localEvaluated++;
                        int xEvalBase = x - shiftX;
                        int yEvalBase = y - shiftY;

                        if (IsInside(xEvalBase, yEvalBase, sizeX, sizeY) &&
                            Math.Abs(dRef - buffEval[xEvalBase, yEvalBase]) <= doseTol)
                        {
                            localPassed++;
                            continue;
                        }

                        bool passed = false;
                        foreach (var off in searchOffsets)
                        {
                            int i = xEvalBase + off.dx;
                            int j = yEvalBase + off.dy;
                            if (!IsInside(i, j, sizeX, sizeY)) continue;

                            double doseDiff = Math.Abs(dRef - buffEval[i, j]);
                            double gammaSq = (doseDiff * doseDiff) / (doseTol * doseTol) + off.distSqMm / distTolSq;

                            if (gammaSq <= 1.0) { passed = true; break; }
                        }
                        if (passed) localPassed++;
                    }

                    return (localEvaluated, localPassed);
                },
                local =>
                {
                    lock (sumLock)
                    {
                        pointsEvaluated += local.evaluated;
                        pointsPassed += local.passed;
                    }
                });

            double passRate = (double)pointsPassed / Math.Max(1, pointsEvaluated) * 100.0;

            return new AnalysisResult
            {
                FieldId = fieldId,
                Date = dateStr,
                SessionNumber = sessionIdx,
                GammaPassRate = passRate,
                Status = passRate >= PASS_LIMIT ? "APROBADO" : "FALLO",
                Details = $"Shift: {shiftX},{shiftY} px. Puntos: {pointsEvaluated}"
            };
        }

        private static (int dx, int dy, double distSqMm)[] BuildSearchOffsets(int radius, double res, double distTolSq)
        {
            var offsets = new List<(int dx, int dy, double distSqMm)>();
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    double distSqMm = (dx * res * dx * res) + (dy * res * dy * res);
                    if (distSqMm <= distTolSq) offsets.Add((dx, dy, distSqMm));
                }
            }
            return offsets.OrderBy(o => o.distSqMm).ToArray();
        }

        private static bool IsInside(int x, int y, int sx, int sy) => x >= 0 && x < sx && y >= 0 && y < sy;

        private static Point GetCenterOfMass(int[,] img, int sx, int sy, double thresh)
        {
            double sumW = 0, sumX = 0, sumY = 0;
            for (int x = 0; x < sx; x++)
            {
                for (int y = 0; y < sy; y++)
                {
                    double val = img[x, y];
                    if (val > thresh) { sumW += val; sumX += x * val; sumY += y * val; }
                }
            }
            return sumW == 0 ? new Point(sx / 2, sy / 2) : new Point(sumX / sumW, sumY / sumW);
        }
    }
}
