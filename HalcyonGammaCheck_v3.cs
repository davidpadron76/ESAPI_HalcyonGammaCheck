using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VMS.TPS.Common.Model.API; // ESAPI types (ensure project references VMS.TPS.Common.Model.API.dll)
using VMS.TPS.Common.Model.Types;

// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]

// TODO: Uncomment the following line if the script requires write access.
//[assembly: ESAPIScript(IsWriteable = false)]

namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context, System.Windows.Window window, ScriptEnvironment environment)
        {
            // TODO : Add here the code that is called when the script is launched from Eclipse.
            if (context.Patient == null)
            {
                MessageBox.Show("Por favor, carga un paciente.");
                return;
            }

            var view = new MainView(context.Patient);
            window.Content = view;
            window.Title = $"Halcyon PD Constancy Check - {context.Patient.Id} (All Fields)";
            window.Width = 1000;
            window.Height = 750;
        }
    }

    // -------------------------------------------------------------------------
    // LÓGICA GAMMA
    // -------------------------------------------------------------------------
    public class AnalysisResult
    {
        public string FieldId { get; set; }
        public string Date { get; set; }
        public int SessionNumber { get; set; }
        public double GammaPassRate { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
    }

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

    // -------------------------------------------------------------------------
    // INTERFAZ GRÁFICA (Con opción TODOS LOS CAMPOS)
    // -------------------------------------------------------------------------
    public class MainView : UserControl
    {
        private Patient _patient;
        private PlanSetup _currentPlan;

        // Controles
        private ComboBox _cbCourses;
        private ComboBox _cbPlans;
        private ComboBox _cbFields;
        private ComboBox _cbRefImage;
        private DataGrid _grid;
        private TextBlock _status;

        // Estado
        private List<PortalDoseImage> _singleFieldImages; // Solo para modo campo único
        private bool _isAllFieldsMode = false;

        public MainView(Patient p)
        {
            _patient = p;
            InitializeComponent();
            LoadCourses();
        }

        private void InitializeComponent()
        {
            this.Background = Brushes.White;
            var mainGrid = new Grid { Margin = new Thickness(15) };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new TextBlock { Text = "Halcyon PD Constancy Check", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.Teal, Margin = new Thickness(0, 0, 0, 15) };
            Grid.SetRow(header, 0); mainGrid.Children.Add(header);

            // Curso/Plan
            var panelCP = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            panelCP.Children.Add(new TextBlock { Text = "Curso:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            _cbCourses = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 20, 0) };
            _cbCourses.SelectionChanged += _cbCourses_SelectionChanged;
            panelCP.Children.Add(_cbCourses);

            panelCP.Children.Add(new TextBlock { Text = "Plan:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            _cbPlans = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 20, 0) };
            _cbPlans.SelectionChanged += _cbPlans_SelectionChanged;
            panelCP.Children.Add(_cbPlans);
            Grid.SetRow(panelCP, 1); mainGrid.Children.Add(panelCP);

            // Campo/Ref/Analizar
            var panelFR = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            panelFR.Children.Add(new TextBlock { Text = "Campo:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            _cbFields = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 20, 0) };
            _cbFields.SelectionChanged += _cbFields_SelectionChanged;
            panelFR.Children.Add(_cbFields);

            panelFR.Children.Add(new TextBlock { Text = "Ref (Sesión 1):", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            _cbRefImage = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 20, 0) };
            panelFR.Children.Add(_cbRefImage);

            var btnAnalyze = new Button { Content = "ANALIZAR", Width = 100, Background = Brushes.SteelBlue, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            btnAnalyze.Click += BtnAnalyze_Click;
            panelFR.Children.Add(btnAnalyze);
            Grid.SetRow(panelFR, 2); mainGrid.Children.Add(panelFR);

            // DataGrid
            _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Background = Brushes.WhiteSmoke };
            _grid.Columns.Add(new DataGridTextColumn { Header = "Campo", Binding = new Binding("FieldId"), Width = 80, FontWeight = FontWeights.SemiBold });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Fecha", Binding = new Binding("Date"), Width = 120 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("SessionNumber"), Width = 40 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Gamma (%)", Binding = new Binding("GammaPassRate") { StringFormat = "F2" }, Width = 90 });

            var colStatus = new DataGridTextColumn { Header = "Estado", Binding = new Binding("Status"), Width = 90, FontWeight = FontWeights.Bold };
            var styleStatus = new Style(typeof(DataGridCell));
            var trigPass = new DataTrigger { Binding = new Binding("Status"), Value = "APROBADO" };
            trigPass.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.LightGreen));
            var trigFail = new DataTrigger { Binding = new Binding("Status"), Value = "FALLO" };
            trigFail.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Salmon));
            styleStatus.Triggers.Add(trigPass); styleStatus.Triggers.Add(trigFail);
            colStatus.CellStyle = styleStatus;
            _grid.Columns.Add(colStatus);

            _grid.Columns.Add(new DataGridTextColumn { Header = "Detalles", Binding = new Binding("Details"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            Grid.SetRow(_grid, 3); mainGrid.Children.Add(_grid);

            // Footer
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            _status = new TextBlock { Text = "Listo.", Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
            footer.Children.Add(_status);
            var btnExport = new Button { Content = "Exportar CSV", Width = 120, Height = 25 };
            btnExport.Click += BtnExport_Click;
            footer.Children.Add(btnExport);
            Grid.SetRow(footer, 4); mainGrid.Children.Add(footer);

            this.Content = mainGrid;
        }

        // --- Carga de Datos ---

        private void LoadCourses()
        {
            _cbCourses.Items.Clear();
            foreach (var c in _patient.Courses) _cbCourses.Items.Add(c.Id);
            if (_cbCourses.Items.Count > 0) _cbCourses.SelectedIndex = 0;
        }

        private void _cbCourses_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _cbPlans.Items.Clear(); _cbFields.Items.Clear(); _cbRefImage.Items.Clear(); _currentPlan = null;
            if (_cbCourses.SelectedItem == null) return;

            var course = _patient.Courses.FirstOrDefault(c => c.Id == _cbCourses.SelectedItem.ToString());
            if (course != null)
                foreach (var p in course.PlanSetups) _cbPlans.Items.Add(p.Id);

            if (_cbPlans.Items.Count > 0) _cbPlans.SelectedIndex = 0;
        }

        private void _cbPlans_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _cbFields.Items.Clear(); _cbRefImage.Items.Clear(); _currentPlan = null;
            if (_cbCourses.SelectedItem == null || _cbPlans.SelectedItem == null) return;

            var course = _patient.Courses.FirstOrDefault(c => c.Id == _cbCourses.SelectedItem.ToString());
            _currentPlan = course?.PlanSetups.FirstOrDefault(p => p.Id == _cbPlans.SelectedItem.ToString());

            if (_currentPlan != null)
            {
                // AGREGAR OPCIÓN DE TODOS LOS CAMPOS
                _cbFields.Items.Add("-- ANALIZAR TODOS --");

                foreach (var b in _currentPlan.Beams)
                {
                    if (!b.IsSetupField) _cbFields.Items.Add(b.Id);
                }
            }
            if (_cbFields.Items.Count > 0) _cbFields.SelectedIndex = 0;
        }

        private void _cbFields_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _cbRefImage.Items.Clear();
            _singleFieldImages = new List<PortalDoseImage>();
            _isAllFieldsMode = false;
            _cbRefImage.IsEnabled = true; // Habilitar por defecto

            if (_currentPlan == null || _cbFields.SelectedItem == null) return;

            string selected = _cbFields.SelectedItem.ToString();

            // CASO: TODOS LOS CAMPOS
            if (selected == "-- ANALIZAR TODOS --")
            {
                _isAllFieldsMode = true;
                _cbRefImage.Items.Add("Automático (Sesión más antigua de cada campo)");
                _cbRefImage.SelectedIndex = 0;
                _cbRefImage.IsEnabled = false; // Deshabilitar selección manual
                return;
            }

            // CASO: CAMPO ÚNICO (Comportamiento original)
            var beam = _currentPlan.Beams.FirstOrDefault(b => b.Id == selected);
            if (beam != null)
            {
                foreach (var img in beam.PortalDoseImages) _singleFieldImages.Add(img);
                _singleFieldImages = _singleFieldImages.OrderBy(i => i.CreationDateTime).ToList();

                foreach (var img in _singleFieldImages)
                {
                    string dateStr = img.CreationDateTime.HasValue ? img.CreationDateTime.Value.ToString("g") : "N/A";
                    _cbRefImage.Items.Add($"{dateStr} [{img.Id}]");
                }
                if (_cbRefImage.Items.Count > 0) _cbRefImage.SelectedIndex = 0;
            }
        }

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var btnAnalyze = sender as Button;
            if (btnAnalyze != null) btnAnalyze.IsEnabled = false;
            _status.Text = "Leyendo imágenes...";
            _grid.ItemsSource = null; // Limpiar

            try
            {
                // Preparar los trabajos a evaluar. Esto SÍ toca objetos ESAPI (PortalDoseImage,
                // Beam, etc.), así que debe ejecutarse en el hilo de UI, pero solo copia los
                // voxeles a memoria — no calcula gamma, por lo que es rápido.
                var jobs = new List<(PortalDoseSnapshot refImg, PortalDoseSnapshot evalImg, int session, string fieldId)>();

                if (_isAllFieldsMode)
                {
                    // LÓGICA PARA TODOS LOS CAMPOS
                    if (_currentPlan == null) return;

                    foreach (var beam in _currentPlan.Beams)
                    {
                        if (beam.IsSetupField) continue;

                        // Obtener imágenes del campo actual
                        var fieldImages = new List<PortalDoseImage>();
                        foreach (var img in beam.PortalDoseImages) fieldImages.Add(img);

                        // Necesitamos al menos 2 imágenes
                        if (fieldImages.Count < 2) continue;

                        // Ordenar y tomar la primera como Referencia Automática
                        fieldImages = fieldImages.OrderBy(i => i.CreationDateTime).ToList();
                        var refSnap = PortalDoseSnapshot.From(fieldImages[0], beam.Id);

                        // Comparar el resto
                        int counter = 1;
                        for (int i = 1; i < fieldImages.Count; i++) // Empezar en 1 (saltar Ref)
                        {
                            var evalSnap = PortalDoseSnapshot.From(fieldImages[i], beam.Id);
                            jobs.Add((refSnap, evalSnap, counter++, beam.Id));
                        }
                    }

                    if (jobs.Count == 0)
                    {
                        MessageBox.Show("No se encontraron suficientes imágenes en los campos del plan.");
                        return;
                    }
                }
                else
                {
                    // LÓGICA PARA UN SOLO CAMPO
                    if (_singleFieldImages == null || _singleFieldImages.Count < 2)
                    {
                        MessageBox.Show("Se necesitan al menos 2 imágenes para comparar.");
                        return;
                    }

                    int refIdx = _cbRefImage.SelectedIndex;
                    if (refIdx < 0) return;

                    string fieldId = _cbFields.SelectedItem?.ToString();
                    var refSnap = PortalDoseSnapshot.From(_singleFieldImages[refIdx], fieldId);

                    int counter = 1;
                    for (int i = 0; i < _singleFieldImages.Count; i++)
                    {
                        if (i == refIdx) continue;
                        var evalSnap = PortalDoseSnapshot.From(_singleFieldImages[i], fieldId);
                        jobs.Add((refSnap, evalSnap, counter++, fieldId));
                    }
                }

                // El cálculo de gamma es matemática pura sobre arrays ya copiados: se ejecuta en
                // un hilo de fondo para no congelar la UI, reportando avance por cada comparación.
                var progress = new Progress<int>(done => _status.Text = $"Procesando... {done}/{jobs.Count}");
                var results = await Task.Run(() => RunJobs(jobs, progress));

                _grid.ItemsSource = results;
                _status.Text = $"Completado: {results.Count} análisis.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
                _status.Text = "Error.";
            }
            finally
            {
                if (btnAnalyze != null) btnAnalyze.IsEnabled = true;
            }
        }

        private static List<AnalysisResult> RunJobs(
            List<(PortalDoseSnapshot refImg, PortalDoseSnapshot evalImg, int session, string fieldId)> jobs,
            IProgress<int> progress)
        {
            var results = new List<AnalysisResult>(jobs.Count);

            for (int idx = 0; idx < jobs.Count; idx++)
            {
                var job = jobs[idx];
                try
                {
                    results.Add(GammaCalculator.Evaluate(job.refImg, job.evalImg, job.session));
                }
                catch (Exception exField)
                {
                    results.Add(new AnalysisResult
                    {
                        FieldId = job.fieldId,
                        Date = job.evalImg.Date,
                        SessionNumber = job.session,
                        GammaPassRate = 0,
                        Status = "ERROR",
                        Details = exField.Message
                    });
                }
                progress.Report(idx + 1);
            }

            return results;
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var data = _grid.ItemsSource as List<AnalysisResult>;
            if (data == null || !data.Any()) 
            {
                MessageBox.Show("No hay datos para exportar. Ejecute un análisis primero.");
                return;
            }

            string path = @"C:\Temp\PortalDosimetryReports";
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            string fieldName = _isAllFieldsMode ? "ALL_FIELDS" : _cbFields.SelectedItem.ToString();
            
            // Sanitizar Patient ID para evitar caracteres ilegales en el nombre del archivo
            string safePatientId = string.Join("_", _patient.Id.Split(Path.GetInvalidFileNameChars()));
            string safePlanId = _currentPlan != null ? string.Join("_", _currentPlan.Id.Split(Path.GetInvalidFileNameChars())) : "NoPlan";

            string file = Path.Combine(path, $"HalcyonQA_{safePatientId}_{safePlanId}_{fieldName}_{DateTime.Now:yyyyMMdd_HHmm}.csv");

            try
            {
                using (StreamWriter sw = new StreamWriter(file))
                {
                    sw.WriteLine("Paciente,Curso,Plan,Campo,Fecha,Sesion,Gamma,Estado,Detalles");
                    foreach (var r in data)
                    {
                        var fields = new[]
                        {
                            CsvField(_patient.Id),
                            CsvField(_cbCourses.SelectedItem?.ToString()),
                            CsvField(safePlanId),
                            CsvField(r.FieldId),
                            CsvField(r.Date),
                            CsvField(r.SessionNumber.ToString(CultureInfo.InvariantCulture)),
                            CsvField(r.GammaPassRate.ToString("F2", CultureInfo.InvariantCulture)),
                            CsvField(r.Status),
                            CsvField(r.Details)
                        };
                        sw.WriteLine(string.Join(",", fields));
                    }
                }
                MessageBox.Show($"Exportado a:\n{file}");
            }
            catch (Exception ex) { MessageBox.Show("Error exportando: " + ex.Message); }
        }

        // Escapa un campo para CSV (RFC 4180): entrecomilla si contiene coma, comilla o salto de línea.
        private static string CsvField(string value)
        {
            string s = value ?? string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }
    }
}