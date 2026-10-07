using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GlbExport
{
    /// <summary>Resultado del dialogo: destino, reglas para build.cjs y categorias excluidas.</summary>
    public sealed class ExportConfig
    {
        public string Out;
        public General General;
        public Dictionary<string, object> Rules;
        public HashSet<string> Exclude;
    }

    public partial class ExportWindow : Window
    {
        private readonly List<(string Name, string Ost, int Count)> _categories;
        private readonly Dictionary<string, CatRow> _saved;
        private List<CatRow> _rows;
        private bool _loading = true;

        public ExportConfig Result { get; private set; }

        public ExportWindow(string viewName, List<(string Name, string Ost, int Count)> categories)
        {
            InitializeComponent();
            _categories = categories;
            var profile = Options.LoadProfile();
            _saved = profile.Rows;
            var g = profile.General;

            view_txt.Text = "Vista: " + viewName;
            var safe = string.Concat(viewName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            out_txt.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), safe + ".glb");
            foreach (var name in Options.PresetNames) preset_cb.Items.Add(name);
            preset_cb.SelectedItem = profile.Preset;
            inst_chk.IsChecked = g.UseInstances;
            center_chk.IsChecked = g.Recenter;
            links_chk.IsChecked = g.IncludeLinks;
            mintris_txt.Text = ((int)g.MinTris).ToString();
            onesided_chk.IsChecked = g.OneSided;
            vcolor_chk.IsChecked = g.VertexColors;
            cell_txt.Text = ((int)g.CellM).ToString();
            Fill(profile.Preset, _saved);
            _loading = false;
        }

        private void Fill(string preset, Dictionary<string, CatRow> saved = null)
        {
            _rows = Options.MakeRows(preset, _categories, saved);
            cat_grid.ItemsSource = _rows;
        }

        private void Preset_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || preset_cb.SelectedItem == null) return;
            Fill((string)preset_cb.SelectedItem);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "GLB (*.glb)|*.glb", DefaultExt = "glb",
                FileName = Path.GetFileName(out_txt.Text),
                InitialDirectory = Path.GetDirectoryName(out_txt.Text),
            };
            if (dlg.ShowDialog(this) == true) out_txt.Text = dlg.FileName;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            cat_grid.CommitEdit(DataGridEditingUnit.Row, true);
            var path = out_txt.Text.Trim();
            var dir = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "Elige una carpeta de destino que exista.", "GLB Export");
                return;
            }
            if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) path += ".glb";
            foreach (var r in _rows)
            {
                r.Ratio = Options.Clamp(r.Ratio, 1, 100);
                r.ErrorCm = Options.Clamp(r.ErrorCm, 0.1, 100);
            }
            var general = new General
            {
                UseInstances = inst_chk.IsChecked == true,
                Recenter = center_chk.IsChecked == true,
                IncludeLinks = links_chk.IsChecked == true,
                OneSided = onesided_chk.IsChecked == true,
                VertexColors = vcolor_chk.IsChecked == true,
                CellM = Options.Clamp(cell_txt.Text, 0, 500, 0),
                MinTris = Options.Clamp(mintris_txt.Text, 0, 100000, 300),
            };
            try { Options.SaveProfile((string)preset_cb.SelectedItem, general, _rows); } catch (IOException) { }
            Result = new ExportConfig
            {
                Out = path, General = general,
                Rules = Options.BuildRules(_rows, general), Exclude = Options.ExcludedNames(_rows),
            };
            Close();
        }
    }
}
