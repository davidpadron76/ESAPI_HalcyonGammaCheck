# ✅ ESAPI Halcyon Gamma Check

## 📖 Overview
The **ESAPI Halcyon Gamma Check** is an open-source ESAPI plugin script for the Varian Eclipse Treatment Planning System (TPS).

For a Halcyon treatment plan, it performs a **portal dosimetry (EPID) gamma constancy check across sessions**: for a selected field — or every field in the plan at once — it compares each delivered `PortalDoseImage` against a reference image (typically the first session) using a 2D gamma index, with automatic center-of-mass alignment, flags sessions below the institutional pass rate, and exports the results to CSV for the QA record.

## ✨ Key Features
* **Portal Dosimetry Gamma Analysis:** 2D gamma index (3%/3mm, global normalization, 10% low-dose threshold by default) between a reference `PortalDoseImage` and each subsequent session, with automatic center-of-mass alignment.
* **Single-Field or All-Fields Batch Mode:** Analyze one field manually, or run `-- ANALIZAR TODOS --` to auto-compare every treatment field in the plan against its own earliest session.
* **Responsive UI:** Image reading is synchronous, but the gamma computation runs on a background thread (parallelized per image row) with live progress in the status bar, so the window never freezes during a batch analysis.
* **Per-Comparison Error Isolation:** A problem with one session/field (mismatched image size or resolution, corrupted image, etc.) is reported as an `ERROR` row instead of aborting the whole batch.
* **CSV Export:** Save results via a standard Save dialog; fields are properly escaped and numbers use culture-invariant formatting, so the report opens cleanly in Excel regardless of the OS locale.
* **Minimal Audit Log:** Every analysis run and export is appended to a local log file (patient, plan, field, pass/fail/error counts, user, timestamp) for institutional traceability.

## 💻 System Requirements
* **Eclipse TPS / ESAPI:** Built against `VMS.TPS.Common.Model.API` v1.0.600.194 (Varian RTM 18.0). If your clinic runs a different ESAPI version, update the reference `HintPath`s in `HalcyonGammaCheck_v3.csproj` accordingly.
* **.NET Framework:** 4.8 (as configured in `HalcyonGammaCheck_v3.csproj`).

## 🗂️ Project Structure
| File | Responsibility |
|---|---|
| `Script.cs` | ESAPI entry point (`Script.Execute`), launched by Eclipse's Script Runner. |
| `MainView.cs` | WPF UI: course/plan/field selection, analysis grid, CSV export. |
| `GammaCalculator.cs` | Pure gamma-index math — no ESAPI dependency, safe to run on a background thread or unit test. |
| `PortalDoseSnapshot.cs` | Immutable copy of a `PortalDoseImage`'s voxels/metadata, used to hand data from the UI thread to the background computation. |
| `AnalysisResult.cs` | Result row shown in the grid and exported to CSV. |
| `ActivityLogger.cs` | Best-effort audit logging. |
| `AssemblyInfo.cs` | Assembly version and `[ESAPIScript(IsWriteable = false)]`. |

## 🛠️ Installation & Compilation
To ensure proper functionality within the Eclipse environment, this project must be compiled into a `.dll` library.

1. Clone or download this repository to your local machine.
2. Open the solution file (`.sln`) using **Visual Studio**.
3. Verify the `VMS.TPS.Common.Model.API` / `VMS.TPS.Common.Model.Types` reference paths in `HalcyonGammaCheck_v3.csproj` point to your Eclipse/ESAPI installation.
4. Build the solution (`Ctrl + Shift + B` or `Build > Build Solution`).
5. Locate the compiled `.dll` file inside the `bin\Debug` or `bin\Release` folder.
6. In Eclipse, open the Script Runner, navigate to the folder containing your compiled `.dll`, and execute it.

## 🚀 How to Use
1. Open a Patient in Eclipse and run the compiled Halcyon Gamma Check `.dll`.
2. Select the **Course**, **Plan**, and either a single **Field** or `-- ANALIZAR TODOS --` to compare every field in the plan.
3. For single-field mode, choose the reference session (defaults to session 1); in all-fields mode, the earliest session of each field is used automatically.
4. Click **ANALIZAR** and watch the status bar for progress. Results appear in the grid, color-coded `APROBADO` / `FALLO` / `ERROR`.
5. Click **Exportar CSV** and choose where to save the report (defaults to `C:\Temp\PortalDosimetryReports`).
6. Ensure all metrics pass institutional protocols before treating the patient on the Halcyon system.

## 📄 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## ⚠️ Clinical Disclaimer
**For Research and Educational Purposes Only.** This software is provided "as is", without warranty of any kind. It is the sole responsibility of the clinical user (Medical Physicist or Dosimetrist) to strictly verify and validate all plan parameters, QA results, and dosimetric data before approving a patient's treatment plan for clinical delivery on the Halcyon system.
