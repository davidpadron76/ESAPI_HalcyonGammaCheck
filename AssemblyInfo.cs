using System.Reflection;
using VMS.TPS.Common.Model.API;

[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]

// Este script solo lee imágenes de dosimetría portal y exporta un CSV a disco local;
// no modifica ni guarda ningún dato del plan/paciente en Eclipse.
[assembly: ESAPIScript(IsWriteable = false)]
