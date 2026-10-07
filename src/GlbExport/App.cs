using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.IO;

namespace GlbExport
{
    /// <summary>Crea la pestana GLB con el boton GLB Export al iniciar Revit.</summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            const string tab = "GLB";
            try { app.CreateRibbonTab(tab); } catch (Exception) { /* la pestana ya existe */ }
            var panel = app.CreateRibbonPanel(tab, "Export");
            var data = new PushButtonData("GlbExport", "GLB\nExport", Assembly.GetExecutingAssembly().Location,
                                          typeof(ExportCommand).FullName)
            {
                ToolTip = "Exporta la vista 3D activa a un GLB optimizado para WebXR (Meta Quest), con instancias para elementos repetidos.",
                LongDescription = "Abre una vista 3D, ejecuta el comando, elige perfil y archivo de salida. Solo se exporta lo visible en la vista. " +
                                  "La exportacion toma unos minutos y Revit queda ocupado. Requiere Node.js y las dependencias que instala el instalador.",
            };
            try
            {
                var uri = new Uri("pack://application:,,,/GlbExport;component/Resources/icon.png");
                var img = new BitmapImage(uri);
                data.LargeImage = img;
                data.Image = img;
            }
            catch (Exception) { /* sin icono no pasa nada */ }
            panel.AddItem(data);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { TaskDialog.Show("GLB Export", "Abre un proyecto de Revit."); return Result.Cancelled; }
            var view = doc.ActiveView as View3D;
            if (view == null) { TaskDialog.Show("GLB Export", "Abre una vista 3D y vuelve a ejecutar."); return Result.Cancelled; }

            var node = NodeRunner.FindNode();
            var build = NodeRunner.FindBuildScript();
            if (node == null || build == null || !Directory.Exists(NodeRunner.DepsDir))
            {
                TaskDialog.Show("GLB Export", "Falta Node.js o sus dependencias.\n" +
                    "Ejecuta el instalador de GLB Export (o install.ps1) y reinicia Revit.");
                return Result.Failed;
            }

            var cats = SceneExporter.ViewCategories(doc, view);
            if (cats.Count == 0) { TaskDialog.Show("GLB Export", "La vista no tiene elementos de modelo visibles."); return Result.Cancelled; }

            var win = new ExportWindow(view.Name, cats);
            new System.Windows.Interop.WindowInteropHelper(win).Owner = data.Application.MainWindowHandle;
            win.ShowDialog();
            var cfg = win.Result;
            if (cfg == null) return Result.Cancelled;

            var tmp = Path.Combine(Path.GetTempPath(), "glbexport_" + Guid.NewGuid().ToString("N"));
            try
            {
                var stats = SceneExporter.ExportView(doc, view, tmp, null, cfg.Exclude, cfg.General.IncludeLinks);
                var summary = NodeRunner.Convert(node, build, tmp, cfg.Out, cfg.Rules);
                TaskDialog.Show("GLB Export", $"Listo.\n\n{cfg.Out}\n\nInstancias: {stats.Instances}\n" +
                    $"Triangulos sin optimizar: {stats.TrisRendered}\n\n{summary}");
                return Result.Succeeded;
            }
            catch (Exception e)
            {
                message = e.Message;
                TaskDialog.Show("GLB Export", "No se pudo exportar:\n" + e.Message);
                return Result.Failed;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (IOException) { }
            }
        }
    }
}
