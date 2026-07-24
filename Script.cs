using System.Runtime.CompilerServices;
using System.Windows;
using VMS.TPS.Common.Model.API;

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
}
