namespace VMS.TPS
{
    public class AnalysisResult
    {
        public string FieldId { get; set; }
        public string Date { get; set; }
        public int SessionNumber { get; set; }
        public double GammaPassRate { get; set; }
        public string Status { get; set; }
        public string Details { get; set; }
    }
}
